using System.Net.Sockets;
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
        private int m_socket = -1;

        /// <summary>
        /// A monitor used to get async triggers from the monitor
        /// </summary>
        private MonitoredHandle m_handle;

        /// <summary>
        /// The handler to use
        /// </summary>
        private readonly ISocketHandler m_handler;

        /// <summary>
        /// Initializes a new instance of the <see cref="T:SockRock.ListenSocket"/> class.
        /// </summary>
        public AsyncListenSocket(ISocketHandler handler)
        {
            m_handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        /// <summary>
        /// Helper method to set up the socket when calling bind
        /// </summary>
        /// <param name="family">The address family to use</param>
        private void SetupSocket(UnixAddressFamily family)
        {
            if (m_socket != -1)
                throw new InvalidOperationException("The socket is already initialized");

            // Create new socket
            m_socket = Syscall.socket(family, UnixSocketType.SOCK_STREAM, 0);

            // Allow address reuse
            Syscall.setsockopt(m_socket, UnixSocketProtocol.SOL_SOCKET, UnixSocketOptionName.SO_REUSEADDR, 1);

            var opts = Syscall.fcntl(m_socket, FcntlCommand.F_GETFL);
            if (opts < 0)
                throw new IOException($"Failed to get openflags from handle: {Stdlib.GetLastError()}");

            opts |= (int)OpenFlags.O_NONBLOCK;

            if (Syscall.fcntl(m_socket, FcntlCommand.F_SETFL, opts) < 0)
                throw new IOException($"Failed to set socket O_NOBLOCK: {Stdlib.GetLastError()}");

            m_handle = m_handler.MonitoredHandle(m_socket);
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

                    SetupSocket(UnixAddressFamily.AF_INET);
                }
                else if (endpoint is UnixEndPoint upe)
                {
                    var isHidden = upe.Filename[0] == 0;
                    if (!isHidden)
                        filepath = upe.Filename;
                    servaddr = new SockaddrUn(upe.Filename.Substring(isHidden ? 1 : 0), isHidden);
                    SetupSocket(UnixAddressFamily.AF_UNIX);
                }
                else if (endpoint is System.Net.Sockets.UnixDomainSocketEndPoint udse)
                {
                    var name = udse.ToString();
                    var isHidden = name[0] == 0 || name[0] == '@';
                    if (!isHidden)
                        filepath = name;
                    servaddr = new SockaddrUn(name.Substring(isHidden ? 1 : 0), isHidden);
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
