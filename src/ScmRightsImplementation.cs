﻿using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SockRock
{
    /// <summary>
    /// Implementation of the SCM_RIGHTS for passing socket handles between processes on Linux
    /// </summary>
    public static class ScmRightsImplementation
    {
        /// <summary>
        /// Sends a single file descriptor over the given socket with SCM_RIGHTS.
        /// </summary>
        /// <param name="sock">The socket handle to use.</param>
        /// <param name="fd">The file descriptor to send.</param>
        public static void send_fd(int sock, int fd)
        {
            send_fds(sock, new int[] { fd }, new byte[] { 1 });
        }

        /// <summary>
        /// Sends a single file descriptor over the given socket with SCM_RIGHTS.
        /// </summary>
        /// <param name="sock">The socket handle to use.</param>
        /// <param name="fd">The file descriptor to send.</param>
        /// <param name="buffer">The data to send with the message, cannot be null or empty.</param>
        public static void send_fd(int sock, int fd, byte[] buffer)
        {
            send_fds(sock, new int[] { fd }, buffer);
        }

        /// <summary>
        /// Sends one or more file descriptors over the given socket with SCM_RIGHTS.
        /// </summary>
        /// <param name="sock">The socket handle to use.</param>
        /// <param name="fds">The file descriptors to send. Must contain at least one.</param>
       public static void send_fds(int sock, int[] fds)
        {
            send_fds(sock, fds, new byte[] { 1 });
        }

        /// <summary>
        /// Sends one or more file descriptors over the given socket with SCM_RIGHTS.
        /// </summary>
        /// <param name="sock">The socket handle to use.</param>
        /// <param name="fds">The file descriptors to send. Must contain at least one.</param>
        /// <param name="buffer">The data to send with the message, cannot be null or empty.</param>
        public static void send_fds(int sock, int[] fds, byte[] buffer)
        {
            if (fds == null || fds.Length == 0)
                throw new ArgumentException("At least one file descriptor must be sent");
            
            if (buffer == null || buffer.Length == 0)
                throw new ArgumentException("The buffer cannot be empty");

            // Create the SCM_RIGHTS control message
            var cmsgbuffer = new byte[Mono.Unix.Native.Syscall.CMSG_SPACE((ulong)fds.Length * sizeof(int))];
            var cmsghdr = new Mono.Unix.Native.Cmsghdr
            {
                cmsg_len = (long)Mono.Unix.Native.Syscall.CMSG_LEN((ulong)fds.Length * sizeof(int)),
                cmsg_level = Mono.Unix.Native.UnixSocketProtocol.SOL_SOCKET,
                cmsg_type = Mono.Unix.Native.UnixSocketControlMessage.SCM_RIGHTS,
            };

            // Create the message header
            var msghdr = new Mono.Unix.Native.Msghdr
            {
                msg_control = cmsgbuffer,
                msg_controllen = cmsgbuffer.Length,
            };
            cmsghdr.WriteToBuffer(msghdr, 0);

            // Copy in the file handles
            var dataOffset = Mono.Unix.Native.Syscall.CMSG_DATA(msghdr, 0);
            for (var i = 0; i < fds.Length; i++)
            {
                Array.Copy(BitConverter.GetBytes(fds[i]), 0, msghdr.msg_control, dataOffset, sizeof(int));
                dataOffset += sizeof(int);
            }

            // Pin the buffer we are sending
            using (var ptr_buffer1 = new GuardedHandle(buffer))
            {
                // Put the buffer into the IO vector attached to the message
                var iovecs = new Mono.Unix.Native.Iovec[] {
                    new Mono.Unix.Native.Iovec {
                        iov_base = ptr_buffer1.Address,
                        iov_len = (ulong)buffer.Length,
                    },
                };
                msghdr.msg_iov = iovecs;
                msghdr.msg_iovlen = 1;

                // Send it
                var ret = Mono.Unix.Native.Syscall.sendmsg(sock, msghdr, 0);
                if (ret < 0)
                    Mono.Unix.UnixMarshal.ThrowExceptionForLastError();
            }
        }

        /// <summary>
        /// Reads file descriptors and the message from a SCM_RIGHTS message
        /// </summary>
        /// <returns>The file descriptors and the bytes.</returns>
        /// <param name="sock">The socket to read from.</param>
        public static Tuple<int[], byte[]> recv_fds(int sock)
        {
            var buffer = new byte[1024];
            var cmsg = new byte[1024];
            var msghdr = new Mono.Unix.Native.Msghdr
            {
                msg_control = cmsg,
                msg_controllen = cmsg.Length,
            };

            long datalen;

            using(var ptr_buffer = new GuardedHandle(buffer))
            {
                var iovec = new Mono.Unix.Native.Iovec[] {
                    new Mono.Unix.Native.Iovec {
                        iov_base = ptr_buffer.Address,
                        iov_len = (ulong) buffer.Length,
                    },
                };
                msghdr.msg_iov = iovec;
                msghdr.msg_iovlen = 1;
                DebugHelper.WriteLine("Calling recvmsg");
                datalen = Mono.Unix.Native.Syscall.recvmsg(sock, msghdr, 0);
                DebugHelper.WriteLine("Called recvmsg: {0}", datalen);
                if (datalen < 0)
                    Mono.Unix.UnixMarshal.ThrowExceptionForLastError();
            }

            // Get the offset of the first message
            var offset = Mono.Unix.Native.Syscall.CMSG_FIRSTHDR(msghdr);
            DebugHelper.WriteLine("Called recvmsg, offset: {0}", offset);

            // Extract the bytes
            var recvHdr = Mono.Unix.Native.Cmsghdr.ReadFromBuffer(msghdr, offset);
            //DebugHelper.WriteLine("Buffer has, recvHdr.cmsg_len: {0}", recvHdr.cmsg_len);

            var data = new byte[datalen];
            Array.Copy(buffer, data, data.Length);
            //DebugHelper.WriteLine("Got {0} bytes of data", data.Length);
            //foreach (var n in data)
            //    Console.Write("{0}, ", n);
            //DebugHelper.WriteLine();

            // See how many bytes are of file descriptors we have
            var recvDataOffset = Mono.Unix.Native.Syscall.CMSG_DATA(msghdr, offset);
            var userData = recvHdr.cmsg_len - (int)Mono.Unix.Native.Syscall.CMSG_LEN(0);
            var bytes = recvHdr.cmsg_len - (recvDataOffset - offset);
            var fdCount = bytes / sizeof(int);
            var fds = new int[fdCount];
            //DebugHelper.WriteLine("Got {0} fds ({1} bytes)", fdCount, bytes);

            // Extract the file descriptors
            for (int i = 0; i < fdCount; i++)
            {
                fds[i] = BitConverter.ToInt32(msghdr.msg_control, (int)(recvDataOffset + (sizeof(int) * i)));
                //DebugHelper.WriteLine($"fd[{i}] = {fds[i]}");
            }

            //DebugHelper.WriteLine("Read {0} fds and {1} bytes", fds.Length, data.Length);

            // Check that we only have a single message
            offset = Mono.Unix.Native.Syscall.CMSG_NXTHDR(msghdr, offset);
            if (offset != -1)
                System.Diagnostics.Trace.WriteLine("WARNING: more than one message detected when reading SCM_RIGHTS, only processing the first one");
                             
            return new Tuple<int[], byte[]>(fds, data);
        }


        /// <summary>
        /// Reads file descriptors and the message from a SCM_RIGHTS message
        /// </summary>
        /// <returns>The file descriptors and the bytes.</returns>
        /// <param name="handle">The socket to read from.</param>
        public static async Task<List<Tuple<int[], byte[]>>> recv_fds_async(MonitoredHandle handle)
        {
            var buffer = new byte[1024];
            var cmsg = new byte[1024];
            var msghdr = new Mono.Unix.Native.Msghdr
            {
                msg_control = cmsg,
                msg_controllen = cmsg.Length,
            };

            long datalen;

            await handle.WaitForReadAsync;

            var res = new List<Tuple<int[], byte[]>>();

            using (var ptr_buffer = new GuardedHandle(buffer))
            {
                var iovec = new Mono.Unix.Native.Iovec[] {
                    new Mono.Unix.Native.Iovec {
                        iov_base = ptr_buffer.Address,
                        iov_len = (ulong) buffer.Length,
                    },
                };
                msghdr.msg_iov = iovec;
                msghdr.msg_iovlen = 1;

                DebugHelper.WriteLine("Calling recvmsg");
                while ((datalen = Mono.Unix.Native.Syscall.recvmsg(handle.Handle, msghdr, 0)) != -1)
                {
                    datalen = Mono.Unix.Native.Syscall.recvmsg(handle.Handle, msghdr, 0);
                    DebugHelper.WriteLine("Called recvmsg: {0}", datalen);
                    if (datalen < 0)
                        Mono.Unix.UnixMarshal.ThrowExceptionForLastError();

                    // Get the offset of the first message
                    var offset = Mono.Unix.Native.Syscall.CMSG_FIRSTHDR(msghdr);
                    DebugHelper.WriteLine("Called recvmsg, offset: {0}", offset);

                    // Extract the bytes
                    var recvHdr = Mono.Unix.Native.Cmsghdr.ReadFromBuffer(msghdr, offset);
                    //DebugHelper.WriteLine("Buffer has, recvHdr.cmsg_len: {0}", recvHdr.cmsg_len);

                    var data = new byte[datalen];
                    Array.Copy(buffer, data, data.Length);
                    //DebugHelper.WriteLine("Got {0} bytes of data", data.Length);
                    //foreach (var n in data)
                    //    Console.Write("{0}, ", n);
                    //DebugHelper.WriteLine();

                    // See how many bytes are of file descriptors we have
                    var recvDataOffset = Mono.Unix.Native.Syscall.CMSG_DATA(msghdr, offset);
                    var userData = recvHdr.cmsg_len - (int)Mono.Unix.Native.Syscall.CMSG_LEN(0);
                    var bytes = recvHdr.cmsg_len - (recvDataOffset - offset);
                    var fdCount = bytes / sizeof(int);
                    var fds = new int[fdCount];
                    //DebugHelper.WriteLine("Got {0} fds ({1} bytes)", fdCount, bytes);

                    // Extract the file descriptors
                    for (int i = 0; i < fdCount; i++)
                    {
                        fds[i] = BitConverter.ToInt32(msghdr.msg_control, (int)(recvDataOffset + (sizeof(int) * i)));
                        //DebugHelper.WriteLine($"fd[{i}] = {fds[i]}");
                    }

                    //DebugHelper.WriteLine("Read {0} fds and {1} bytes", fds.Length, data.Length);

                    // Check that we only have a single message
                    offset = Mono.Unix.Native.Syscall.CMSG_NXTHDR(msghdr, offset);
                    if (offset != -1)
                        System.Diagnostics.Trace.WriteLine("WARNING: more than one message detected when reading SCM_RIGHTS, only processing the first one");


                    res.Add(new Tuple<int[], byte[]>(fds, data));
                }
            }
            var lasterr = Mono.Unix.Native.Stdlib.GetLastError();
            if (res.Count == 0 && lasterr != Mono.Unix.Native.Errno.EAGAIN && lasterr != Mono.Unix.Native.Errno.EWOULDBLOCK)
                throw new Exception($"Unexpected error code while reading socket: {lasterr}");

            return res;

        }

        /// <summary>
        /// Closes a handle by invoking the OS close method
        /// </summary>
        /// <param name="handle">The handle to close.</param>
        public static void native_close(int handle)
        {
            Mono.Unix.Native.Syscall.close(handle);
        }
    }    
}
