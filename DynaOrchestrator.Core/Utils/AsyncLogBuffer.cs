using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DynaOrchestrator.Core.Utils
{
    /// <summary>
    /// 异步日志缓冲器：防止高频日志爆发式输出导致 WPF UI 线程假死。
    /// [渲染优化版]：将日志作为独立集合抛出，修复单条超大文本导致的滚动条失效与渲染卡顿。
    /// </summary>
    public class AsyncLogBuffer : IAsyncDisposable, IDisposable
    {
        private readonly ConcurrentQueue<string> _queue = new();

        // 【修改说明】：回调签名从 Action<string> 改为 Action<List<string>>
        // 目的：让 UI 层能够遍历集合，逐条插入 ListBox，从而保证 ScrollIntoView 精准定位到最后一行。
        private readonly Action<List<string>> _uiUpdateAction;

        private readonly int _flushIntervalMs;
        private readonly CancellationTokenSource _cts = new();
        private Task? _flushTask;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="uiUpdateAction">UI 线程的实际写入操作 (接收批量日志列表)</param>
        /// <param name="flushIntervalMs">刷新间隔 (毫秒)，默认 200ms</param>
        public AsyncLogBuffer(Action<List<string>> uiUpdateAction, int flushIntervalMs = 200)
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
        /// 立即将队列中的日志提取并推送到 UI
        /// </summary>
        public void FlushNow()
        {
            if (_queue.IsEmpty) return;

            // 【修改说明】：放弃使用 StringBuilder 拼接单行文本，改为使用 List 收集本轮所有日志。
            var batchMessages = new List<string>();

            // 批量出队
            while (_queue.TryDequeue(out var msg))
            {
                batchMessages.Add(msg);
            }

            if (batchMessages.Count > 0)
            {
                // 触发 UI 更新委托，将集合整体抛出
                _uiUpdateAction(batchMessages);
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