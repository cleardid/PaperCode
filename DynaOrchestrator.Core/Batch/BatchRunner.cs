using DynaOrchestrator.Core.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DynaOrchestrator.Core.Solver;

namespace DynaOrchestrator.Core.Batch
{
    public static class BatchRunner
    {
        /// <summary>
        /// 异步执行批处理任务
        /// </summary>
        /// <param name="batchRoot">工作区根目录</param>
        /// <param name="casesCsvPath">CSV文件路径</param>
        /// <param name="baseConfig">基础配置</param>
        /// <param name="maxParallelCases">最大并发数</param>
        /// <param name="ncpuPerCase">每个工况分配的CPU核数</param>
        /// <param name="memoryPerCase">每个工况分配的内存</param>
        /// <param name="records">从 ViewModel 传入的、已实现数据绑定的工况列表</param>
        /// <param name="logger">用于向 WPF UI 回传日志的委托</param>
        /// <param name="cancellationToken">用于支持 UI 中途取消批处理的令牌</param>
        public static async Task RunAsync(
            string batchRoot,
            string casesCsvPath,
            AppConfig baseConfig,
            int maxParallelCases,
            int ncpuPerCase,
            string memoryPerCase,
            List<BatchCaseRecord> records,
            Action<string> logger,
            Action<BatchCaseRecord, string, string, string>? uiStateUpdater = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(batchRoot))
                throw new ArgumentException("batchRoot 不能为空。", nameof(batchRoot));
            if (string.IsNullOrWhiteSpace(casesCsvPath))
                throw new ArgumentException("casesCsvPath 不能为空。", nameof(casesCsvPath));
            if (baseConfig == null)
                throw new ArgumentNullException(nameof(baseConfig));
            if (records == null || records.Count == 0)
                throw new ArgumentException("传入的工况列表为空。", nameof(records));
            if (maxParallelCases <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxParallelCases), "maxParallelCases 必须大于 0。");
            if (ncpuPerCase <= 0)
                throw new ArgumentOutOfRangeException(nameof(ncpuPerCase), "ncpuPerCase 必须大于 0。");

            logger("========== 启动批处理模式 ==========");

            string fullBatchRoot = Path.GetFullPath(batchRoot);
            string fullCsvPath = Path.GetFullPath(casesCsvPath);

            CaseCsvWriter.EnsureStatusColumns(fullCsvPath);

            logger($"[Batch] 接收到 {records.Count} 条工况。");

            var distinctStages = records
                .Select(r => r.DatasetStage)    // 选择所有 records 中的 DatasetStage 字段
                .Where(s => !string.IsNullOrWhiteSpace(s)) // 排除空值
                .Distinct(StringComparer.OrdinalIgnoreCase) // 忽略大小写比较
                .ToList(); // 转换为 List

            // 如果存在不同 DatasetStage 的记录，则抛出异常
            if (distinctStages.Count != 1)
                throw new Exception("当前要求同一批次 CSV 中所有记录必须属于同一个 DatasetStage。");

            // 获取同一 DatasetStage 下的所有记录
            string datasetStage = distinctStages[0];
            // 创建总结目录，用于统计结果
            string summaryDir = Path.Combine(fullBatchRoot, "runs", datasetStage, "summary");
            Directory.CreateDirectory(summaryDir);

            // 筛选出未完成的工况
            var pendingRecords = records.Where(r => !r.IsCompleted).ToList();
            logger($"[Batch] 未完成工况数: {pendingRecords.Count}");
            // 如果没有可执行工况，则直接返回
            if (pendingRecords.Count == 0)
            {
                logger("[Batch] 所有工况均已完成，无需重复执行。");
                return;
            }

            // 线程安全的结果集合
            var results = new ConcurrentBag<BatchRunResult>();
            // 创建信号量，限制并发数
            // 第一个参数表示初始信号量的计数，第二个参数表示最大信号量计数
            var semaphore = new SemaphoreSlim(maxParallelCases, maxParallelCases);

            // 构建并发任务
            var tasks = pendingRecords.Select(async record =>
            {
                bool semaphoreAcquired = false;

                try
                {
                    // 等待信号量，直到有可用资源，即任一任务完成或被取消时开始执行
                    await semaphore.WaitAsync(cancellationToken);
                    semaphoreAcquired = true;

                    cancellationToken.ThrowIfCancellationRequested();

                    BatchRunResult result = await Task.Run(() =>
                    {
                        return RunSingleCase(
                            fullBatchRoot,
                            fullCsvPath,
                            record,
                            baseConfig,
                            ncpuPerCase,
                            memoryPerCase,
                            logger,
                            uiStateUpdater,
                            cancellationToken);
                    }, cancellationToken);

                    results.Add(result);
                }
                // 如果捕获到取消异常
                catch (OperationCanceledException)
                {
                    UpdateRuntimeState(record, "Canceled", "0", uiStateUpdater);

                    if (!string.IsNullOrWhiteSpace(fullCsvPath))
                        CaseCsvWriter.MarkCanceled(fullCsvPath, record.CaseId);

                    results.Add(new BatchRunResult
                    {
                        CaseId = record.CaseId,
                        GeomType = record.GeomType,
                        DatasetStage = record.DatasetStage,
                        Status = "canceled",
                        Message = "Canceled before execution",
                        ExitCode = -2,
                        StartTime = DateTime.Now,
                        EndTime = DateTime.Now
                    });

                    logger($"[Batch] 工况 {record.CaseId} 被用户取消。");
                }
                finally
                {
                    if (semaphoreAcquired)
                        semaphore.Release();
                }
            }).ToArray();

            // 等待所有任务完成
            await Task.WhenAll(tasks);

            var orderedResults = results
                .OrderBy(r => r.CaseId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 写入总结文件
            WriteSummaryFiles(summaryDir, orderedResults, datasetStage);

            int successCount = orderedResults.Count(r => string.Equals(r.Status, "success", StringComparison.OrdinalIgnoreCase));
            int canceledCount = orderedResults.Count(r => string.Equals(r.Status, "canceled", StringComparison.OrdinalIgnoreCase));
            int failedCount = orderedResults.Count(r => string.Equals(r.Status, "failed", StringComparison.OrdinalIgnoreCase));

            logger($"[Batch] 批处理完成。成功 {successCount} 条，取消 {canceledCount} 条，失败 {failedCount} 条。");
            logger($"[Batch] 汇总目录: {summaryDir}");
        }

        /// <summary>
        /// 运行单个工况的核心逻辑
        /// </summary>
        private static BatchRunResult RunSingleCase(
            string batchRoot,
            string? casesCsvPath,
            BatchCaseRecord record,
            AppConfig baseConfig,
            int ncpuPerCase,
            string memoryPerCase,
            Action<string> logger,
            Action<BatchCaseRecord, string, string, string>? uiStateUpdater,
            CancellationToken cancellationToken)
        {
            // 构建工况目录
            var paths = new BatchCasePaths(batchRoot, record);
            // 声明结果
            var result = new BatchRunResult
            {
                CaseId = record.CaseId,
                GeomType = record.GeomType,
                DatasetStage = record.DatasetStage,
                InputDir = paths.InputDir,
                OutputDir = paths.OutputDir,
                StartTime = DateTime.Now
            };

            try
            {
                logger($"\n[Batch] 开始工况: {record.CaseId}");

                // 1. 直接更新对象的属性，触发 INotifyPropertyChanged 通知 WPF 界面刷新
                UpdateRuntimeState(record, "Running", "0", uiStateUpdater);

                // 2. 同步更新 CSV 文件
                if (!string.IsNullOrWhiteSpace(casesCsvPath))
                    CaseCsvWriter.MarkRunning(casesCsvPath, record.CaseId);

                // 重新创建工况目录
                // 此处后续可优化，第三阶段任务失败时，不需要重新创建工况目录，此目录仅用于第一、二阶段
                RecreateCaseDirectory(paths);
                // 拷贝 k文件、模型文件到工况目录
                paths.CopyBaseFilesToInput(overwrite: true);

                AppConfig caseConfig = BatchConfigBuilder.Build(
                    baseConfig,
                    record,
                    paths,
                    ncpuPerCase,
                    memoryPerCase,
                    logger);

                BatchConfigBuilder.WriteConfig(caseConfig, paths.LocalConfigFile);
                WriteCaseMetadata(paths.LocalCaseMetadataFile, record, paths);

                // 3. 执行单工况流水线，并将 logger 向下传递给 LS-DYNA 和 C++ 引擎的包装层
                PipelineExecutor.Execute(
                    caseConfig,
                    record,
                    cancellationToken: cancellationToken,
                    logger: logger);

                // 4. 成功后更新结果及状态
                result.Status = "success";
                result.Message = "OK";
                result.ExitCode = 0;

                UpdateRuntimeState(record, "Success", "1", uiStateUpdater);
                if (!string.IsNullOrWhiteSpace(casesCsvPath))
                    CaseCsvWriter.MarkSuccess(casesCsvPath, record.CaseId);
            }
            catch (OperationCanceledException)
            {
                result.Status = "canceled";
                result.Message = "Canceled by user";
                result.ExitCode = -2;

                UpdateRuntimeState(record, "Canceled", "0", uiStateUpdater);

                if (!string.IsNullOrWhiteSpace(casesCsvPath))
                    CaseCsvWriter.MarkCanceled(casesCsvPath, record.CaseId);

                logger($"[Batch][取消] {record.CaseId}: 工况已被用户终止。");
            }
            catch (Exception ex)
            {
                result.Status = "failed";
                result.Message = ex.Message;
                result.ExitCode = -1;

                UpdateRuntimeState(record, "Failed", "0", uiStateUpdater);

                if (!string.IsNullOrWhiteSpace(casesCsvPath))
                    CaseCsvWriter.MarkFailed(casesCsvPath, record.CaseId);

                logger($"[Batch][失败] {record.CaseId}: {ex.Message}");
            }
            finally
            {
                result.EndTime = DateTime.Now;
            }

            return result;
        }

        private static void UpdateRuntimeState(
            BatchCaseRecord record,
            string status,
            string completed,
            Action<BatchCaseRecord, string, string, string>? uiStateUpdater)
        {
            string lastRunTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            if (uiStateUpdater != null)
            {
                uiStateUpdater(record, status, completed, lastRunTime);
            }
            else
            {
                record.Status = status;
                record.Completed = completed;
                record.LastRunTime = lastRunTime;
            }
        }

        private static void RecreateCaseDirectory(BatchCasePaths paths)
        {
            if (Directory.Exists(paths.CaseRootDir))
                Directory.Delete(paths.CaseRootDir, recursive: true);

            paths.EnsureDirectories();
        }

        private static void WriteCaseMetadata(string metadataPath, BatchCaseRecord record, BatchCasePaths paths)
        {
            string? dir = Path.GetDirectoryName(metadataPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var metadata = new
            {
                record.CaseId,
                record.DatasetStage,
                record.GeomType,
                record.L,
                record.W,
                record.H,
                record.PositionType,
                record.X,
                record.Y,
                record.Z,
                record.ChargeLevel,
                record.ChargeMass,
                record.ChargeDensity,
                record.Completed,
                record.Status,
                record.LastRunTime,
                paths.InputDir,
                paths.RunDir,
                paths.OutputDir,
                paths.SourceBaseKFile,
                paths.SourceStlFile
            };

            File.WriteAllText(
                metadataPath,
                JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static void WriteSummaryFiles(string summaryDir, List<BatchRunResult> results, string datasetStage)
        {
            Directory.CreateDirectory(summaryDir);

            string runSummaryCsv = Path.Combine(summaryDir, "run_summary.csv");
            string successCsv = Path.Combine(summaryDir, "success_cases.csv");
            string failedCsv = Path.Combine(summaryDir, "failed_cases.csv");
            string summaryJson = Path.Combine(summaryDir, "run_summary.json");

            var allLines = new List<string>
            {
                "CaseId,GeomType,DatasetStage,Status,Message,InputDir,OutputDir,StartTime,EndTime,DurationSeconds,ExitCode"
            };

            var successLines = new List<string>
            {
                "CaseId,GeomType,DatasetStage,DurationSeconds"
            };

            var failedLines = new List<string>
            {
                "CaseId,GeomType,DatasetStage,Message"
            };

            foreach (var r in results)
            {
                allLines.Add(string.Join(",",
                    EscapeCsv(r.CaseId),
                    EscapeCsv(r.GeomType),
                    EscapeCsv(r.DatasetStage),
                    EscapeCsv(r.Status),
                    EscapeCsv(r.Message),
                    EscapeCsv(r.InputDir),
                    EscapeCsv(r.OutputDir),
                    EscapeCsv(r.StartTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                    EscapeCsv(r.EndTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                    EscapeCsv(r.DurationSeconds.ToString("F3", CultureInfo.InvariantCulture)),
                    EscapeCsv(r.ExitCode.ToString(CultureInfo.InvariantCulture))));

                if (string.Equals(r.Status, "success", StringComparison.OrdinalIgnoreCase))
                {
                    successLines.Add(string.Join(",",
                        EscapeCsv(r.CaseId),
                        EscapeCsv(r.GeomType),
                        EscapeCsv(r.DatasetStage),
                        EscapeCsv(r.DurationSeconds.ToString("F3", CultureInfo.InvariantCulture))));
                }
                else
                {
                    failedLines.Add(string.Join(",",
                        EscapeCsv(r.CaseId),
                        EscapeCsv(r.GeomType),
                        EscapeCsv(r.DatasetStage),
                        EscapeCsv(r.Message)));
                }
            }

            File.WriteAllLines(runSummaryCsv, allLines, Encoding.UTF8);
            File.WriteAllLines(successCsv, successLines, Encoding.UTF8);
            File.WriteAllLines(failedCsv, failedLines, Encoding.UTF8);

            var summaryObj = new
            {
                DatasetStage = datasetStage,
                Total = results.Count,
                Success = results.Count(r => string.Equals(r.Status, "success", StringComparison.OrdinalIgnoreCase)),
                Failed = results.Count(r => string.Equals(r.Status, "failed", StringComparison.OrdinalIgnoreCase)),
                Canceled = results.Count(r => string.Equals(r.Status, "canceled", StringComparison.OrdinalIgnoreCase)),
                GeneratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            };

            File.WriteAllText(summaryJson, JsonSerializer.Serialize(summaryObj, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static string EscapeCsv(string? value)
        {
            value ??= string.Empty;
            bool mustQuote = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
            if (!mustQuote)
                return value;

            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>
        /// 取消所有正在运行的案例
        /// </summary>
        /// <param name="logger"></param>
        public static void ForceStopAllRunningCases(Action<string>? logger = null)
        {
            LsDynaOrchestrator.KillAllRunningProcesses(logger);
        }
    }
}