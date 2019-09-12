SockRock - A native socket handling library for C#
==================================================

[Install SockRock from NuGet](https://www.nuget.org/packages/SockRock/):
```bash
PM> Install-Package SockRock
```

SockRock is a wrapper library for using `epoll` and `kqueue` from C# without having a dependency on a native library.

This is required to allow passing sockets handles between processes on Linux/BSD-based systems, as Mono and .NET Core does not support using a passed handle.

To support this, the library offers a simple interface to `ScmRights` for passing the file handle, a monitor process based on `epoll`/`kqueue` and a `SocketStream` class that implements `System.IO.Stream` but uses signals from the monitor process to avoid blocking the `async` methods.

This allows a mostly drop-in replacement for `System.Net.NetworkStream` with handles passed from other processes.

Since Mono accesses the handles in unexpected ways, it is not always possible to simply extract the handle and pass it. To remedy this, SockRock also contain a `ListenSocket` (and `AsyncListenSocket`) implementation that performs native `listen` and `bind` operations, giving direct access to the handles.
