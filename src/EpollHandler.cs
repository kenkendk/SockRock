using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mono.Unix.Native;
using System.Runtime.InteropServices;

namespace SockRock
{
    /// <summary>
    /// Wrapping epoll functionality in a thread safe class
    /// </summary>
    public class EpollHandler : ISocketHandler
    {
        /// <summary>
        /// The events we listen to on a socket
        /// </summary>
        private static readonly EpollEvents LISTEN_EVENTS =
                        EpollEvents.EPOLLIN |     // Monitor read
                        EpollEvents.EPOLLOUT |    // Monitor write
                        EpollEvents.EPOLLRDHUP |  // Monitor read close
                        EpollEvents.EPOLLHUP;     // Monitor write close


        /// <summary>
        /// The list of monitored handles
        /// </summary>
        private readonly Dictionary<int, IMonitorItem> m_handles = new Dictionary<int, IMonitorItem>();

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
        /// The eventfile used to signal stop to epoll_wait
        /// </summary>
        private readonly EventFile m_eventfile = new EventFile();

        /// <summary>
        /// The thread that polls data
        /// </summary>
        private readonly Thread m_runnerThread;

        /// <summary>
        /// Initializes a new instance of the <see cref="T:SockRock.EPollHandler"/> class.
        /// </summary>
        public EpollHandler()
        {
            m_fd = Syscall.epoll_create(1);
            if (m_fd < 0)
                throw new Exception($"Call to {nameof(Syscall.epoll_create)} failed with code: {Stdlib.GetLastError()}");
            
            var ev = new EpollEvent()
            {
                fd = m_eventfile.Handle,
                events = LISTEN_EVENTS | EpollEvents.EPOLLET
            };

            var r = Syscall.epoll_ctl(m_fd, EpollOp.EPOLL_CTL_ADD, m_eventfile.Handle, ref ev);
            if (r != 0)
                throw new Exception($"Call to {nameof(Syscall.epoll_ctl)} failed with code {r}: {Stdlib.GetLastError()}");
            
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
        /// <returns>The handle.</returns>
        public SocketStream MonitorWithStream(int handle, bool closehandle = true)
        {
            return DoMonitor(handle, closehandle, (h, manager, action) => new SocketStream(h, manager, action));
        }

        /// <summary>
        /// Begins monitoring the handle and returns a stream for interacting with the handle
        /// </summary>
        /// <param name="handle">The handle to monitor</param>
        /// <param name="closehandle">If set to <c>true</c>, the socket handle is closed when the stream is disposed</param>
        /// <returns>The handle.</returns>
        public MonitoredHandle MonitoredHandle(int handle, bool closehandle = true)
        {
            return DoMonitor(handle, closehandle, (h, manager, action) => new MonitoredHandle(h, action));
        }

        /// <summary>
        /// Begins monitoring the handle and returns a stream for interacting with the handle
        /// </summary>
        /// <param name="handle">The handle to monitor</param>
        /// <param name="closehandle">If set to <c>true</c>, the socket handle is closed when the stream is disposed</param>
        /// <param name="creator">The method that creates the instance</param>
        /// <returns>The stream.</returns>
        private T DoMonitor<T>(int handle, bool closehandle, Func<int, BufferManager, Action, T> creator)
            where T : IMonitorItem
        {
            try
            {
                T res;
                lock (m_lock)
                {
                    //if (m_handles.Count >= MAX_HANDLES)
                        //return null;
                    if (m_handles.ContainsKey(handle))
                        throw new Exception("Handle is already registered?");

                    m_handles.Add(handle, res = creator(handle, m_bufferManager, () => this.DeregisterHandle(handle, closehandle)));
                }

                var ev = new EpollEvent()
                {
                    fd = handle,
                    events =  // Listen and use Edge Triggered
                        LISTEN_EVENTS | EpollEvents.EPOLLET
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
                if (closehandle)
                    Syscall.close(handle);
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
                fd = handle,
                events = LISTEN_EVENTS
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
            var events = new EpollEvent[MAX_EVENTS];
            var stopped = false;
            try
            {
                while (!stopped)
                {
                    DebugHelper.WriteLine("Waiting for epoll socket");
                    var count = Syscall.epoll_wait(m_fd, events, events.Length, -1);
                    if (count < 0)
                    {
                        DebugHelper.WriteLine("Stopped");
                        stopped = true;
                    }

                    for (var i = 0; i < count; i++)
                    {
                        var ev = events[i];
                        DebugHelper.WriteLine("Epoll[{2}] got {0} on {1}", ev.events, ev.fd, i);
                        IMonitorItem mi = null;
                        bool known = false;
                        lock(m_lock)
                            known = m_handles.TryGetValue(ev.fd, out mi);

                        if (known)
                        {
                            if (ev.events.HasFlag(EpollEvents.EPOLLIN) || ev.events.HasFlag(EpollEvents.EPOLLRDHUP))
                                mi.SignalReadReady();

                            if (ev.events.HasFlag(EpollEvents.EPOLLOUT) || ev.events.HasFlag(EpollEvents.EPOLLHUP))
                                mi.SignalWriteReady();
                        }
                        else if (ev.fd == m_eventfile.Handle)
                        {
                            DebugHelper.WriteLine("Got {0} for eventfile", ev.events);
                            if (ev.events.HasFlag(EpollEvents.EPOLLHUP) || ev.events.HasFlag(EpollEvents.EPOLLIN))
                                stopped = true;
                        }
                    }
                }
            }
            finally
            {
                DebugHelper.WriteLine("Epoll thread stopping");
                Syscall.close(m_fd);
            }
        }

        /// <summary>
        /// Stops the socket handler
        /// </summary>
        /// <param name="waittime">The grace period before the active connections are killed</param>
        public void Stop(TimeSpan waittime)
        {
            var endtime = DateTime.Now + waittime;
            while (true)
            {
                var stopped = false;
                lock (m_lock)
                    if (m_handles.Count == 0 || endtime < DateTime.Now)
                    {
                        Dispose();
                        stopped = true;
                    }

                if (stopped)
                {
                    var waitms = (int)Math.Max(TimeSpan.FromSeconds(1).TotalMilliseconds, (endtime - DateTime.Now).TotalMilliseconds);
                    m_runnerThread.Join(waitms);

                    if (m_runnerThread.IsAlive)
                        m_runnerThread.Abort();
                    return;
                }

                var seconds = Math.Min(1, (endtime - DateTime.Now).TotalSeconds);
                if (seconds > 0)
                    System.Threading.Thread.Sleep(TimeSpan.FromSeconds(seconds));
            }
        }


        /// <summary>
        /// Releases all resource used by the <see cref="T:SockRock.EpollHandler"/> object.
        /// </summary>
        /// <remarks>Call <see cref="Dispose"/> when you are finished using the
        /// <see cref="T:SockRock.EpollHandler"/>. The <see cref="Dispose"/> method leaves the
        /// <see cref="T:SockRock.EpollHandler"/> in an unusable state. After calling
        /// <see cref="Dispose"/>, you must release all references to the
        /// <see cref="T:SockRock.EpollHandler"/> so the garbage collector can reclaim the
        /// memory that the <see cref="T:SockRock.EpollHandler"/> was occupying.</remarks>
        public void Dispose()
        {
            DebugHelper.WriteLine("Closing epoll socket");
            m_eventfile?.Write(1);
            m_eventfile?.Dispose();
            DebugHelper.WriteLine("Closed epoll socket");

            // Drop all active connections, as we no longer get signals
            lock (m_lock)
                foreach (var k in m_handles)
                    Task.Run(() => k.Value.Dispose());
        }

    }
}
