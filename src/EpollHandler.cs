using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Mono.Unix.Native;

namespace SockRock
{
    /// <summary>
    /// Wrapping epoll functionality in a thread safe class
    /// </summary>
    public class EpollHandler : ISocketHandler
    {
        /// <summary>
        /// The list of monitored handles
        /// </summary>
        private readonly Dictionary<int, SocketStream> m_handles = new Dictionary<int, SocketStream>();

        /// <summary>
        /// The buffer manager
        /// </summary>
        private readonly BufferManager m_bufferManager = new BufferManager();

        /// <summary>
        /// The maximum number of events returned from one call to epoll
        /// </summary>
        public const int MAX_EVENTS = 100;

        /// <summary>
        /// The size of the read/write buffers
        /// </summary>
        private const int BUFFER_SIZE = 10 * 1024;

        /// <summary>
        /// The epoll file descriptor
        /// </summary>
        private readonly int m_fd;

        /// <summary>
        /// The lock used to guard the data structures
        /// </summary>
        private readonly object m_lock = new object();

        /// <summary>
        /// The thread that polls data
        /// </summary>
        private readonly Thread m_runnerThread;

        /// <summary>
        /// Initializes a new instance of the <see cref="T:Ceen.Httpd.Cli.Runner.SubProcess.EPollHandler"/> class.
        /// </summary>
        public EpollHandler()
        {
            m_fd = Syscall.epoll_create(0);
            m_runnerThread = new Thread(RunPoll);
            m_runnerThread.Start();
        }

        /// <summary>
        /// Gets the number of monitored handles
        /// </summary>
        public int HandleCount => m_handles.Count;

        /// <summary>
        /// Begins monitoring the handle and returns a stream for interacting with the handle
        /// </summary>
        /// <param name="handle">The handle to monitor</param>
        /// <param name="closehandle">If set to <c>true</c>, the socket handle is closed when the stream is disposed</param>
        /// <returns>The stream.</returns>
        public SocketStream MonitorHandle(int handle, bool closehandle = true)
        {
            try
            {
                SocketStream res;
                lock (m_lock)
                {
                    //if (m_handles.Count >= MAX_HANDLES)
                        //return null;
                    if (m_handles.ContainsKey(handle))
                        throw new Exception("Handle is already registered?");

                    m_handles.Add(handle, res = new SocketStream(handle, m_bufferManager, () => this.DeregisterHandle(handle, closehandle)));
                }

                var ev = new EpollEvent()
                {
                    events =
                        EpollEvents.EPOLLIN |  // Monitor read
                        EpollEvents.EPOLLOUT | // Monitor write
                        EpollEvents.EPOLLET |  // Monitor read close
                        EpollEvents.EPOLLRDHUP // Monitor write close
                };
                var opts = Syscall.fcntl(handle, FcntlCommand.F_GETFL);
                if (opts < 0)
                    throw new IOException($"Failed to get openflags from handle: {Stdlib.GetLastError()}");
                
                opts |= (int)OpenFlags.O_NONBLOCK;

                if (Syscall.fcntl(handle, FcntlCommand.F_SETFL, opts) < 0)
                    throw new IOException($"Failed to set socket O_NOBLOCK: {Stdlib.GetLastError()}");

                var r = Syscall.epoll_ctl(m_fd, EpollOp.EPOLL_CTL_ADD, handle, ref ev);
                if (r != 0)
                    throw new Exception($"Call to {nameof(Syscall.epoll_ctl)} failed with code {r}: {Stdlib.GetLastError()}");

                return res;
            }
            catch
            {
                // Make sure we remove the handle from the list as we are not handling it
                lock (m_lock)
                    m_handles.Remove(handle);
                throw;
            }
        }

        /// <summary>
        /// Deregisters the handle from the monitor.
        /// </summary>
        /// <param name="handle">The handle to de-register.</param>
        /// <param name="close">If set to <c>true</c>, close the socket after deregistering.</param>
        private void DeregisterHandle(int handle, bool close)
        {
            lock (m_lock)
                if (m_handles.TryGetValue(handle, out var stream))
                    m_handles.Remove(handle);

            var ev = new EpollEvent()
            {
                events =
                    EpollEvents.EPOLLIN |  // Monitor read
                    EpollEvents.EPOLLOUT | // Monitor write
                    EpollEvents.EPOLLET |  // Monitor read close
                    EpollEvents.EPOLLRDHUP // Monitor write close
            };
            var r = Syscall.epoll_ctl(m_fd, EpollOp.EPOLL_CTL_DEL, handle, ref ev);
            if (r != 0)
                throw new Exception($"Call to {nameof(Syscall.epoll_ctl)} failed with code {r}: {Stdlib.GetLastError()}");

            if (close)
                Syscall.close(handle);
        }

        /// <summary>
        /// Runs the poll task.
        /// </summary>
        private void RunPoll()
        {
            // TODO: We need to be able to stop this

            var events = new EpollEvent[MAX_EVENTS];
            while (true)
            {
                var count = Syscall.epoll_wait(m_fd, events, events.Length, -1);
                for (var i = 0; i < count; i++)
                {
                    if (m_handles.TryGetValue(events[i].fd, out var stream))
                    {
                        var ev = events[i];
                        if (ev.events.HasFlag(EpollEvents.EPOLLIN) || ev.events.HasFlag(EpollEvents.EPOLLRDHUP))
                            stream.m_readDataSignal.TrySetResult(true);

                        if (ev.events.HasFlag(EpollEvents.EPOLLOUT) || ev.events.HasFlag(EpollEvents.EPOLLHUP))
                            stream.m_writeDataSignal.TrySetResult(true);
                    }
                }
            }
        }

        /// <summary>
        /// Releases all resource used by the <see cref="T:Ceen.Httpd.Cli.Runner.SubProcess.EpollHandler"/> object.
        /// </summary>
        /// <remarks>Call <see cref="Dispose"/> when you are finished using the
        /// <see cref="T:Ceen.Httpd.Cli.Runner.SubProcess.EpollHandler"/>. The <see cref="Dispose"/> method leaves the
        /// <see cref="T:Ceen.Httpd.Cli.Runner.SubProcess.EpollHandler"/> in an unusable state. After calling
        /// <see cref="Dispose"/>, you must release all references to the
        /// <see cref="T:Ceen.Httpd.Cli.Runner.SubProcess.EpollHandler"/> so the garbage collector can reclaim the
        /// memory that the <see cref="T:Ceen.Httpd.Cli.Runner.SubProcess.EpollHandler"/> was occupying.</remarks>
        public void Dispose()
        {
            Syscall.close(m_fd);

            // Drop all active connections, as we no longer get signals
            lock (m_lock)
                foreach (var k in m_handles)
                    k.Value.Dispose();
        }
    }
}
