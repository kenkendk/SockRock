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
        private int m_socket = -1;

        /// <summary>
        /// Initializes a new instance of the <see cref="T:SockRock.ListenSocket"/> class.
        /// </summary>
        public ListenSocket()
        {
        }

        /// <summary>
        /// Helper method to set up the socket when calling bind
        /// </summary>
        /// <param name="family">The address family to use</param>
        private void SetupSocket(UnixAddressFamily family)
        {
            if (m_socket != -1)
                throw new InvalidOperationException("The socket is already initialized");

            // Create the socket
            m_socket = Syscall.socket(family, UnixSocketType.SOCK_STREAM, 0);
            // Allow address reuse
            Syscall.setsockopt(m_socket, UnixSocketProtocol.SOL_SOCKET, UnixSocketOptionName.SO_REUSEADDR, 1);
        }

        /// <summary>
        /// Creates a socket listening to the supplied endpoint
        /// </summary>
        /// <param name="endpoint">The endpoint we listen to.</param>
        /// <param name="backlog">The connection backlog</param>
        public void Bind(EndPoint endpoint, int backlog)
            => Bind(endpoint, backlog, true);

        /// <summary>
        /// Creates a socket listening to the supplied endpoint
        /// </summary>
        /// <param name="endpoint">The endpoint we listen to.</param>
        /// <param name="backlog">The connection backlog</param>
        /// <param name="autodeletedomainsocket">A flag indicating if the domain socket should be attempted deleted</param>
        public void Bind(EndPoint endpoint, int backlog, bool autodeletedomainsocket)
        {
            try
            {
                Sockaddr servaddr;
                var filepath = string.Empty;

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

                    // Prepare the socket for TCP/IP
                    SetupSocket(UnixAddressFamily.AF_INET);
                }
                else if (endpoint is UnixEndPoint upe)
                {
                    var isHidden = upe.Filename[0] == 0;
                    if (!isHidden)
                        filepath = upe.Filename;
                    servaddr = new SockaddrUn(upe.Filename.Substring(isHidden ? 1 : 0), isHidden);
                    // Prepare the socket for Unix Domain Socket
                    SetupSocket(UnixAddressFamily.AF_UNIX);
                }
                else if (endpoint is System.Net.Sockets.UnixDomainSocketEndPoint udse)
                {
                    var name = udse.ToString();
                    var isHidden = name[0] == 0 || name[0] == '@';
                    if (!isHidden)
                        filepath = name;
                    servaddr = new SockaddrUn(name.Substring(isHidden ? 1 : 0), isHidden);
                    // Prepare the socket for Unix Domain Socket
                    SetupSocket(UnixAddressFamily.AF_UNIX);
                }
                else
                    throw new NotSupportedException($"EndPoint not supported: {endpoint.GetType()}");

                // Bind so we are attached
                var ret = Syscall.bind(m_socket, servaddr);
                if (ret < 0)
                {
                    // Save original error code
                    var err = Stdlib.GetLastError();

                    // Handle auto-delete of sockets
                    if (autodeletedomainsocket && err == Errno.EADDRINUSE && !string.IsNullOrWhiteSpace(filepath) && File.Exists(filepath) && new FileInfo(filepath).Length == 0)
                    {
                        // Ensure that no other process is using the socket
                        var csock = Syscall.socket(UnixAddressFamily.AF_UNIX, UnixSocketType.SOCK_STREAM, 0);
                        ret = Syscall.connect(csock, servaddr);
                        Syscall.close(csock);

                        // If we cannot connect, assume it is broken
                        if (ret != 0)
                        {
                            // Try to remove the file
                            var failed = false;
                            try { File.Delete(filepath); }
                            catch { failed = true; }

                            // If the file is removed, try binding again
                            if (!failed)
                            {
                                Syscall.close(m_socket);
                                m_socket = -1;
                                Bind(endpoint, backlog, false);
                                return;
                            }
                        }
                    }

                    throw new IOException($"Failed to bind to endpoint: {err}");
                }

                ret = Syscall.listen(m_socket, backlog);
                if (ret < 0)
                    throw new IOException($"Failed to set socket to listen: {Stdlib.GetLastError()}");
            }
            catch
            {
                // Avoid leaving the listensocket in an invalid state
                if (m_socket != -1)
                {
                    try { Syscall.close(m_socket); }
                    catch {}

                    m_socket = -1;
                }
                throw;
            }
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
            if (m_socket != -1) 
            {
                try 
                {
                    Syscall.close(m_socket);
                }
                finally
                {
                    m_socket = -1;
                }
            }
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
