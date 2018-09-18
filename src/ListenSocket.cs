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
    /// Implements functionality for listening to a socket
    /// </summary>
    public class ListenSocket : IDisposable
    {
        /// <summary>
        /// The socket we are bound to
        /// </summary>
        private readonly int m_socket;

        /// <summary>
        /// Initializes a new instance of the <see cref="T:SockRock.ListenSocket"/> class.
        /// </summary>
        public ListenSocket()
        {
            // Create new socket
            m_socket = Syscall.socket(UnixAddressFamily.AF_INET, UnixSocketType.SOCK_STREAM, 0);

            // Allow address reuse
            Syscall.setsockopt(m_socket, UnixSocketProtocol.SOL_SOCKET, UnixSocketOptionName.SO_REUSEADDR, 1);
        }

        /// <summary>
        /// Creates a socket listening to the supplied endpoint
        /// </summary>
        /// <returns>The and accept async.</returns>
        /// <param name="endpoint">The endpoint we listen to.</param>
        /// <param name="backlog">The connection backlog</param>
        public void Bind(EndPoint endpoint, int backlog)
        {
            SockaddrIn servaddr;

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
                throw new Exception($"EndPoint not supported: {endpoint}");
            }
            else
                throw new Exception($"EndPoint not supported: {endpoint}");

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
        public Task<KeyValuePair<long, EndPoint>> AcceptAsync(CancellationToken cancellationToken)
        {
            // TODO: Support the cancellationToken
            return Task.Run(() => {
                var addr = new SockaddrIn();
                var ret = Syscall.accept(m_socket, addr);
                if (ret < 0)
                    throw new IOException($"Failed to accept: {Stdlib.GetLastError()}");

                // Dummy endpoint
                var endpoint = new IPEndPoint(IPAddress.Any, 0);
                // If we get a real endpoint, use that
                if (addr is SockaddrIn sain)
                    endpoint = new IPEndPoint((long)sain.sin_addr.s_addr, sain.sin_port);

                return new KeyValuePair<long, EndPoint>(ret, endpoint);
            });
        }
    }
}
