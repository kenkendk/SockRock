using System;
using System.Threading.Tasks;

namespace SockRock
{
    /// <summary>
    /// Shared interface for socket handlers
    /// </summary>
    public interface ISocketHandler : IDisposable
    {
        /// <summary>
        /// Begins monitoring the handle and returns a stream for interacting with the handle
        /// </summary>
        /// <param name="handle">The handle to monitor</param>
        /// <param name="closehandle">If set to <c>true</c>, the socket handle is closed when the stream is disposed</param>
        /// <returns>The stream.</returns>
        SocketStream MonitorWithStream(int handle, bool closehandle = true);

        /// <summary>
        /// Begins monitoring the handle and returns an event proxy
        /// </summary>
        /// <param name="handle">The handle to monitor</param>
        /// <param name="closehandle">If set to <c>true</c>, the socket handle is closed when the stream is disposed</param>
        /// <returns>The monitor instance</returns>
        MonitoredHandle MonitoredHandle(int handle, bool closehandle = true);

        /// <summary>
        /// Stops the socket handler
        /// </summary>
        /// <param name="waittime">The grace period before the active connections are killed</param>
        void Stop(TimeSpan waittime);

        /// <summary>
        /// An awaitable task that monitors the internal runner thread
        /// </summary>
        Task WaitForShutdownAsync { get; }
    }
}
