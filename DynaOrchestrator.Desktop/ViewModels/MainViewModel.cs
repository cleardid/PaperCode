using DynaOrchestrator.Core.Batch;
using DynaOrchestrator.Core.Models;
using DynaOrchestrator.Core.Utils;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Windows;

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
            set
            {
                if (_baseConfig.Workspace.RootDir == value) return;
                _baseConfig.Workspace.RootDir = value;
                OnPropertyChanged();
                SaveBaseConfig();
            }
        }

        public string CasesCsvPath
        {
            get => _baseConfig.Workspace.CasesCsv;
            set
            {
                if (_baseConfig.Workspace.CasesCsv == value) return;
                _baseConfig.Workspace.CasesCsv = value;
                OnPropertyChanged();
                SaveBaseConfig();
            }
        }

        // --- 绑定到 UI 的全局执行参数 ---
        public int MaxParallelCases
        {
            get => _baseConfig.Workspace.MaxParallelCases;
            set
            {
                if (_baseConfig.Workspace.MaxParallelCases == value) return;
                _baseConfig.Workspace.MaxParallelCases = value;
                OnPropertyChanged();
                SaveBaseConfig();
            }
        }

        public int NcpuPerCase
        {
            get => _baseConfig.Workspace.NcpuPerCase;
            set
            {
                if (_baseConfig.Workspace.NcpuPerCase == value) return;
                _baseConfig.Workspace.NcpuPerCase = value;
                OnPropertyChanged();
                SaveBaseConfig();
            }
        }

        public string MemoryPerCase
        {
            get => _baseConfig.Workspace.MemoryPerCase;
            set
            {
                if (_baseConfig.Workspace.MemoryPerCase == value) return;
                _baseConfig.Workspace.MemoryPerCase = value;
                OnPropertyChanged();

                try
                {
                    _baseConfig.Workspace.MemoryPerCase = WorkspaceSettingsValidator.NormalizeMemoryPerCase(value);
                    OnPropertyChanged();
                    SaveBaseConfig();
                }
                catch
                {
                    // 文本框允许用户继续编辑到合法值；仅当格式合法时才持久化到 config.json。
                }
            }
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

                int i = 1;
                foreach (var r in records)
                {
                    r.Index = i++; // 分配序号
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
        /// 确认批处理任务是否可以开始
        /// </summary>
        /// <param name="normalizedMemoryPerCase"></param>
        /// <returns></returns>
        private bool ConfirmBatchStart(string normalizedMemoryPerCase)
        {
            bool isPreOnly = EnablePreProcessing && !EnableSimulation && !EnablePostProcessing;
            bool isSimOnly = !EnablePreProcessing && EnableSimulation && !EnablePostProcessing;
            bool isPostOnly = !EnablePreProcessing && !EnableSimulation && EnablePostProcessing;
            bool isPreSim = EnablePreProcessing && EnableSimulation && !EnablePostProcessing;
            bool isSimPost = !EnablePreProcessing && EnableSimulation && EnablePostProcessing;
            bool isFullPipeline = EnablePreProcessing && EnableSimulation && EnablePostProcessing;
            bool isAllDisabled = !EnablePreProcessing && !EnableSimulation && !EnablePostProcessing;

            string modeName;
            if (isFullPipeline)
                modeName = "全流程（前处理 + 求解 + 后处理）";
            else if (isPreSim)
                modeName = "前两步（前处理 + 求解）";
            else if (isPostOnly)
                modeName = "仅第三步（后处理）";
            else if (isPreOnly)
                modeName = "仅第一步（前处理）";
            else if (isSimOnly)
                modeName = "仅第二步（求解）";
            else if (isSimPost)
                modeName = "后两步（求解 + 后处理）";
            else
                modeName = "未选择任何阶段";

            string warning = string.Empty;

            if (isAllDisabled)
            {
                warning =
                    "警告：当前三个阶段全部关闭，本次不会执行任何任务。\n" +
                    "请返回检查勾选状态。\n\n";
            }
            else if (isPostOnly)
            {
                warning =
                    "警告：当前为“仅第三步”模式。\n" +
                    "只有状态为 Simulated 且 run/trhist 已存在的工况才能正常执行。\n" +
                    "如果前两步未完成，相关工况会被跳过或失败。\n\n";
            }
            else if (isSimOnly)
            {
                warning =
                    "提示：当前为“仅第二步”模式。\n" +
                    "通常要求对应工况已经完成前处理，并具备可直接求解的输入文件。\n\n";
            }
            else if (isPreOnly)
            {
                warning =
                    "提示：当前为“仅第一步”模式。\n" +
                    "本轮结束后工况通常会停留在 PreProcessed 状态，不会直接完成整体流程。\n\n";
            }
            else if (isPreSim)
            {
                warning =
                    "提示：当前为“前两步”模式。\n" +
                    "本轮结束后工况通常会停留在 Simulated 状态，适合后续统一执行第三步。\n\n";
            }
            else if (isSimPost)
            {
                warning =
                    "提示：当前为“后两步”模式。\n" +
                    "通常要求工况已经完成前处理。\n\n";
            }

            string modeSummary =
                $"{warning}" +
                $"执行模式: {modeName}\n\n" +
                $"前处理: {(EnablePreProcessing ? "开启" : "关闭")}\n" +
                $"求解: {(EnableSimulation ? "开启" : "关闭")}\n" +
                $"后处理: {(EnablePostProcessing ? "开启" : "关闭")}\n\n" +
                $"最大并发工况数: {MaxParallelCases}\n" +
                $"每工况 CPU 数: {NcpuPerCase}\n" +
                $"每工况内存: {normalizedMemoryPerCase}\n\n" +
                "确认按以上配置开始批处理吗？";

            var image = (isAllDisabled || isPostOnly)
                ? MessageBoxImage.Warning
                : MessageBoxImage.Question;

            var result = MessageBox.Show(
                modeSummary,
                "确认批处理执行配置",
                MessageBoxButton.YesNo,
                image,
                MessageBoxResult.No);

            return result == MessageBoxResult.Yes;
        }

        /// <summary>
        /// 启动批处理任务
        /// </summary>
        private async void StartBatch()
        {
            bool isPostOnlyMode = !EnablePreProcessing && !EnableSimulation && EnablePostProcessing;

            string normalizedMemoryPerCase;
            try
            {
                normalizedMemoryPerCase = WorkspaceSettingsValidator.NormalizeMemoryPerCase(MemoryPerCase);
                WorkspaceSettingsValidator.ValidateBatchSettings(MaxParallelCases, NcpuPerCase, normalizedMemoryPerCase);
                MemoryPerCase = normalizedMemoryPerCase;
            }
            catch (Exception ex)
            {
                AppendLog($"[Error] 批处理配置无效: {ex.Message}");
                return;
            }

            // 启动前弹框确认
            if (!ConfirmBatchStart(normalizedMemoryPerCase))
            {
                AppendLog("[UI] 用户取消了本次批处理启动。");
                return;
            }

            // --- 1. 运行前状态重置 ---
            // Running 说明上次异常中断，始终重置为 Pending。
            // 但在“只跑第三步”模式下，绝不能把 Failed/Canceled 重置为 Pending，
            // 否则会把没有 trhist 的 case 混进来。
            foreach (var record in Cases)
            {
                if (record.Status == "Running")
                {
                    record.Status = "Pending";
                }
                else if (!isPostOnlyMode && (record.Status == "Failed" || record.Status == "Canceled"))
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

                var recordList = Cases.Where(r => r.IsSelected).ToList();

                if (recordList.Count == 0)
                {
                    AppendLog("[Warning] 未勾选任何任务，请先在列表中挑选任务。");
                    return;
                }

                // 【修改说明】：适配 AsyncLogBuffer 的新签名 Action<List<string>>
                using var logBuffer = new AsyncLogBuffer(batchMessages =>
                {
                    Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        // 1. 遍历批量传递过来的日志，作为独立记录插入 ObservableCollection
                        // 此时 WPF 绑定的 ListBox 会将它们渲染为独立的 Item，ScrollIntoView 将正常工作
                        foreach (var msg in batchMessages)
                        {
                            AppendLog(msg);
                        }

                        // 2. 【新增保护机制】：日志行数截断 (防 OOM 与 UI 假死)
                        // 当批处理运行成百上千个工况时，无限增长的 UI 元素会导致内存溢出。
                        const int MaxLogLines = 5000;
                        if (Logs.Count > MaxLogLines)
                        {
                            // 批量移除顶部的旧日志，保持最新的 5000 行可见
                            int removeCount = Logs.Count - MaxLogLines;
                            for (int i = 0; i < removeCount; i++)
                            {
                                Logs.RemoveAt(0);
                            }
                        }
                    }, System.Windows.Threading.DispatcherPriority.Background);
                }, flushIntervalMs: 200);

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
                        logger: logBuffer.Log,
                        uiStateUpdater: (record, status, completed, lastRunTime) =>
                        {
                            Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                record.Status = status;
                                record.Completed = completed;
                                record.LastRunTime = lastRunTime;
                            }, System.Windows.Threading.DispatcherPriority.Background);
                        },
                        cancellationToken: _cts.Token
                    );
                });

                logBuffer.FlushNow();
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