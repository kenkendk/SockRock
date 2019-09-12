using System;
using System.IO;
using Mono.Unix.Native;

namespace SockRock
{
    /// <summary>
    /// Implementation of a unix domain socket, using native methods
    /// </summary>
    public class NativeDomainSocket : IDisposable
    {
        /// <summary>
        /// The handle this socket is bound to
        /// </summary>
        public int Handle { get; private set; }

        /// <summary>
        /// Flag indicating if the socket is disposed
        /// </summary>
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// The path for the socket
        /// </summary>
        public string Path { get; private set; }

        /// <summary>
        /// A flag indicating if the path is hidden
        /// </summary>
        public bool Hidden { get; private set; }

        /// <summary>
        /// Constructs a new domain socket
        /// </summary>
        /// <param name="path">The socket path</param>
        /// <param name="hidden">A flag indicating if the socket is using a hidden (non-filesystem) path</param>
        public NativeDomainSocket(string path, bool hidden)
        {
            Path = path;
            Hidden = hidden;

            Handle = Syscall.socket(UnixAddressFamily.AF_UNIX, UnixSocketType.SOCK_STREAM, 0);
            if (Handle < 0)
                throw new IOException("Failed to create a socket");
        }

        /// <summary>
        /// Creates the socket address
        /// </summary>
        /// <returns>The socket address</returns>
        private Sockaddr CreateAddr()
        {
            return new SockaddrUn(Path, Hidden);
        }

        /// <summary>
        /// Connects to the socket
        /// </summary>
        public void Connect()
        {
            var res = Syscall.connect(Handle, CreateAddr());
            if (res != 0)
                throw new IOException($"Failed to connect socket (handle={Handle}, code={res}): {Syscall.GetLastError()}");
        }

        /// <summary>
        /// Binds the socket
        /// </summary>
        public void Bind()
        {
            var addr = CreateAddr();
            var res = Syscall.bind(Handle, addr);
            if (res != 0)
                throw new IOException($"Failed to bind socket (handle={Handle}, code={res}): {Syscall.GetLastError()}");

            // Remove the file, if it exists
            if (!Hidden)
                Syscall.unlink(Path);

        }

        /// <summary>
        /// Listens
        /// </summary>
        /// <param name="backlog">The backlog</param>
        public void Listen(int backlog = 1)
        {
            var res = Syscall.listen(Handle, backlog);
            if (res != 0)
                throw new IOException($"Failed to listen on socket (handle={Handle}, code={res}): {Syscall.GetLastError()}");
        }

        /// <summary>
        /// Disposes the instance and closes the socket
        /// </summary>
        public void Dispose()
        {
            Syscall.close(Handle);
        }
    }
}