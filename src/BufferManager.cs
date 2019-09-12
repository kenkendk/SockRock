using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SockRock
{
    /// <summary>
    /// Class to handle allocation and pinning of buffers
    /// </summary>
    public class BufferManager : IDisposable
    {
        /// <summary>
        /// The maxmimum number of buffers to have allocated without being used.
        /// </summary>
        private const int MAX_EXTRA_BUFFERS = 10;

        /// <summary>
        /// Flag indicating if this instance is disposed
        /// </summary>
        private bool m_isDisposed;

        /// <summary>
        /// The allocated buffers
        /// </summary>
        private readonly Queue<KeyValuePair<byte[], GCHandle>> m_buffers = new Queue<KeyValuePair<byte[], GCHandle>>();

        /// <summary>
        /// The lock object
        /// </summary>
        private readonly object m_lock = new object();

        /// <summary>
        /// The size of the allocated buffers
        /// </summary>
        private readonly int m_buffer_size;

        /// <summary>
        /// Initializes a new instance of the <see cref="T:SockRock.BufferManager"/> class.
        /// </summary>
        public BufferManager()
            : this(10 * 1024)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="T:SockRock.BufferManager"/> class.
        /// </summary>
        /// <param name="buffersize">The size of the buffers the instance manages.</param>
        public BufferManager(int buffersize)
        {
            m_buffer_size = buffersize;
        }

        /// <summary>
        /// Obtains a buffer
        /// </summary>
        /// <returns>The acquired buffer.</returns>
        public KeyValuePair<byte[], GCHandle> Acquire()
        {
            lock (m_lock)
            {
                if (m_isDisposed)
                    throw new ObjectDisposedException(nameof(BufferManager));

                if (m_buffers.Count > 0)
                    return m_buffers.Dequeue();

                var buffer = new byte[m_buffer_size];
                return new KeyValuePair<byte[], GCHandle>(buffer, GCHandle.Alloc(buffer, GCHandleType.Pinned));
            }
        }

        /// <summary>
        /// Returns a buffer to the pool
        /// </summary>
        /// <returns>An empty buffer.</returns>
        /// <param name="handle">The item to release.</param>
        public KeyValuePair<byte[], GCHandle> Release(KeyValuePair<byte[], GCHandle> handle)
        {
            if (handle.Key != null)
                lock (m_lock)
                    if (m_isDisposed)
                    {
                        handle.Value.Free();
                        throw new ObjectDisposedException(nameof(BufferManager));
                    }
                    else
                    {
                        m_buffers.Enqueue(handle);

                        while (m_buffers.Count > MAX_EXTRA_BUFFERS)
                            m_buffers.Dequeue().Value.Free();
                    }

            return new KeyValuePair<byte[], GCHandle>(null, default(GCHandle));
        }

        /// <summary>
        /// Releases all resource used by the
        /// <see cref="T:Ceen.Httpd.Cli.Runner.SubProcess.EPollHandler.BufferManager"/> object.
        /// </summary>
        /// <remarks>Call <see cref="Dispose"/> when you are finished using the
        /// <see cref="T:Ceen.Httpd.Cli.Runner.SubProcess.EPollHandler.BufferManager"/>. The <see cref="Dispose"/>
        /// method leaves the <see cref="T:Ceen.Httpd.Cli.Runner.SubProcess.EPollHandler.BufferManager"/> in an
        /// unusable state. After calling <see cref="Dispose"/>, you must release all references to the
        /// <see cref="T:Ceen.Httpd.Cli.Runner.SubProcess.EPollHandler.BufferManager"/> so the garbage collector can
        /// reclaim the memory that the <see cref="T:Ceen.Httpd.Cli.Runner.SubProcess.EPollHandler.BufferManager"/>
        /// was occupying.</remarks>
        public void Dispose()
        {
            m_isDisposed = true;

            lock (m_lock)
                while (m_buffers.Count > MAX_EXTRA_BUFFERS)
                    m_buffers.Dequeue().Value.Free();
        }
    }
}
