using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Mono.Unix.Native;

namespace SockRock
{
    /// <summary>
    /// Wrapping kqueue functionality in a thread safe class
    /// </summary>
    public class KqueueHandler : ISocketHandler
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
        /// The maximum number of events to handle in a single call to kevent
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
        /// Gets the number of monitored handles
        /// </summary>
        public int HandleCount => m_handles.Count;

        /// <summary>
        /// Initializes a new instance of the <see cref="T:SockRock.KqueueHandler"/> class.
        /// </summary>
        public KqueueHandler()
        {
            m_fd = PInvoke.kqueue();
            if (m_fd < 0)
                throw new IOException($"Failed to create new kqueue: {Stdlib.GetLastError()}");
                
            m_runnerThread = new Thread(RunPoll);
            m_runnerThread.Start();
        }

        /// <summary>
        /// Begins monitoring the handle and returns a stream for interacting with the handle
        /// </summary>
        /// <param name="handle">The handle to monitor</param>
        /// <param name="closehandle">If set to <c>true</c>, the socket handle is closed when the stream is disposed</param>
        /// <returns>The handle.</returns>
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

                var kev = new struct_kevent[] {
                    new struct_kevent() {
                        ident = handle,
                        filter = EVFILT.READ | EVFILT.WRITE,
                        flags = EV.ADD | EV.CLEAR,
                        fflags = 0,
                        data = IntPtr.Zero,
                        udata = IntPtr.Zero
                    }
                };

                var ret = PInvoke.kevent(m_fd, kev, 1, null, 0, null);
                if (ret < 0)
                    throw new IOException($"Failed to add handle to kqueue: {Stdlib.GetLastError()}");
                if (kev[0].flags.HasFlag(EV.ERROR))
                    throw new IOException($"Failed to add handle to kqueue: {kev[0].data}");

                var opts = Syscall.fcntl(handle, FcntlCommand.F_GETFL);
                if (opts < 0)
                    throw new IOException($"Failed to get openflags from handle: {Stdlib.GetLastError()}");

                opts |= (int)OpenFlags.O_NONBLOCK;

                if (Syscall.fcntl(handle, FcntlCommand.F_SETFL, opts) < 0)
                    throw new IOException($"Failed to set socket O_NOBLOCK: {Stdlib.GetLastError()}");
                    
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

            var kev = new struct_kevent[] {
                    new struct_kevent() {
                        ident = handle,
                        filter = EVFILT.READ | EVFILT.WRITE,
                        flags = EV.DELETE,
                        fflags = 0,
                        data = IntPtr.Zero,
                        udata = IntPtr.Zero

                    }
                };

            var ret = PInvoke.kevent(m_fd, kev, 1, null, 0, null);
            if (ret < 0)
                throw new IOException($"Failed to remove handle from kqueue: {Stdlib.GetLastError()}");
            if (kev[0].flags.HasFlag(EV.ERROR))
                throw new IOException($"Failed remove add handle from kqueue: {kev[0].data}");

            if (close)
                Syscall.close(handle);
        }

        /// <summary>
        /// Runs the poll task.
        /// </summary>
        private void RunPoll()
        {
            // TODO: We need to be able to stop this

            var events = new struct_kevent[MAX_EVENTS];
            var timeout = new[] { new Timespec() { tv_sec = 10 } };

            while (true)
            {
                var count = PInvoke.kevent(m_fd, null, 0, events, events.Length, timeout);
                for (var i = 0; i < count; i++)
                {
                    if (m_handles.TryGetValue(events[i].ident, out var stream))
                    {
                        var ev = events[i];
                        if (ev.filter.HasFlag(EVFILT.READ) || ev.flags.HasFlag(EV.EOF))
                            stream.m_readDataSignal.TrySetResult(true);

                        if (ev.filter.HasFlag(EVFILT.WRITE) || ev.flags.HasFlag(EV.EOF))
                            stream.m_writeDataSignal.TrySetResult(true);
                    }
                }
            }
        }

        /// <summary>
        /// Creates a new listen socket bound to the given endpoint
        /// </summary>
        /// <returns>The listen socket.</returns>
        /// <param name="endPoint">The end point to listen to.</param>
        /// <param name="backlog">The connection backlog</param>
        public AsyncListenSocket Listen(System.Net.EndPoint endPoint, int backlog)
        {
            var sock = new AsyncListenSocket(this);
            sock.Bind(endPoint, backlog);
            return sock;
        }

        /// <summary>
        /// Releases all resource used by the <see cref="T:SockRock.KqueueHandler"/> object.
        /// </summary>
        /// <remarks>Call <see cref="Dispose"/> when you are finished using the <see cref="T:SockRock.KqueueHandler"/>. The
        /// <see cref="Dispose"/> method leaves the <see cref="T:SockRock.KqueueHandler"/> in an unusable state. After
        /// calling <see cref="Dispose"/>, you must release all references to the <see cref="T:SockRock.KqueueHandler"/>
        /// so the garbage collector can reclaim the memory that the <see cref="T:SockRock.KqueueHandler"/> was occupying.</remarks>
        public void Dispose()
        {
            Syscall.close(m_fd);

            // Drop all active connections, as we no longer get signals
            lock (m_lock)
                foreach (var k in m_handles)
                    k.Value.Dispose();
        }


        /// <summary>
        /// The communication structure used by kqueue
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct struct_kevent
        {
            /// <summary>
            /// identifier for this event
            /// </summary>
            public int ident;
            /// <summary>
            /// filter for event
            /// </summary>
            public EVFILT filter;
            /// <summary>
            /// action flags for kqueue
            /// </summary>
            public EV flags;
            /// <summary>
            /// filter flag value
            /// </summary>
            public uint fflags;
            /// <summary>
            /// filter data value
            /// </summary>
            public IntPtr data;
            /// <summary>
            /// opaque user data identifier
            /// </summary>
            public IntPtr udata;
        }

        /// <summary>
        /// Event filter values
        /// </summary>
        [Flags]
        private enum EVFILT : short
        {
            ///<summary>Read events</summary>
            READ = -1,
            ///<summary>Write events</summary>
            WRITE = -2,
            ///<summary>Attached to aio requests</summary>
            AIO = -3,
            ///<summary>Attached to vnodes</summary>
            VNODE = -4,
            ///<summary>Attached to struct proc</summary>
            PROC = -5,
            ///<summary>Attached to struct proc</summary>
            SIGNAL = -6,
            ///<summary>Timer events</summary>
            TIMER = -7,
            ///<summary>Mach portsets</summary>
            MACHPORT = -8,
            ///<summary>Filesystem events</summary>
            FS = -9,
            ///<summary>User events</summary>
            USER = -10,
            ///<summary>Unused</summary>
            UNUSED = -11,
            ///<summary>Virtual memory events</summary>
            VM = -12,
            ///<summary>Exception events</summary>
            EXCEPT = -15
        }

        /// <summary>
        /// Event actions and flags
        /// </summary>
        [Flags]
        private enum EV : ushort
        {
            /* Actions */
            /// <summary>Add event to kq (implies enable)</summary>
            ADD = 0x0001,
            /// <summary>Delete event from kq</summary>
            DELETE = 0x0002,
            /// <summary>Enable event</summary>
            ENABLE = 0x0004,
            /// <summary>Disable event (not reported)</summary>
            DISABLE = 0x0008,

            /* flags */
            /// <summary>Only report one occurrence</summary>
            ONESHOT = 0x0010,
            /// <summary>Clear event state after reporting</summary>
            CLEAR = 0x0020,
            /// <summary>
            /// force immediate event output with or without EV_ERROR.
            /// Use KEVENT_FLAG_ERROR_EVENTS on syscalls supporting flags
            /// </summary>
            RECEIPT = 0x0040,

            /// <summary>Disable event after reporting</summary>
            DISPATCH = 0x0080,
            /// <summary>Unique kevent per udata value</summary>
            UDATA_SPECIFIC = 0x0100,

            /// <summary>
            /// When used in combination with EV_DELETE
            /// will defer delete until udata-specific
            /// event enabled. EINPROGRESS will be
            /// returned to indicate the deferral
            /// </summary>
            DISPATCH2 = (DISPATCH | UDATA_SPECIFIC),

            /// <summary>
            /// Report that source has vanished.
            /// Only valid with <see href="EV.DISPATCH2" />
            /// </summary>
            VANISHED = 0x0200,

            /// <summary>Reserved by system</summary>
            SYSFLAGS = 0xF000,
            /// <summary>Filter-specific flag</summary>
            FLAG0 = 0x1000,
            /// <summary>Filter-specific flag</summary>
            FLAG1 = 0x2000,

            /* returned values */
            /// <summary>EOF detected</summary>
            EOF = 0x8000,
            /// <summary>Error, data contains errno</summary>
            ERROR = 0x4000
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
            public static extern int kqueue();

            /// <summary>
            /// Modifies the list of monitored handles and returns the events that happened since
            /// </summary>
            /// <returns>The number of changes added.</returns>
            /// <param name="kq">The kqueue descriptor.</param>
            /// <param name="changelist">The list of changes to the descriptor.</param>
            /// <param name="nchanges">The length of the <paramref name="changelist"/> array.</param>
            /// <param name="eventlist">The events that were discovered.</param>
            /// <param name="nevents">The length of the <paramref name="eventlist"/> array.</param>
            /// <param name="timeout">The timeout parameter, only supports a single timeout, but the array allows us to pass null.</param>
            [DllImport("libc", SetLastError = true)]
            public static extern int kevent(int kq, struct_kevent[] changelist, int nchanges, struct_kevent[] eventlist, int nevents, Mono.Unix.Native.Timespec[] timeout);

        }
    }
}
