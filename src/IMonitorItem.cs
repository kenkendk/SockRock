using System;
namespace SockRock
{
    /// <summary>
    /// The interface for an item that can be monitored with a <see cref="ISocketHandler"/>
    /// </summary>
    public interface IMonitorItem : IDisposable
    {
        /// <summary>
        /// Signals that the handle can be read
        /// </summary>
         void SignalReadReady();
         /// <summary>
         /// Signals that the handle can be written
         /// </summary>
         void SignalWriteReady();
    }
}