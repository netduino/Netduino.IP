////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Copyright (c) Microsoft Corporation.  All rights reserved.
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections;
using System.Reflection;
using System.Diagnostics;
using System.Runtime.CompilerServices;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("System")]

namespace Microsoft.SPOT.Net
{
    internal static class SocketNative
    {
        public const int FIONREAD = 0x4004667F;

        public static int socket(int family, int type, int protocol)
        {
			throw new NotImplementedException();
        }

        public static void bind(int handle, byte[] address)
        {
			throw new NotImplementedException();
        }

        public static void connect(int handle, byte[] address, bool fThrowOnWouldBlock)
        {
			throw new NotImplementedException();
        }

        public static int send(int handle, byte[] buf, int offset, int count, int flags, int timeout_ms)
        {
			throw new NotImplementedException();
        }

        public static int recv(int handle, byte[] buf, int offset, int count, int flags, int timeout_ms)
        {
			throw new NotImplementedException();
        }

        public static int close(int handle)
        {
			throw new NotImplementedException();
        }

        public static void listen(int handle, int backlog)
        {
			throw new NotImplementedException();
        }

        public static int accept(int handle)
        {
			throw new NotImplementedException();
        }

        //No standard non-blocking api
        public static void getaddrinfo(string name, out string canonicalName, out byte[][] addresses)
        {
			throw new NotImplementedException();
        }

        public static void shutdown(int handle, int how, out int err)
        {
			throw new NotImplementedException();
        }

        public static int sendto(int handle, byte[] buf, int offset, int count, int flags, int timeout_ms, byte[] address)
        {
			throw new NotImplementedException();
        }

        public static int recvfrom(int handle, byte[] buf, int offset, int count, int flags, int timeout_ms, ref byte[] address)
        {
			throw new NotImplementedException();
        }

        public static void getpeername(int handle, out byte[] address)
        {
			throw new NotImplementedException();
        }

        public static void getsockname(int handle, out byte[] address)
        {
			throw new NotImplementedException();
        }

        public static void getsockopt(int handle, int level, int optname, byte[] optval)
        {
			throw new NotImplementedException();
        }

        public static void setsockopt(int handle, int level, int optname, byte[] optval)
        {
			throw new NotImplementedException();
        }

        public static bool poll(int handle, int mode, int microSeconds)
        {
			throw new NotImplementedException();
        }

        public static void ioctl(int handle, uint cmd, ref uint arg)
        {
			throw new NotImplementedException();
        }
    }
}


