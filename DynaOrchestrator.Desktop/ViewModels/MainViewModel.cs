using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Windows;

// 引入更新后的核心库命名空间
using DynaOrchestrator.Core.Batch;
using DynaOrchestrator.Core.Models;

namespace DynaOrchestrator.Desktop.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        // 全局基础配置
        private AppConfig _baseConfig = new AppConfig();

        public ObservableCollection<BatchCaseRecord> Cases { get; set; } = new ObservableCollection<BatchCaseRecord>();
        public ObservableCollection<LogMessage> Logs { get; set; } = new ObservableCollection<LogMessage>();

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

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            set { _isRunning = value; OnPropertyChanged(); }
        }

        public RelayCommand StartCommand { get; }
        public RelayCommand StopCommand { get; }
        public RelayCommand LoadCsvCommand { get; }

        private CancellationTokenSource? _cts;
        private readonly string _configPath = "config.json";

        public MainViewModel()
        {
            StartCommand = new RelayCommand(StartBatch, () => !IsRunning && Cases.Count > 0);
            StopCommand = new RelayCommand(StopBatch, () => IsRunning);
            LoadCsvCommand = new RelayCommand(LoadCsv, () => !IsRunning);

            // 构造函数中优先加载全局配置
            LoadBaseConfig();
        }

        private void LoadBaseConfig()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
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
            OnPropertyChanged(nameof(MaxParallelCases));
            OnPropertyChanged(nameof(NcpuPerCase));
            OnPropertyChanged(nameof(WorkspaceRootDir));
            OnPropertyChanged(nameof(CasesCsvPath));
        }

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
                string workspaceRoot = Path.GetFullPath(Path.Combine(baseDir, WorkspaceRootDir));
                string fullCsvPath = Path.GetFullPath(Path.Combine(workspaceRoot, CasesCsvPath));

                // 仅将状态为 Pending 的记录提交给 BatchRunner 
                // 或者由 BatchRunner 内部识别 Status 进行过滤
                var recordList = new System.Collections.Generic.List<BatchCaseRecord>(Cases);

                await Task.Run(() =>
                {
                    return BatchRunner.RunAsync(
                        batchRoot: workspaceRoot,
                        casesCsvPath: fullCsvPath,
                        baseConfig: _baseConfig,
                        maxParallelCases: MaxParallelCases,
                        ncpuPerCase: NcpuPerCase,
                        memoryPerCase: _baseConfig.Workspace.MemoryPerCase,
                        records: recordList,
                        logger: msg => Application.Current.Dispatcher.Invoke(() => AppendLog(msg)),
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
            AppendLog("[UI] 用户正在请求取消任务，等待当前进行中的工况安全中断...");
            _cts?.Cancel();
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