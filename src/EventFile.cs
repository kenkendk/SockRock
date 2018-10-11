using System;
using System.IO;
using System.Runtime.InteropServices;
using Mono.Unix.Native;

namespace SockRock
{
    /// <summary>
    /// Wraps functionality for an event file
    /// </summary>
    public class EventFile : IDisposable
    {
        /// <summary>
        /// The eventfd handle
        /// </summary>
        public readonly int Handle;

        /// <summary>
        /// The read and write buffer
        /// </summary>
        private readonly GuardedHandle m_bufferHandle;

        /// <summary>
        /// The buffer to use
        /// </summary>
        private readonly byte[] m_buffer = new byte[8];

        /// <summary>
        /// A value indicating if the handle is disposed
        /// </summary>
        /// <value></value>
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// Creates a new EventFile
        /// </summary>
        public EventFile()
        {
            m_bufferHandle = new GuardedHandle(m_buffer);
            Handle = PInvoke.eventfd(0, 0);
        }

        /// <summary>
        /// Sends a write signal
        /// </summary>
        /// <param name="count">The counter to add</param>
        public void Write(ulong count)
        {
            Array.Copy(BitConverter.GetBytes(count), m_buffer, m_buffer.Length);
            var res = Syscall.write(Handle, m_bufferHandle.Address, (ulong)m_buffer.Length);
            if (res != m_buffer.Length)
                throw new IOException($"Failed to write {m_buffer.Length} bytes, wrote {res}: {Syscall.GetLastError()}");
        }

        /// <summary>
        /// Reads from the event file
        /// </summary>
        /// <returns>The counter</returns>
        public ulong Read()
        {
            var res = Syscall.read(Handle, m_bufferHandle.Address, (ulong)m_buffer.Length);
            if (res != m_buffer.Length)
                throw new IOException($"Failed to read {m_buffer.Length} bytes, read {res}: {Syscall.GetLastError()}");
            return BitConverter.ToUInt64(m_buffer, 0);
        }

        /// <summary>
        /// Disposes all resources
        /// </summary>
        public void Dispose()
        {
            if (!IsDisposed)
                Syscall.close(Handle);
            IsDisposed = true;
        }

        /// <summary>
        /// Wraps P/Invoke functionality
        /// </summary>
        private static class PInvoke
        {
            /// <summary>
            /// Creates a new kqueue descriptor
            /// </summary>
            /// <returns>The kqueue descriptor.</returns>
            [DllImport("libc", SetLastError = true)]
            public static extern int eventfd(uint initval, int flags);
        }        
    }
}