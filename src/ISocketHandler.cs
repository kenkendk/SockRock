using System;
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
        SocketStream MonitorHandle(int handle, bool closehandle = true);
    }
}
