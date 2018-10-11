using System;
using System.Runtime.InteropServices;

namespace SockRock
{
    /// <summary>
    /// Helper class to make sure we free a pinned handle,
    /// basically a way to add the <see cref="IDisposable"/> interface
    /// to <see cref="GCHandle"/>
    /// </summary>
    public class GuardedHandle : IDisposable
    {
        /// <summary>
        /// The handle we allocated
        /// </summary>
        private readonly GCHandle m_handle;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="T:SockRock.GuardedHandle"/> class.
        /// </summary>
        /// <param name="item">The object to pin.</param>
        public GuardedHandle(object item)
        {
            m_handle = GCHandle.Alloc(item, GCHandleType.Pinned);
        }

        /// <summary>
        /// Gets the pinned address of the item
        /// </summary>
        /// <value>The address.</value>
        public IntPtr Address => m_handle.AddrOfPinnedObject();

        /// <summary>
        /// Releases all resource used by the
        /// <see cref="T:SockRock.GuardedHandle"/> object.
        /// </summary>
        /// <remarks>Call <see cref="Dispose"/> when you are finished using the
        /// <see cref="T:SockRock.GuardedHandle"/>. The
        /// <see cref="Dispose"/> method leaves the
        /// <see cref="T:SockRock.GuardedHandle"/> in an unusable state.
        /// After calling <see cref="Dispose"/>, you must release all references to the
        /// <see cref="T:SockRock.GuardedHandle"/> so the garbage collector
        /// can reclaim the memory that the
        /// <see cref="T:SockRock.GuardedHandle"/> was occupying.</remarks>
        public void Dispose()
        {
            if (m_handle.IsAllocated)
                m_handle.Free();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged resources and performs other cleanup operations before the
        /// <see cref="T:SockRock.GuardedHandle"/> is reclaimed by garbage collection.
        /// </summary>
        ~GuardedHandle()
        {
            if (m_handle.IsAllocated)
                m_handle.Free();
        }
    }
}