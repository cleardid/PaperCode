using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Windows;

using DynaOrchestrator.Core.Batch;
using DynaOrchestrator.Core.Models;

namespace DynaOrchestrator.Desktop.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        // 全局基础配置
        private AppConfig _baseConfig = new AppConfig();

        // 示例集合
        public ObservableCollection<BatchCaseRecord> Cases { get; set; } = new ObservableCollection<BatchCaseRecord>();
        // 日志集合
        public ObservableCollection<LogMessage> Logs { get; set; } = new ObservableCollection<LogMessage>();

        // 取消令牌
        private CancellationTokenSource? _cts;
        // 配置文件信息
        private readonly string _configPath = "config.json";

        // --- 动态读取 Config 中的路径配置 ---
        public string WorkspaceRootDir
        {
            get => _baseConfig.Workspace.RootDir;
            set { _baseConfig.Workspace.RootDir = value; OnPropertyChanged(); }
        }

        public string CasesCsvPath
        {
            get => _baseConfig.Workspace.CasesCsv;
            set { _baseConfig.Workspace.CasesCsv = value; OnPropertyChanged(); }
        }

        // --- 绑定到 UI 的全局执行参数 ---
        public int MaxParallelCases
        {
            get => _baseConfig.Workspace.MaxParallelCases;
            set { _baseConfig.Workspace.MaxParallelCases = value; OnPropertyChanged(); }
        }

        public int NcpuPerCase
        {
            get => _baseConfig.Workspace.NcpuPerCase;
            set { _baseConfig.Workspace.NcpuPerCase = value; OnPropertyChanged(); }
        }

        public string MemoryPerCase
        {
            get => _baseConfig.Workspace.MemoryPerCase;
            set { _baseConfig.Workspace.MemoryPerCase = value; OnPropertyChanged(); }
        }

        public bool EnablePreProcessing
        {
            get => _baseConfig.Pipeline.EnablePreProcessing;
            set { _baseConfig.Pipeline.EnablePreProcessing = value; OnPropertyChanged(); SaveBaseConfig(); }
        }

        public bool EnableSimulation
        {
            get => _baseConfig.Pipeline.EnableSimulation;
            set { _baseConfig.Pipeline.EnableSimulation = value; OnPropertyChanged(); SaveBaseConfig(); }
        }

        public bool EnablePostProcessing
        {
            get => _baseConfig.Pipeline.EnablePostProcessing;
            set { _baseConfig.Pipeline.EnablePostProcessing = value; OnPropertyChanged(); SaveBaseConfig(); }
        }

        private bool _isRunning;
        /// <summary>
        ///  用于更新按钮状态
        /// </summary>
        public bool IsRunning
        {
            get => _isRunning;
            set { _isRunning = value; OnPropertyChanged(); }
        }

        public RelayCommand StartCommand { get; }
        public RelayCommand StopCommand { get; }
        public RelayCommand LoadCsvCommand { get; }

        public MainViewModel()
        {
            StartCommand = new RelayCommand(StartBatch, () => !IsRunning && Cases.Count > 0);
            StopCommand = new RelayCommand(StopBatch, () => IsRunning);
            LoadCsvCommand = new RelayCommand(LoadCsv, () => !IsRunning);

            // 构造函数中优先加载全局配置
            LoadBaseConfig();
        }

        /// <summary>
        /// 加载全局配置信息
        /// </summary>
        private void LoadBaseConfig()
        {
            // 获取可执行文件所在目录
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            // 拼接全局配置文件路径（配置文件默认与可执行文件在同一目录）
            string fullConfigPath = Path.Combine(baseDir, _configPath);

            if (File.Exists(fullConfigPath))
            {
                try
                {
                    string json = File.ReadAllText(fullConfigPath);
                    _baseConfig = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                    AppendLog($"[UI] 成功加载全局配置文件: {fullConfigPath}");
                }
                catch (Exception ex)
                {
                    AppendLog($"[Error] 配置文件解析失败，使用默认配置: {ex.Message}");
                }
            }
            else
            {
                AppendLog($"[Warning] 未找到全局配置文件，将使用程序内置默认配置。");
            }

            // 触发属性变更，让 UI 文本框显示从 Config 读取到的路径与参数
            OnPropertyChanged(nameof(WorkspaceRootDir));
            OnPropertyChanged(nameof(CasesCsvPath));
            OnPropertyChanged(nameof(MaxParallelCases));
            OnPropertyChanged(nameof(NcpuPerCase));
            OnPropertyChanged(nameof(MemoryPerCase));
            OnPropertyChanged(nameof(EnablePreProcessing));
            OnPropertyChanged(nameof(EnableSimulation));
            OnPropertyChanged(nameof(EnablePostProcessing));
        }

        /// <summary>
        /// 同步保存当前的基础配置到 config.json
        /// </summary>
        private void SaveBaseConfig()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string fullConfigPath = Path.Combine(baseDir, _configPath);
                string json = JsonSerializer.Serialize(_baseConfig, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(fullConfigPath, json);
            }
            catch (Exception ex)
            {
                AppendLog($"[Error] 同步保存配置文件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载 CSV 文件，并更新 Cases 集合
        /// </summary>
        private void LoadCsv()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;

                // 增加空值保护
                string safeRootDir = string.IsNullOrWhiteSpace(WorkspaceRootDir) ? "." : WorkspaceRootDir;
                string safeCsvPath = string.IsNullOrWhiteSpace(CasesCsvPath) ? "cases.csv" : CasesCsvPath;

                string workspaceRoot = Path.GetFullPath(Path.Combine(baseDir, safeRootDir));
                string fullCsvPath = Path.GetFullPath(Path.Combine(workspaceRoot, safeCsvPath));

                // 显式拦截文件不存在的情况
                if (!File.Exists(fullCsvPath))
                {
                    AppendLog($"[Error] CSV 文件不存在，请检查以下路径是否正确:\n{fullCsvPath}");
                    return;
                }

                AppendLog($"[UI] 正在读取 CSV 文件: {fullCsvPath}");

                var records = CaseCsvReader.Read(fullCsvPath);
                Cases.Clear();
                foreach (var r in records)
                {
                    // --- 状态重置逻辑 ---
                    // 1. 如果上次退出时任务显示为 Running，说明执行被非正常中断，需重置为 Pending
                    // 2. 如果任务状态为 Failed，通常需要重新跑，也重置为 Pending
                    if (r.Status == "Running" || r.Status == "Failed")
                    {
                        r.Status = "Pending";
                    }

                    Cases.Add(r);
                }
                AppendLog($"[UI] 成功加载 {Cases.Count} 条记录。");
            }
            catch (Exception ex)
            {
                AppendLog($"[Error] 加载 CSV 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 启动批处理任务
        /// </summary>
        private async void StartBatch()
        {
            // --- 1. 运行前状态重置 ---
            // 无论是因为点击了“停止”，还是之前的残留错误
            // 我们将所有非 Success 的任务（Running, Failed, Canceled）统一重置为 Pending
            foreach (var record in Cases)
            {
                if (record.Status == "Running" || record.Status == "Failed" || record.Status == "Canceled")
                {
                    record.Status = "Pending";
                }
            }

            // --- 2. 启动执行流程 ---
            IsRunning = true;
            _cts = new CancellationTokenSource();
            Logs.Clear();
            AppendLog("[UI] 批处理任务已启动...");

            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                // 在启动批处理任务之前，LoadCsv 方法中已经进行空值检查，因此这里不再重复检查
                string workspaceRoot = Path.GetFullPath(Path.Combine(baseDir, WorkspaceRootDir));
                string fullCsvPath = Path.GetFullPath(Path.Combine(workspaceRoot, CasesCsvPath));

                // 由 BatchRunner 内部识别 Status 进行过滤，即仅执行状态为 Pending 的记录
                var recordList = new List<BatchCaseRecord>(Cases);

                await Task.Run(() =>
                {
                    return BatchRunner.RunAsync(
                        batchRoot: workspaceRoot,
                        casesCsvPath: fullCsvPath,
                        baseConfig: _baseConfig,
                        maxParallelCases: MaxParallelCases,
                        ncpuPerCase: NcpuPerCase,
                        memoryPerCase: MemoryPerCase,
                        records: recordList,
                        logger: msg => Application.Current.Dispatcher.Invoke(() => AppendLog(msg)),
                        uiStateUpdater: (record, status, completed, lastRunTime) =>
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                record.Status = status;
                                record.Completed = completed;
                                record.LastRunTime = lastRunTime;
                            });
                        },
                        cancellationToken: _cts.Token
                    );
                });
            }
            catch (Exception ex)
            {
                AppendLog($"[Error] 批处理过程中发生异常: {ex.Message}");
            }
            finally
            {
                IsRunning = false;
                AppendLog("[UI] 批处理执行周期已结束。");
            }
        }

        private void StopBatch()
        {
            AppendLog("[UI] 用户请求立即停止任务，准备终止所有正在运行的 LS-DYNA 进程...");

            // 先发出取消信号，阻止后续新任务继续进入执行阶段
            _cts?.Cancel();

            // 再直接杀死所有已经启动的求解器进程
            BatchRunner.ForceStopAllRunningCases(msg => AppendLog(msg));
        }

        private void AppendLog(string message)
        {
            var log = new LogMessage { Message = message };
            Logs.Add(log);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}