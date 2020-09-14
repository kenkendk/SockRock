SockRock - A native socket handling library for C#
==================================================
[![Nuget count](https://img.shields.io/nuget/v/SockRock.svg)](https://www.nuget.org/packages/SockRock/)
[![License](https://img.shields.io/github/license/kenkendk/SockRock.svg)](https://github.com/kenkendk/SockRock/blob/master/LICENSE)
[![Issues open](https://img.shields.io/github/issues-raw/kenkendk/SockRock.svg)](https://github.com/kenkendk/SockRock/issues/)
[![Codacy Badge](https://api.codacy.com/project/badge/Grade/41cd7874645f42f2940a930fe53cd238)](https://www.codacy.com/manual/kenneth/SockRock?utm_source=github.com&amp;utm_medium=referral&amp;utm_content=kenkendk/SockRock&amp;utm_campaign=Badge_Grade)

[Install SockRock from NuGet](https://www.nuget.org/packages/SockRock/):
```bash
PM> Install-Package SockRock
```

SockRock is a wrapper library for using `epoll` and `kqueue` from C# without having a dependency on a native library.

This is required to allow passing socket-handles between processes on Linux/BSD-based systems, as Mono and .NET Core does not support using a passed native socket-handle.

To support this, the library offers a simple interface to `ScmRights` for passing the socket-handle, a monitor process based on `epoll`/`kqueue` and a `SocketStream` class that implements `System.IO.Stream` but uses signals from the monitor process to avoid blocking the `async` methods.

This allows a mostly drop-in replacement for `System.Net.NetworkStream` with a monitor-handle passed from another process.

Since Mono accesses the handles in unexpected ways, it is not always possible to simply extract the socket-handle and pass it. To remedy this, SockRock also contain a `ListenSocket` (and `AsyncListenSocket`) implementation that performs native `listen` and `bind` operations, giving direct access to the handles.
