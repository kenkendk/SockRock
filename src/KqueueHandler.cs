using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Mono.Unix.Native;
using INTPTR = System.IntPtr;
//using INTPTR = System.Int32;
using IDENTTYPE = System.UInt64;
//using IDENTTYPE = System.UInt32;

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
        private readonly Dictionary<IDENTTYPE, IMonitorItem> m_handles = new Dictionary<IDENTTYPE, IMonitorItem>();

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
        /// The signal used to communicate with the kevent thread on exit
        /// </summary>
        private readonly UserEvent m_closeSignal;

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
            DebugHelper.WriteLine("{0}: Starting kqueue", System.Diagnostics.Process.GetCurrentProcess().Id);
            m_fd = PInvoke.kqueue();
            if (m_fd < 0)
                throw new IOException($"Failed to create new kqueue: {Stdlib.GetLastError()}");

            m_closeSignal = new UserEvent(m_fd, 42);

            m_runnerThread = new Thread(RunPoll);
            m_runnerThread.Start();
        }

        /// <summary>
        /// Begins monitoring the handle and returns a stream for interacting with the handle
        /// </summary>
        /// <param name="handle">The handle to monitor</param>
        /// <param name="closehandle">If set to <c>true</c>, the socket handle is closed when the stream is disposed</param>
        /// <param name="creator">The method that creates the instance</param>
        /// <returns>The handle.</returns>
        private T DoMonitor<T>(int handle, bool closehandle, Func<int, BufferManager, Action, T> creator)
            where T : IMonitorItem
        {
            // Typecast if required
            var h = (IDENTTYPE)handle;
            try
            {
                T res;
                lock (m_lock)
                {
                    //if (m_handles.Count >= MAX_HANDLES)
                    //return null;
                    if (m_handles.ContainsKey(h))
                        throw new Exception("Handle is already registered?");

                    m_handles.Add(h, res =  creator(handle, m_bufferManager, () => this.DeregisterHandle(handle, closehandle)));
                }

                var ret = PInvoke.kevent(m_fd, h, EVFILT.READ | EVFILT.WRITE, EV.ADD | EV.CLEAR);
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
                    m_handles.Remove(h);
                if (closehandle)
                    Syscall.close(handle);
                throw;
            }
        }        

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
        /// Deregisters the handle from the monitor.
        /// </summary>
        /// <param name="handle">The handle to de-register.</param>
        /// <param name="close">If set to <c>true</c>, close the socket after deregistering.</param>
        private void DeregisterHandle(int handle, bool close)
        {
            // Typecast if required
            var h = (IDENTTYPE)handle;

            lock (m_lock)
                if (m_handles.TryGetValue(h, out var stream))
                    m_handles.Remove(h);

            var ret = PInvoke.kevent(m_fd, h, EVFILT.READ | EVFILT.WRITE, EV.DELETE);
            if (close)
                Syscall.close(handle);
        }

        /// <summary>
        /// Runs the poll task.
        /// </summary>
        private void RunPoll()
        {
            var events = new struct_kevent[MAX_EVENTS];
            var timeout = new[] { new Timespec() { tv_sec = 10 } };
            var stopped = false;

            DebugHelper.WriteLine("{0}: Running kqueue monitor", System.Diagnostics.Process.GetCurrentProcess().Id);
            try
            {
                while (!stopped)
                {
                    var count = PInvoke.kevent(m_fd, null, 0, events, events.Length, timeout);
                    if (count < 0)
                    {
                        DebugHelper.WriteLine($"{System.Diagnostics.Process.GetCurrentProcess().Id}: kqueue stopped: {Stdlib.GetLastError()}");
                        stopped = true;
                    }

                    for (var i = 0; i < count; i++)
                    {
                        var ev = events[i];
                        DebugHelper.WriteLine("{0}: Got signal {2} for {1}", System.Diagnostics.Process.GetCurrentProcess().Id, ev.ident, ev.flags);
                        if (m_handles.TryGetValue(ev.ident, out var mi))
                        {
                            if (ev.filter.HasFlag(EVFILT.READ) || ev.flags.HasFlag(EV.EOF))
                                mi.SignalReadReady();

                            if (ev.filter.HasFlag(EVFILT.WRITE) || ev.flags.HasFlag(EV.EOF))
                                mi.SignalWriteReady();
                        }
                        else if (ev.ident == m_closeSignal.Handle && events[i].filter.HasFlag(EVFILT.USER))
                        {
                            DebugHelper.WriteLine("{0}: Got exit signal!", System.Diagnostics.Process.GetCurrentProcess().Id);
                            stopped = true;
                        }
                    }
                }
            }
            catch(Exception ex)
            {
                DebugHelper.WriteLine("{0}: kqueue crash", System.Diagnostics.Process.GetCurrentProcess().Id);
            }
            finally
            {
                DebugHelper.WriteLine("{0}: Closing kqueue", System.Diagnostics.Process.GetCurrentProcess().Id);
                Syscall.close(m_fd);
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
            DebugHelper.WriteLine("{0}: In dispose", System.Diagnostics.Process.GetCurrentProcess().Id);
            m_closeSignal?.Set();
            DebugHelper.WriteLine("{0}: Stop signalled, terminating any attached streams", System.Diagnostics.Process.GetCurrentProcess().Id);

            // Drop all active connections, as we no longer get signals
            lock (m_lock)
                foreach (var k in m_handles)
                    k.Value.Dispose();

            // Close the handle as well
            Syscall.close(m_fd);
            DebugHelper.WriteLine("{0}: Dispose completed", System.Diagnostics.Process.GetCurrentProcess().Id);

        }

        /// <summary>
        /// Stops the socket handler
        /// </summary>
        /// <param name="waittime">The grace period before the active connections are killed</param>
        public void Stop(TimeSpan waittime)
        {
            DebugHelper.WriteLine("{0}: Stop requested", System.Diagnostics.Process.GetCurrentProcess().Id);

            var endtime = DateTime.Now + waittime;
            while (true)
            {
                var stopped = false;
                lock (m_lock)
                    if (m_handles.Count == 0 || endtime < DateTime.Now)
                    {
                        DebugHelper.WriteLine("{0}: Stop is calling dispose", System.Diagnostics.Process.GetCurrentProcess().Id);
                        Dispose();
                        stopped = true;
                    }

                if (stopped)
                {
                    DebugHelper.WriteLine("{0}: Waiting for thread to stop", System.Diagnostics.Process.GetCurrentProcess().Id);

                    var waitms = (int)Math.Max(TimeSpan.FromSeconds(1).TotalMilliseconds, (endtime - DateTime.Now).TotalMilliseconds);
                    m_runnerThread.Join(waitms);

                    if (m_runnerThread.IsAlive)
                    {
                        DebugHelper.WriteLine("{0}: Aborting thread after timeout", System.Diagnostics.Process.GetCurrentProcess().Id);
                        m_runnerThread.Abort();
                    }
                    return;
                }

                var seconds = Math.Min(1, (endtime - DateTime.Now).TotalSeconds);
                if (seconds > 0)
                    System.Threading.Thread.Sleep(TimeSpan.FromSeconds(seconds));
            }
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
            public IDENTTYPE ident;
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
            public NOTE fflags;
            /// <summary>
            /// filter data value
            /// </summary>
            public INTPTR data;
            /// <summary>
            /// opaque user data identifier
            /// </summary>
            public INTPTR udata;
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
        /// The supported flags
        /// </summary>
        private enum NOTE : uint
        {
            /// <summary>On input, NOTE_TRIGGER causes the event to be triggered for output.</summary>
            TRIGGER = 0x01000000,

            /// <summary>ignore input fflags</summary>
            FFNOP = 0x00000000,
            /// <summary>and fflags</summary>
            FFAND = 0x40000000,
            /// <summary>or fflags</summary>
            FFOR = 0x80000000,
            /// <summary>copy fflags</summary>
            FFCOPY = 0xc0000000,
            /// <summary>mask for operation</summary>
            FFCTRLMASK = 0xc0000000,
            /// <summary>Mask for flags</summary>
            FFFLAGSMASK = 0x00ffffff,
            
            /// <summary>low water mark </summary>
            LOWAT = 0x00000001,
            /// <summary>vnode was removed </summary>
            DELETE = 0x00000001,
            /// <summary>data contents changed </summary>
            WRITE = 0x00000002,
            /// <summary>size increased </summary>
            EXTEND = 0x00000004,
            /// <summary>attributes changed </summary>
            ATTRIB = 0x00000008,
            /// <summary>link count changed </summary>
            LINK = 0x00000010,
            /// <summary>vnode was renamed </summary>
            RENAME = 0x00000020,
            /// <summary>vnode access was revoked </summary>
            REVOKE = 0x00000040,
            /// <summary>No specific vnode event: to test for EVFILT_READ activation</summary>
            NONE = 0x00000080,
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

            /// <summary>
            /// Helper method for invoking kevent with a single argument
            /// </summary>
            /// <param name="kq">The kqueue descriptor.</param>
            /// <param name="handle">The handle to register</param>
            /// <param name="filter">The filter to use</param>
            /// <param name="flags">The flags to use</param>
            /// <param name="fflags">The fflags</param>
            /// <param name="data">The data to associate</param>
            /// <param name="udata">The udata to associate</param>
            /// <returns></returns>
            public static int kevent(int kq, IDENTTYPE handle, EVFILT filter, EV flags, NOTE fflags = 0, INTPTR data = default(INTPTR), INTPTR udata = default(INTPTR))
            {
                var kev = new struct_kevent[] {
                    new struct_kevent() {
                        ident = (IDENTTYPE)handle,
                        filter = filter,
                        flags = flags,
                        fflags = fflags,
                        data = data,
                        udata = udata
                    }
                };

                var timeout = new[] { new Timespec() { tv_sec = 10 } };

                var ret = PInvoke.kevent(kq, kev, 1, null, 0, timeout);
                if (ret < 0)
                    throw new IOException($"Failed to {flags} handle on kqueue: {Stdlib.GetLastError()}");
                if (kev[0].flags.HasFlag(EV.ERROR))
                    throw new IOException($"Failed to {flags} handle on kqueue: {kev[0].data}");

                return ret;
            }
        }

        /// <summary>
        /// Encapsulates a user event
        /// </summary>
        private class UserEvent : IDisposable
        {
            /// <summary>
            /// The handle we are using
            /// </summary>
            public readonly IDENTTYPE Handle;

            /// <summary>
            /// The kqueue handle
            /// </summary>
            private readonly int m_fd;

            /// <summary>
            /// Constructs a new user event, and adds it to the kqueue
            /// </summary>
            /// <param name="handle">The handle to use for the userevent</param>
            /// <param name="kqueue">The queue to register for</param>
            public UserEvent(int kqueue, IDENTTYPE handle)
            {
                m_fd = kqueue;
                Handle = handle;

                PInvoke.kevent(m_fd, Handle, EVFILT.USER, EV.ADD, NOTE.FFCOPY);
            }

            /// <summary>
            /// Signals the user event
            /// </summary>
            public void Set()
            {
                PInvoke.kevent(m_fd, Handle, EVFILT.USER, EV.ENABLE, NOTE.FFCOPY | NOTE.TRIGGER | (NOTE)0x1);
            }

            /// <summary>
            /// Resets the user event
            /// </summary>
            public void Clear()
            {
                PInvoke.kevent(m_fd, Handle, EVFILT.USER, EV.DISABLE, NOTE.FFCOPY | (NOTE)EV.CLEAR);
            }

            /// <summary>
            /// Unregisters the event
            /// </summary>
            public void Dispose()
            {
                PInvoke.kevent(m_fd, Handle, EVFILT.USER, EV.DELETE);
            }
        }
    }
}
