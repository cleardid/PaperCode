using System;
using System.Diagnostics;
using System.IO;
using DynaOrchestrator.Core.Models;

namespace DynaOrchestrator.Core.Solver
{
    public static class LsDynaOrchestrator
    {
        public static bool Run(PipelineConfig config, Action<string>? logger = null)
        {

            // 业务级验证标志位
            bool isNormalTermination = false;

            try
            {
                // 1. 获取输入 K 文件的绝对路径和所在目录
                string fullKFilePath = Path.GetFullPath(config.OutputKFile);
                string workDir = Path.GetDirectoryName(fullKFilePath) ?? string.Empty;

                // 确保工作目录存在
                if (!Directory.Exists(workDir))
                {
                    Directory.CreateDirectory(workDir);
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = config.LsDynaPath,
                    Arguments = $"i={Path.GetFileName(fullKFilePath)} memory={config.Memory} ncpu={config.Ncpu}",
                    // 将 LS-DYNA 的生成目录锁死在这个文件夹
                    WorkingDirectory = workDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null) return false;
                    // 异步读取标准输出流，实时分析求解器状态
                    process.OutputDataReceived += (s, e) =>
                    {
                        // 捕捉 LS-DYNA 经典的正常结束标志 (兼容带空格与不带空格的版本)
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            logger?.Invoke($"[DYNA] {e.Data}");
                            if (e.Data.Contains("N o r m a l    t e r m i n a t i o n") || e.Data.Contains("Normal termination"))
                                isNormalTermination = true;
                        }
                    };

                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data)) logger?.Invoke($"[DYNA ERR] {e.Data}");
                    };

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    // 进程级阻塞：死等求解器退出，确保文件锁释放且数据全部刷入硬盘
                    process.WaitForExit();

                    // 双重校验：进程已安全退出，且内部业务逻辑确认跑到了最后一个时间步
                    if (!isNormalTermination)
                    {
                        logger?.Invoke("[Warning] LS-DYNA 未检测到正常结束标志！");
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                logger?.Invoke($"[Error] 求解器异常: {ex.Message}");
                return false;
            }
        }
    }
}
