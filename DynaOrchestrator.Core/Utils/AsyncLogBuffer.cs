using System.Collections.Concurrent;
using System.Text;

namespace DynaOrchestrator.Core.Utils
{
    /// <summary>
    /// 异步日志缓冲器：防止高频日志爆发式输出导致 WPF UI 线程假死
    /// </summary>
    public class AsyncLogBuffer : IAsyncDisposable, IDisposable
    {
        private readonly ConcurrentQueue<string> _queue = new();
        private readonly Action<string> _uiUpdateAction;
        private readonly int _flushIntervalMs;
        private readonly CancellationTokenSource _cts = new();
        private Task? _flushTask;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="uiUpdateAction">UI 线程的实际写入操作 (如 TextBox.AppendText)</param>
        /// <param name="flushIntervalMs">刷新间隔 (毫秒)，默认 200ms</param>
        public AsyncLogBuffer(Action<string> uiUpdateAction, int flushIntervalMs = 200)
        {
            _uiUpdateAction = uiUpdateAction ?? throw new ArgumentNullException(nameof(uiUpdateAction));
            _flushIntervalMs = flushIntervalMs;

            // 启动后台定时刷新任务
            _flushTask = Task.Run(FlushLoopAsync);
        }

        /// <summary>
        /// 提供给后台逻辑调用的日志入口 (极速返回，不阻塞后台运算)
        /// </summary>
        public void Log(string message)
        {
            _queue.Enqueue(message);
        }

        private async Task FlushLoopAsync()
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(_flushIntervalMs, _cts.Token).ConfigureAwait(false);
                    FlushNow();
                }
            }
            catch (TaskCanceledException)
            {
                // 正常取消
            }
        }

        /// <summary>
        /// 立即将队列中的日志合并并推送到 UI
        /// </summary>
        public void FlushNow()
        {
            if (_queue.IsEmpty) return;

            var sb = new StringBuilder();
            // 批量出队
            while (_queue.TryDequeue(out var msg))
            {
                sb.AppendLine(msg);
            }

            if (sb.Length > 0)
            {
                // 触发 UI 更新委托（注意：外部的 _uiUpdateAction 需要包含 Dispatcher.Invoke 逻辑）
                _uiUpdateAction(sb.ToString().TrimEnd('\r', '\n'));
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            FlushNow(); // 确保残留日志被输出
            _cts.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            if (_flushTask != null)
            {
                await Task.WhenAny(_flushTask, Task.Delay(500)); // 给它一点时间结束
            }
            FlushNow();
            _cts.Dispose();
        }
    }
}