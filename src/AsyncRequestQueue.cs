using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SockRock
{
    /// <summary>
    /// Implementation of a queue with an async interface,
    /// that only supports a single reader, but multiple writers.
    /// Disposing the instance prevents new items from entering the queue,
    /// but allows the reader to empty the queue
    /// </summary>
    public class AsyncRequestQueue : IDisposable
    {
        /// <summary>
        /// The lock protecting the queue
        /// </summary>
        private readonly object m_lock = new object();
        /// <summary>
        /// The queue with all requests
        /// </summary>
        private readonly Queue<Tuple<byte[], int, int, TaskCompletionSource<int>, CancellationToken>> m_queue = new Queue<Tuple<byte[], int, int, TaskCompletionSource<int>, CancellationToken>>();
        /// <summary>
        /// A task that can be awaited until the queue has data
        /// </summary>
        private TaskCompletionSource<bool> m_waitTask = new TaskCompletionSource<bool>();
        /// <summary>
        /// A flag keeping track of the disposed state
        /// </summary>
        private bool m_disposed = false;

        /// <summary>
        /// Enqueues data in the queue
        /// </summary>
        /// <param name="buffer">The buffer to use.</param>
        /// <param name="offset">The offset into the buffer</param>
        /// <param name="length">The length of the data</param>
        /// <param name="cancelToken">The cancellation token</param>
        /// <returns>The number of bytes processed</returns>
        public Task<int> Enqueue(byte[] buffer, int offset, int length, CancellationToken cancelToken)
        {
            TaskCompletionSource<int> tcs;
            lock (m_lock)
            {
                if (m_disposed)
                    throw new ObjectDisposedException(nameof(AsyncRequestQueue));

                tcs = new TaskCompletionSource<int>();
                m_queue.Enqueue(new Tuple<byte[], int, int, TaskCompletionSource<int>, CancellationToken>(buffer, offset, length, tcs, cancelToken));
                m_waitTask.TrySetResult(true);
            }

            return tcs.Task;
        }

        /// <summary>
        /// Dequeue an item, performs poorly with multiple readers,
        /// and does not prevent starvation
        /// </summary>
        /// <param name="throwIfDisposed">If set to <c>true</c> throws an exception if the instance is disposed, otherwise returns <c>null</c></param>
        /// <returns>The item from the queue.</returns>
        public Task<Tuple<byte[], int, int, TaskCompletionSource<int>, CancellationToken>> DequeueAsync(bool throwIfDisposed)
        {
            return TryDequeueAsync(Timeout.InfiniteTimeSpan, throwIfDisposed);
        }

        /// <summary>
        /// Dequeue an item, performs poorly with multiple readers,
        /// and does not prevent starvation
        /// </summary>
        /// <param name="timeout">The maximum time to wait</param>
        /// <param name="throwIfDisposed">If set to <c>true</c> throws an exception if the instance is disposed, otherwise returns <c>null</c></param>
        /// <returns>The item from the queue and flag indicating if the dequeue succeeded.</returns>
        public async Task<Tuple<byte[], int, int, TaskCompletionSource<int>, CancellationToken>> TryDequeueAsync(TimeSpan timeout, bool throwIfDisposed)
        {
            while (true)
            {
                lock (m_lock)
                {
                    while (m_queue.Count > 0)
                    {
                        var r = m_queue.Dequeue();
                        if (r.Item5.IsCancellationRequested)
                        {
                            r.Item4.TrySetCanceled();
                            continue;
                        }

                        return r;
                    }

                    if (m_disposed)
                    {
                        if (throwIfDisposed)
                            throw new ObjectDisposedException(nameof(AsyncRequestQueue));
                        return null;
                    }

                    m_waitTask = new TaskCompletionSource<bool>();
                }

                if (timeout == Timeout.InfiniteTimeSpan)
                    await m_waitTask.Task.ConfigureAwait(false);
                else
                {
                    if (await Task.WhenAny(Task.Delay(timeout), m_waitTask.Task).ConfigureAwait(false) != m_waitTask.Task)
                        return null;
                }
            }
        }

        /// <summary>
        /// Releases all resource used by the <see cref="T:SockRock.AsyncQueue`1"/> object.
        /// </summary>
        /// <remarks>Call <see cref="Dispose"/> when you are finished using the
        /// <see cref="T:SockRock.AsyncQueue`1"/>. The <see cref="Dispose"/> method leaves the
        /// <see cref="T:SockRock.AsyncQueue`1"/> in an unusable state. After calling
        /// <see cref="Dispose"/>, you must release all references to the
        /// <see cref="T:SockRock.AsyncQueue`1"/> so the garbage collector can reclaim the
        /// memory that the <see cref="T:SockRock.AsyncQueue`1"/> was occupying.</remarks>
        public void Dispose()
        {
            m_disposed = true;
            m_waitTask.TrySetResult(true);
        }
    }
}
