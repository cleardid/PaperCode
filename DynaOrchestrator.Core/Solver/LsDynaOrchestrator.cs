using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using DynaOrchestrator.Core.Models;

namespace DynaOrchestrator.Core.Solver
{
    public static class LsDynaOrchestrator
    {
        // 记录当前所有正在运行的 LS-DYNA 进程
        private static readonly ConcurrentDictionary<int, Process> RunningProcesses = new();

        public static bool Run(
            PipelineConfig config,
            int ncpu,
            string memory,
            CancellationToken cancellationToken = default,
            Action<string>? logger = null)
        {
            bool isNormalTermination = false;
            Process? process = null;
            CancellationTokenRegistration ctr = default;

            try
            {
                // 1. 获取输入 K 文件的绝对路径和所在目录
                string fullKFilePath = Path.GetFullPath(config.OutputKFile);
                string workDir = Path.GetDirectoryName(fullKFilePath) ?? string.Empty;

                if (!Directory.Exists(workDir))
                    Directory.CreateDirectory(workDir);

                var startInfo = new ProcessStartInfo
                {
                    FileName = config.LsDynaPath,
                    Arguments = $"i={Path.GetFileName(fullKFilePath)} memory={memory} ncpu={ncpu}",
                    WorkingDirectory = workDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                process = Process.Start(startInfo);
                if (process == null)
                    return false;

                RunningProcesses[process.Id] = process;

                logger?.Invoke($"[DYNA] 进程已启动，PID={process.Id}");

                process.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        logger?.Invoke($"[DYNA] {e.Data}");
                        if (e.Data.Contains("N o r m a l    t e r m i n a t i o n") ||
                            e.Data.Contains("Normal termination"))
                        {
                            isNormalTermination = true;
                        }
                    }
                };

                process.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        logger?.Invoke($"[DYNA ERR] {e.Data}");
                };

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // 一旦收到取消请求，立即杀死该求解器进程树
                if (cancellationToken.CanBeCanceled)
                {
                    ctr = cancellationToken.Register(() =>
                    {
                        TryKillProcess(process, $"[DYNA] 收到取消请求，准备终止进程 PID={process.Id}", logger);
                    });
                }

                // 阻塞等待进程结束；若上层触发取消，将由 Register 回调直接 Kill
                process.WaitForExit();

                // 如果结束是由取消触发，直接按取消处理，不走失败逻辑
                cancellationToken.ThrowIfCancellationRequested();

                if (!isNormalTermination)
                {
                    logger?.Invoke("[Warning] LS-DYNA 未检测到正常结束标志！");
                    return false;
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                logger?.Invoke("[DYNA] 求解器已被取消。");
                throw;
            }
            catch (Exception ex)
            {
                logger?.Invoke($"[Error] 求解器异常: {ex.Message}");
                return false;
            }
            finally
            {
                ctr.Dispose();

                if (process != null)
                {
                    RunningProcesses.TryRemove(process.Id, out _);
                    process.Dispose();
                }
            }
        }

        /// <summary>
        /// 直接杀死当前所有正在运行的 LS-DYNA 进程。
        /// </summary>
        public static void KillAllRunningProcesses(Action<string>? logger = null)
        {
            var snapshot = RunningProcesses.Values.ToArray();
            if (snapshot.Length == 0)
            {
                logger?.Invoke("[DYNA] 当前没有正在运行的求解器进程。");
                return;
            }

            logger?.Invoke($"[DYNA] 准备终止 {snapshot.Length} 个正在运行的求解器进程...");

            foreach (var process in snapshot)
            {
                TryKillProcess(process, $"[DYNA] 强制终止进程 PID={process.Id}", logger);
            }
        }

        private static void TryKillProcess(Process? process, string logMessage, Action<string>? logger)
        {
            if (process == null)
                return;

            try
            {
                if (process.HasExited)
                    return;

                logger?.Invoke(logMessage);

                // 直接终止整个进程树，避免残留子进程
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 进程可能刚好已经退出，忽略
            }
            catch (Exception ex)
            {
                logger?.Invoke($"[DYNA] 终止进程失败: {ex.Message}");
            }
        }
    }
}