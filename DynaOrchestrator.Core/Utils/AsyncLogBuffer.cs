using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DynaOrchestrator.Core.Utils
{
    /// <summary>
    /// 后台日志缓冲器。
    ///
    /// 作用：
    /// 1. 后台线程可以高频调用 Log() 写入日志。
    /// 2. 日志先进入队列，不直接刷新 UI。
    /// 3. 定时批量刷新，降低 WPF Dispatcher 压力。
    /// 4. FlushNowAsync / DisposeAsync 会等待 UI 真正完成写入。
    /// </summary>
    public sealed class AsyncLogBuffer : IAsyncDisposable
    {
        private readonly ConcurrentQueue<string> _queue = new();

        /// <summary>
        /// 批量写入 UI 的异步回调。
        /// 调用方应在该回调中切换到 UI Dispatcher，并等待写入完成。
        /// </summary>
        private readonly Func<IReadOnlyList<string>, Task> _uiUpdateAsync;

        private readonly int _flushIntervalMs;
        private readonly CancellationTokenSource _cts = new();

        /// <summary>
        /// 防止周期刷新、手动刷新、释放刷新同时操作队列。
        /// </summary>
        private readonly SemaphoreSlim _flushLock = new(1, 1);

        private readonly Task _flushTask;

        /// <summary>
        /// 0 = 未释放，1 = 已释放。
        /// 使用 int 是为了配合 Interlocked / Volatile。
        /// </summary>
        private int _disposed;

        public AsyncLogBuffer(
            Func<IReadOnlyList<string>, Task> uiUpdateAsync,
            int flushIntervalMs = 200)
        {
            _uiUpdateAsync = uiUpdateAsync
                ?? throw new ArgumentNullException(nameof(uiUpdateAsync));

            _flushIntervalMs = flushIntervalMs > 0 ? flushIntervalMs : 200;

            // 启动后台周期刷新任务。
            _flushTask = Task.Run(FlushLoopAsync);
        }

        /// <summary>
        /// 写入一条日志。
        /// 该方法通常由后台线程调用，不直接访问 UI。
        /// </summary>
        public void Log(string message)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            _queue.Enqueue(message);
        }

        /// <summary>
        /// 后台周期刷新循环。
        /// 每隔 _flushIntervalMs 毫秒把队列中的日志批量推送到 UI。
        /// </summary>
        private async Task FlushLoopAsync()
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(_flushIntervalMs, _cts.Token)
                        .ConfigureAwait(false);

                    await FlushNowAsync()
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // DisposeAsync 触发取消时会进入这里，属于正常退出。
            }
        }

        /// <summary>
        /// 立即刷新当前队列中的日志。
        ///
        /// 与旧版 FlushNow() 不同：
        /// 该方法会等待 _uiUpdateAsync 执行完成。
        /// 因此返回时，日志已经真正进入 UI 集合。
        /// </summary>
        public async Task FlushNowAsync()
        {
            if (_queue.IsEmpty)
                return;

            await _flushLock.WaitAsync().ConfigureAwait(false);

            try
            {
                if (_queue.IsEmpty)
                    return;

                var batchMessages = new List<string>();

                while (_queue.TryDequeue(out var msg))
                {
                    batchMessages.Add(msg);
                }

                if (batchMessages.Count > 0)
                {
                    await _uiUpdateAsync(batchMessages)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                _flushLock.Release();
            }
        }

        /// <summary>
        /// 停止后台刷新任务，并刷新剩余日志。
        ///
        /// DisposeAsync 返回时：
        /// 1. 后台刷新循环已停止；
        /// 2. 队列中剩余日志已写入 UI；
        /// 3. 资源已释放。
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _cts.Cancel();

            try
            {
                await _flushTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常取消。
            }

            await FlushNowAsync().ConfigureAwait(false);

            _cts.Dispose();
            _flushLock.Dispose();
        }
    }
}