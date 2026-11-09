using System;
using System.Threading;

namespace AIHub.Proxy
{
    /// <summary>
    /// 跨 Visual Studio 实例的端口仲裁。
    /// 用一个命名互斥体表示「谁在监听端口」：持有者释放互斥体（或进程崩溃被系统遗弃）时，
    /// 其它实例阻塞在等待上的线程会立刻被唤醒，因此接管是事件驱动的，不需要任何轮询。
    /// </summary>
    internal sealed class ProxyCoordinator : IDisposable
    {
        private readonly Mutex _hostMutex;
        private readonly EventWaitHandle _stopSignal;

        private bool _owned;
        private int _ownerThreadId;

        public ProxyCoordinator(int port)
        {
            Port = port;
            _hostMutex = new Mutex(false, @"Local\AIHub.Proxy.Host." + port);

            // 手工重置的命名事件：任一实例点了「停止代理」后，其它实例同步停止并保持停止。
            _stopSignal = new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                @"Local\AIHub.Proxy.Stop." + port);
        }

        /// <summary>本协调器仲裁的端口。</summary>
        public int Port { get; }

        /// <summary>本实例要主动启动代理时，先清掉其它实例留下的「停止」信号。</summary>
        public void ClearStopSignal()
        {
            try
            {
                _stopSignal.Reset();
            }
            catch (Exception)
            {
                // 忽略跨进程句柄异常。
            }
        }

        /// <summary>跨进程的「停止」信号句柄，宿主线程要把它和本地的停止/重启请求一起等。</summary>
        public WaitHandle StopSignal => _stopSignal;

        /// <summary>
        /// 阻塞等待成为唯一宿主。返回 true 表示当前线程已持有互斥体，可以开始监听端口。
        /// <paramref name="cancel"/> 被置位、或其它实例明确停止了代理时返回 false。
        /// </summary>
        public bool WaitForHostship(WaitHandle cancel)
        {
            while (true)
            {
                if (cancel.WaitOne(0) || _stopSignal.WaitOne(0))
                {
                    return false;
                }

                try
                {
                    // 注意：WaitAny 命中 Mutex 时已经完成了一次所有权获取，不能再 WaitOne 一次，
                    // 否则递归计数变成 2，ReleaseMutex 一次也释放不掉，其它实例永远接管不了。
                    if (WaitHandle.WaitAny(new[] { _hostMutex, cancel, _stopSignal }) != 0)
                    {
                        return false;
                    }
                }
                catch (AbandonedMutexException ex)
                {
                    // 上一个宿主进程崩溃，所有权已直接转给本线程。
                    if (ex.MutexIndex != 0)
                    {
                        return false;
                    }
                }

                if (_stopSignal.WaitOne(0) || cancel.WaitOne(0))
                {
                    ReleaseMutex();
                    return false;
                }

                _owned = true;
                _ownerThreadId = Thread.CurrentThread.ManagedThreadId;
                return true;
            }
        }

        /// <summary>
        /// 广播「停止代理」信号。<paramref name="stopEverything"/> 之外还用于：本实例不是宿主时，
        /// 也能让真正的宿主一起停下来。
        /// </summary>
        public void SignalStop()
        {
            try
            {
                _stopSignal.Set();
            }
            catch (Exception)
            {
                // 忽略跨进程句柄异常。
            }
        }

        /// <summary>
        /// 释放宿主身份。<paramref name="stopEverything"/> 为 true 时通知其它实例一起停止；
        /// 为 false 表示只是本进程退出，让别的实例立刻接管。
        /// </summary>
        public void Release(bool stopEverything)
        {
            if (!_owned || Thread.CurrentThread.ManagedThreadId != _ownerThreadId)
            {
                return;
            }

            _owned = false;

            if (stopEverything)
            {
                SignalStop();
            }

            ReleaseMutex();
        }

        public void Dispose()
        {
            if (_owned)
            {
                Release(false);
            }

            try
            {
                _hostMutex.Dispose();
            }
            catch (Exception)
            {
                // 忽略。
            }

            try
            {
                _stopSignal.Dispose();
            }
            catch (Exception)
            {
                // 忽略。
            }
        }

        private void ReleaseMutex()
        {
            try
            {
                _hostMutex.ReleaseMutex();
            }
            catch (Exception)
            {
                // 忽略未持有或已释放的情况。
            }
        }
    }
}
