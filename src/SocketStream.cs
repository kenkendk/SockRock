using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Mono.Unix.Native;

namespace SockRock
{
    /// <summary>
    /// Implementation of a stream that gets data from epoll events
    /// </summary>
    public class SocketStream : Stream, IMonitorItem
    {
        /// <summary>
        /// The socket we are wrapping
        /// </summary>
        private readonly int m_socket;

        /// <summary>
        /// The maximum amount of time to wait before giving up the buffers
        /// </summary>
        private static readonly TimeSpan MAX_WAIT_TIME = TimeSpan.FromMilliseconds(100);

        /// <summary>
        /// Signal that data can be read
        /// </summary>
        private TaskCompletionSource<bool> m_readDataSignal = new TaskCompletionSource<bool>();

        /// <summary>
        /// Signal that data can be written
        /// </summary>
        private TaskCompletionSource<bool> m_writeDataSignal = new TaskCompletionSource<bool>();

        /// <summary>
        /// The channel used to request read operations
        /// </summary>
        private readonly AsyncRequestQueue m_readRequests = new AsyncRequestQueue();

        /// <summary>
        /// The channel used to request write operations
        /// </summary>
        private readonly AsyncRequestQueue m_writeRequests = new AsyncRequestQueue();

        /// <summary>
        /// Gets a value indicating whether the reader has closed
        /// </summary>
        public bool ReaderClosed { get; private set; }

        /// <summary>
        /// Gets a value indicating whether the writer has closed
        /// </summary>
        public bool WriterClosed { get; private set; }

        /// <summary>
        /// A flag that tracks the disposed state of this object
        /// </summary>
        private bool m_isDisposed;

        ///<inheritdoc />
        public override int ReadTimeout { get; set; }
        ///<inheritdoc />
        public override int WriteTimeout { get; set; }

        ///<inheritdoc />
        public override bool CanRead => true;

        ///<inheritdoc />
        public override bool CanSeek => false;

        ///<inheritdoc />
        public override bool CanWrite => true;

        ///<inheritdoc />
        public override long Length => throw new NotSupportedException();

        ///<inheritdoc />
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        /// <summary>
        /// The task signaling completion of the stream
        /// </summary>
        public readonly Task RunnerTask;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="T:Ceen.Httpd.Cli.Runner.SubProcess.EPollHandler.EpollStream"/> class.
        /// </summary>
        /// <param name="socket">The socket handle to use.</param>
        /// <param name="deregister">A method to call when the instance terminates</param>
        /// <param name="manager">The buffer manager</param>
        public SocketStream(int socket, BufferManager manager, Action deregister)
        {
            m_socket = socket;
            RunnerTask = Task.WhenAll(
                Task.Run(() => ProcessSocketRead(manager)),
                Task.Run(() => ProcessSocketWrite(manager))
            )
            .ContinueWith((_) => deregister());
        }

        /// <summary>
        /// Processes read requests from the queue
        /// </summary>
        /// <param name="manager">The buffer manager</param>
        private async Task ProcessSocketRead(BufferManager manager)
        {
            var readBlocking = false; // Assume there is data to grab
            var buffer = new KeyValuePair<byte[], GCHandle>();
            Tuple<byte[], int, int, TaskCompletionSource<int>, CancellationToken> readReq = null;

            try
            {
                while (true)
                {
                    // Inside the loop, we first acquire a request to read
                    // then avoid reading from the socket until it has been
                    // signalled as ready

                    // The buffer is released prior to entering a wait state,
                    // and acquired just before needing it for the syscall
                    // This allows us to have a large number of active streams
                    // without needing a full buffer for each

                    // See if we have a pending request, 
                    // and ignore if we have no buffer allocated
                    readReq =
                        (ReaderClosed || buffer.Key == null)
                        ? null
                        : await m_readRequests.TryDequeueAsync(MAX_WAIT_TIME, false);

                    // Nope, relinquish control of our buffer
                    // and wait for a request
                    if (readReq == null)
                    {
                        buffer = manager.Release(buffer);
                        readReq = await m_readRequests.DequeueAsync(false);
                        // If we are disposed, just stop listening
                        if (readReq == null)
                            return;
                    }

                    if (ReaderClosed)
                    {
                        readReq.Item4.SetResult(0);
                        readReq = null;
                        continue;
                    }

                    while (true)
                    {
                        // If we cannot read, release the buffer and wait
                        if (readBlocking)
                        {
                            // Check if we can read data, otherwise release the buffer
                            if (await Task.WhenAny(Task.Delay(MAX_WAIT_TIME), m_readDataSignal.Task) != m_readDataSignal.Task)
                            {
                                buffer = manager.Release(buffer);
                                var rt = ReadTimeout;
                                if (rt > 0)
                                {
                                    if (await Task.WhenAny(m_readDataSignal.Task, Task.Delay(rt)) != m_readDataSignal.Task)
                                    {
                                        readReq.Item4.TrySetException(new TimeoutException());
                                        break;
                                    }
                                }
                                else
                                {
                                    await m_readDataSignal.Task;
                                }
                            }

                            // The poll handler says we have data, lets see
                            readBlocking = false;
                            m_readDataSignal = new TaskCompletionSource<bool>();
                        }

                        if (buffer.Key == null)
                            buffer = manager.Acquire();

                        var r = Syscall.read(m_socket, buffer.Value.AddrOfPinnedObject(), (ulong)buffer.Key.Length);
                        if (r == 0)
                        {
                            ReaderClosed = true;
                            readReq.Item4.SetResult(0);
                            readReq = null;
                        }
                        else if (r > 0)
                        {
                            Array.Copy(buffer.Key, 0, readReq.Item1, readReq.Item2, r);
                            readReq.Item4.SetResult((int)r);
                            readReq = null;
                            break;
                        }
                        else
                        {
                            var errno = Stdlib.GetLastError();
                            if (errno == Errno.EAGAIN || errno == Errno.EWOULDBLOCK)
                            {
                                readBlocking = true;
                                if (m_isDisposed)
                                    throw new ObjectDisposedException(nameof(SocketStream));
                            }
                            else
                                throw new IOException($"Got unexpected error while reading socket: {Stdlib.GetLastError()}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                readReq?.Item4?.TrySetException(ex);
                throw;
            }
            finally
            {
                readReq?.Item4?.TrySetCanceled();
                manager.Release(buffer);
            }
        }

        /// <summary>
        /// Processes write requests from the request queue
        /// </summary>
        /// <param name="manager">The buffer manager</param>
        private async Task ProcessSocketWrite(BufferManager manager)
        {
            var writeBlocking = false;
            var buffer = new KeyValuePair<byte[], GCHandle>();
            Tuple<byte[], int, int, TaskCompletionSource<int>, CancellationToken> writeReq = null;

            try
            {
                while (true)
                {
                    // Inside the loop, we first acquire a request to write
                    // then avoid writing to the socket if it has been
                    // signalled as blocking

                    // The buffer is released prior to entering a wait state,
                    // and acquired just before needing it for the syscall
                    // This allows us to have a large number of active streams
                    // without needing a full buffer for each

                    // Probe for a request if we have an allocated buffer
                    writeReq =
                        (buffer.Key == null)
                        ? null
                        : await m_writeRequests.TryDequeueAsync(MAX_WAIT_TIME, false);

                    // No requests, so relinquish control of our buffer
                    // and wait for a request
                    if (writeReq == null)
                    {
                        buffer = manager.Release(buffer);
                        writeReq = await m_writeRequests.DequeueAsync(false);

                        // If we are disposed, just stop listening
                        if (writeReq == null)
                            return;
                    }

                    if (WriterClosed)
                    {
                        writeReq.Item4.SetException(new IOException("Socket closed"));
                        writeReq = null;
                        continue;
                    }

                    var offset = writeReq.Item2;
                    var remain = writeReq.Item3;

                    while (remain > 0)
                    {
                        if (writeBlocking)
                        {
                            // Check if we can read data, otherwise release the buffer
                            if (await Task.WhenAny(Task.Delay(MAX_WAIT_TIME), m_writeDataSignal.Task) != m_writeDataSignal.Task)
                            {
                                buffer = manager.Release(buffer);
                                var rt = WriteTimeout;
                                if (rt > 0)
                                {
                                    // TODO: Figure out if the timeout is for the whole operation, 
                                    // or just for time between progress as we do here
                                    if (await Task.WhenAny(m_writeDataSignal.Task, Task.Delay(rt)) != m_writeDataSignal.Task)
                                    {
                                        writeReq.Item4.TrySetException(new TimeoutException());
                                        // Avoid setting the result to a partial result
                                        writeReq = null;
                                        break;
                                    }
                                }
                                else
                                {
                                    await m_writeDataSignal.Task;
                                }
                            }

                            // The poll handler says we can write, lets see
                            writeBlocking = false;
                            m_writeDataSignal = new TaskCompletionSource<bool>();
                        }

                        if (buffer.Key == null)
                            buffer = manager.Acquire();

                        var len = Math.Min(remain, buffer.Key.Length);
                        Array.Copy(writeReq.Item1, offset, buffer.Key, 0, len);

                        var r = Syscall.write(m_socket, buffer.Value.AddrOfPinnedObject(), (ulong)len);
                        if (r > 0)
                        {
                            offset += (int)r;
                            remain -= (int)r;
                        }
                        else
                        {
                            var errno = Stdlib.GetLastError();
                            if (errno == Errno.EAGAIN || errno == Errno.EWOULDBLOCK)
                            {
                                writeBlocking = true;
                                if (m_isDisposed)
                                    throw new ObjectDisposedException(nameof(SocketStream));
                            }
                            else
                                throw new IOException($"Got unexpected error while writing socket: {Stdlib.GetLastError()}");
                        }
                    }

                    writeReq?.Item4?.SetResult(0);
                    writeReq = null;
                }
            }
            catch (Exception ex)
            {
                writeReq?.Item4?.TrySetException(ex);
                throw;
            }
            finally
            {
                writeReq?.Item4?.TrySetCanceled();
                manager.Release(buffer);
            }
        }

        ///<inheritdoc />
        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer, offset, count).Result;
        }
        ///<inheritdoc />
        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAsync(buffer, offset, count).Wait();
        }

        ///<inheritdoc />
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (m_isDisposed)
                throw new ObjectDisposedException(nameof(SocketStream));
            if (ReaderClosed)
                return Task.FromResult(0);

            return m_readRequests.Enqueue(buffer, offset, count, cancellationToken);
        }

        ///<inheritdoc />
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (m_isDisposed)
                throw new ObjectDisposedException(nameof(SocketStream));
            if (WriterClosed)
                throw new IOException("Connection was closed");

            return m_writeRequests.Enqueue(buffer, offset, count, cancellationToken);
        }

        ///<inheritdoc />
        public override void Flush()
        {
            FlushAsync().Wait();
        }

        ///<inheritdoc />
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            // Set TCP_NODELAY to flush any pending message
            Syscall.setsockopt(m_socket, UnixSocketProtocol.IPPROTO_TCP, (UnixSocketOptionName)0x1, 1);
            Syscall.setsockopt(m_socket, UnixSocketProtocol.IPPROTO_TCP, (UnixSocketOptionName)0x1, 0);
            return Task.FromResult(true);
        }

        ///<inheritdoc />
        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        ///<inheritdoc />
        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        ///<inheritdoc />
        protected override void Dispose(bool disposing)
        {
            // Make sure we clear the queue
            m_writeRequests.Dispose();
            m_readRequests.Dispose();

            // This instance is now disposed
            m_isDisposed = true;

            // Then signal if we are waiting for data
            m_writeDataSignal.TrySetResult(true);
            m_readDataSignal.TrySetResult(true);

            base.Dispose(disposing);
        }

        ///<inheritdoc />
        void IMonitorItem.SignalReadReady()
        {
            if (!m_isDisposed)
                m_readDataSignal.TrySetResult(true);
        }

        ///<inheritdoc />
        void IMonitorItem.SignalWriteReady()
        {
            if (!m_isDisposed)
                m_writeDataSignal.TrySetResult(true);
        }
    }
}
