using System;
using System.Threading.Tasks;

namespace SockRock
{
    /// <summary>
    /// An instance that can monitor a handle with async events
    /// </summary>
    public class MonitoredHandle : IMonitorItem
    {
        /// <summary>
        /// A flag that tracks the disposed state of this object
        /// </summary>
        private bool m_isDisposed;

        /// <summary>
        /// Signal that data can be read
        /// </summary>
        private TaskCompletionSource<bool> m_readDataSignal = new TaskCompletionSource<bool>();

        /// <summary>
        /// Signal that data can be written
        /// </summary>
        private TaskCompletionSource<bool> m_writeDataSignal = new TaskCompletionSource<bool>();

        /// <summary>
        /// A flag indicating if the process is disposed
        /// </summary>
        public bool IsDisposed => m_isDisposed;

        /// <summary>
        /// The method used to deregister
        /// </summary>
        private readonly Action m_deregister;

        /// <summary>
        /// The handle we are monitoring
        /// </summary>
        public int Handle { get; private set; }

        /// <summary>
        /// Constructs a new monitored handle
        /// </summary>
        /// <param name="handle">The handle to monitor</param>
        /// <param name="deregister">The method used to deregister this entry</param>
        public MonitoredHandle(int handle, Action deregister)
        {
            Handle = handle;
            m_deregister = deregister;
        }

        /// <summary>
        /// Gets an event that blocks until data is ready, do not call this method again before the handle has returned EAGAIN
        /// </summary>
        public Task WaitForReadAsync => m_readDataSignal.Task.ContinueWith(x => { m_readDataSignal = new TaskCompletionSource<bool>(); return x; });

        /// <summary>
        /// Gets an event that blocks until data is ready, do not call this method again before the handle has returned EAGAIN
        /// </summary>
        public Task WaitForWriteAsync => m_writeDataSignal.Task.ContinueWith(x => { m_writeDataSignal = new TaskCompletionSource<bool>(); return x; });

        ///<inheritdoc />
        void IMonitorItem.SignalReadReady()
        {
            if (!m_isDisposed)
                m_readDataSignal.TrySetResult(true);
        }

        ///<inheritdoc />
        void IMonitorItem.SignalWriteReady()
        {
            if (!m_isDisposed)
                m_writeDataSignal.TrySetResult(true);
        }

        /// <summary>
        /// Closes this instance
        /// </summary>
        public void Dispose()
        {
            if (!m_isDisposed)
            {
                m_isDisposed = true;
                m_readDataSignal.TrySetCanceled();
                m_writeDataSignal.TrySetCanceled();
                m_deregister?.Invoke();
            }
        }

    }
}