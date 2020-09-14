using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Mono.Unix.Native;

namespace SockRock
{
    /// <summary>
    /// Implements functionality for listening to a socket with await syntax
    /// </summary>
    public class AsyncListenSocket : IDisposable
    {
        /// <summary>
        /// The socket we are bound to
        /// </summary>
        private readonly int m_socket;

        /// <summary>
        /// A monitor used to get async triggers from the monitor
        /// </summary>
        private readonly MonitoredHandle m_handle;

        /// <summary>
        /// Initializes a new instance of the <see cref="T:SockRock.ListenSocket"/> class.
        /// </summary>
        public AsyncListenSocket(ISocketHandler handler)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            // Create new socket
            m_socket = Syscall.socket(UnixAddressFamily.AF_INET, UnixSocketType.SOCK_STREAM, 0);

            // Allow address reuse
            Syscall.setsockopt(m_socket, UnixSocketProtocol.SOL_SOCKET, UnixSocketOptionName.SO_REUSEADDR, 1);

            var opts = Syscall.fcntl(m_socket, FcntlCommand.F_GETFL);
            if (opts < 0)
                throw new IOException($"Failed to get openflags from handle: {Stdlib.GetLastError()}");

            opts |= (int)OpenFlags.O_NONBLOCK;

            if (Syscall.fcntl(m_socket, FcntlCommand.F_SETFL, opts) < 0)
                throw new IOException($"Failed to set socket O_NOBLOCK: {Stdlib.GetLastError()}");

            m_handle = handler.MonitoredHandle(m_socket);
        }

        /// <summary>
        /// Creates a socket listening to the supplied endpoint
        /// </summary>
        /// <returns>The and accept async.</returns>
        /// <param name="endpoint">The endpoint we listen to.</param>
        /// <param name="backlog">The connection backlog</param>
        public void Bind(EndPoint endpoint, int backlog)
        {
            Sockaddr servaddr;

            if (endpoint is IPEndPoint ipe)
            {
                // Set up the IP address we are listening on
                servaddr = new SockaddrIn()
                {
                    sa_family = UnixAddressFamily.AF_INET,
                    sin_family = UnixAddressFamily.AF_INET,
                    sin_addr = new InAddr() { s_addr = BitConverter.ToUInt32(ipe.Address.GetAddressBytes(), 0) },
                    sin_port = Syscall.htons((ushort)ipe.Port)
                };
            }
            else if (endpoint is UnixEndPoint upe)
            {
                var isHidden = upe.Filename[0] == 0;
                servaddr = new SockaddrUn(upe.Filename.Substring(isHidden ? 1 : 0), isHidden);
            }
            else if (endpoint is System.Net.Sockets.UnixDomainSocketEndPoint udse)
            {
                var name = udse.ToString();
                var isHidden = name[0] == 0;
                servaddr = new SockaddrUn(name.Substring(isHidden ? 1 : 0), isHidden);
            }
            else
                throw new NotSupportedException($"EndPoint not supported: {endpoint}");

            // Bind so we are attached
            var ret = Syscall.bind(m_socket, servaddr);
            if (ret < 0)
                throw new IOException($"Failed to bind to endpoint: {Stdlib.GetLastError()}");

            ret = Syscall.listen(m_socket, backlog);
            if (ret < 0)
                throw new IOException($"Failed to set socket to listen: {Stdlib.GetLastError()}");
        }

        /// <summary>
        /// Releases all resource used by the <see cref="T:SockRock.ListenSocket"/> object.
        /// </summary>
        /// <remarks>Call <see cref="Dispose"/> when you are finished using the <see cref="T:SockRock.ListenSocket"/>. The
        /// <see cref="Dispose"/> method leaves the <see cref="T:SockRock.ListenSocket"/> in an unusable state. After
        /// calling <see cref="Dispose"/>, you must release all references to the <see cref="T:SockRock.ListenSocket"/>
        /// so the garbage collector can reclaim the memory that the <see cref="T:SockRock.ListenSocket"/> was occupying.</remarks>
        public void Dispose()
        {
            Syscall.close(m_socket);
        }

        /// <summary>
        /// Waits for a connection and accepts the socket
        /// </summary>
        /// <returns>The socket and endpoint.</returns>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task<KeyValuePair<long, EndPoint>> AcceptAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Try to read; we a non-blocking
                var addr = new Sockaddr();
                var ret = Syscall.accept(m_socket, addr);

                // If we did not get a socket
                if (ret < 0)
                {
                    var errno = Stdlib.GetLastError();
                    if (errno == Errno.EAGAIN || errno == Errno.EWOULDBLOCK)
                    {
                        await m_handle.WaitForReadAsync;

                        // Error code is normal operation code
                        continue;
                    }

                    throw new IOException($"Failed to accept socket: {errno}");
                }

                // Dummy endpoint
                var endpoint = new IPEndPoint(IPAddress.Any, 0);
                // If we get a real endpoint, use that
                if (addr is SockaddrIn sain)
                    endpoint = new IPEndPoint((long)sain.sin_addr.s_addr, sain.sin_port);

                return new KeyValuePair<long, EndPoint>(ret, endpoint);
            }

            throw new TaskCanceledException();
        }
    }
}
