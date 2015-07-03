using Microsoft.SPOT.Hardware;
using System;

namespace Netduino.IP
{
    static class SocketsInterface
    {
        public const int FIONREAD = 0x4004667F;

        static object _initializeMethodSyncObject = new object();
        static bool _isInitialized = false;

        static internal EthernetInterface _ethernetInterface = null;
        static internal IPv4Layer _ipv4Layer = null;

#if NETDUINOIP_AX88796C
        const string LINK_LAYER_BASE_TYPENAME = "Netduino.IP.LinkLayers.AX88796C, Netduino.IP.LinkLayers.AX88796C";
#elif NETDUINOIP_ENC28J60
        const string LINK_LAYER_BASE_TYPENAME = "Netduino.IP.LinkLayers.ENC28J60, Netduino.IP.LinkLayers.ENC28J60";
#elif NETDUINOIP_CC3100
        const string LINK_LAYER_BASE_TYPENAME = "Netduino.IP.LinkLayers.CC3100, Netduino.IP.LinkLayers.CC3100";
#endif

        static internal void Initialize()
        {
            lock (_initializeMethodSyncObject)
            {
                if (_isInitialized) return;

#if CC3100
                Type socketNativeType = Type.GetType("Netduino.IP.LinkLayers.CC3100SocketNative, Netduino.IP.LinkLayers.CC3100");
                System.Reflection.MethodInfo initializeMethod = socketNativeType.GetMethod("Initialize", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                initializeMethod.Invoke(null, new object[] { });
#else
                // create the appropriate link layer object
                Type linkLayerType = Type.GetType(LINK_LAYER_BASE_TYPENAME);
                System.Reflection.ConstructorInfo linkLayerConstructor = linkLayerType.GetConstructor(new Type[] { typeof(SPI.SPI_module), typeof(Cpu.Pin), typeof(Cpu.Pin), typeof(Cpu.Pin), typeof(Cpu.Pin) });
#if NETDUINOIP_AX88796C
                ILinkLayer linkLayer = (ILinkLayer)linkLayerConstructor.Invoke(new object[] { SPI.SPI_module.SPI2  /* spiBusID */, (Cpu.Pin)0x28 /* csPinID */, (Cpu.Pin)0x04 /* intPinID */, (Cpu.Pin)0x12 /* resetPinID */, (Cpu.Pin)0x44 /* wakeupPinID */ });
#elif NETDUINOIP_ENC28J60
                ILinkLayer linkLayer = (ILinkLayer)linkLayerConstructor.Invoke(new object[] { SPI.SPI_module.SPI2  /* spiBusID */, (Cpu.Pin)0x28 /* csPinID */, (Cpu.Pin)0x04 /* intPinID */, (Cpu.Pin)0x32 /* resetPinID */, Cpu.Pin.GPIO_NONE /* wakeupPinID */ });
#elif NETDUINOIP_CC3100
#endif
                // retrieve MAC address from the config sector
                object networkInterface = Netduino.IP.Interop.NetworkInterface.GetNetworkInterface(0);
                byte[] physicalAddress = (byte[])networkInterface.GetType().GetMethod("get_PhysicalAddress").Invoke(networkInterface, new object[] { });
                linkLayer.SetMacAddress(physicalAddress);

                // create EthernetInterface instance
                _ethernetInterface = new Netduino.IP.EthernetInterface(linkLayer);
                // create our IPv4Layer instance
                _ipv4Layer = new IPv4Layer(_ethernetInterface);

                // start up our link layer
                linkLayer.Start();
#endif

                _isInitialized = true;
            }
        }

        public static object[] getaddrinfo_reflection(string name)
        {
            string canonicalName;
            byte[][] addresses;
            getaddrinfo(name, out canonicalName, out addresses);
            return new object[] { canonicalName, addresses };
        }

        public static void getaddrinfo(string name, out string canonicalName, out byte[][] addresses)
        {
            if (!_isInitialized) Initialize();

            UInt32[] ipAddresses = _ipv4Layer.ResolveHostNameToIpAddresses(name, out canonicalName);

            addresses = new byte[ipAddresses.Length][];
            for (int iAddress = 0; iAddress < ipAddresses.Length; iAddress++)
            {
                addresses[iAddress] = new byte[8];
                if (SystemInfo.IsBigEndian)
                {
                    addresses[iAddress][1] = 0x02; /* AddressFamily.InterNetwork */
                }
                else
                {
                    addresses[iAddress][0] = 0x02; /* AddressFamily.InterNetwork */
                }
                addresses[iAddress][4] = (byte)((ipAddresses[iAddress] >> 24) & 0xFF);
                addresses[iAddress][5] = (byte)((ipAddresses[iAddress] >> 16) & 0xFF);
                addresses[iAddress][6] = (byte)((ipAddresses[iAddress] >> 8) & 0xFF);
                addresses[iAddress][7] = (byte)(ipAddresses[iAddress] & 0xFF);
            }
        }

        public static int accept(int handle)
        {
            if (!_isInitialized) Initialize();

            Socket socket = _ipv4Layer.GetSocket(handle).Accept();

            if (socket != null)
                return socket.Handle;
            else
                return -1;
        }

        public static void bind(int handle, byte[] address)
        {
            if (!_isInitialized) Initialize();

            UInt16 ipPort = (UInt16)(((UInt16)address[2] << 8) +
                (UInt16)address[3]);
            UInt32 ipAddress = ((UInt32)address[4] << 24) +
                ((UInt32)address[5] << 16) +
                ((UInt32)address[6] << 8) +
                (UInt32)address[7];

            _ipv4Layer.GetSocket(handle).Bind(ipAddress, ipPort);
        }

        public static void connect(int handle, byte[] address, bool fThrowOnWouldBlock)
        {
            if (!_isInitialized) Initialize();

            UInt16 ipPort = (UInt16)(((UInt16)address[2] << 8) +
                (UInt16)address[3]);
            UInt32 ipAddress = ((UInt32)address[4] << 24) +
                ((UInt32)address[5] << 16) +
                ((UInt32)address[6] << 8) +
                (UInt32)address[7];

            _ipv4Layer.GetSocket(handle).Connect(ipAddress, ipPort);
        }

        public static int close(int handle)
        {
            if (!_isInitialized) Initialize();

            _ipv4Layer.CloseSocket(handle);

            return handle; /* TODO: what should we be returning from the close sockets method? */
        }

        public static int socket(int family, int type, int protocol)
        {
            if (!_isInitialized) Initialize();

            switch (family)
            {
                case 2: /* InterNetwork */
                    {
                        switch (type)
                        {
                            case 1: /* Stream */
                                {
                                    if (protocol == 6 /* Tcp */)
                                    {
                                        return _ipv4Layer.CreateSocket(IPv4Layer.ProtocolType.Tcp, Int64.MaxValue);
                                    }
                                }
                                break;
                            case 2: /* Dgram */
                                {
                                    if (protocol == 17 /* Udp */)
                                    {
                                        return _ipv4Layer.CreateSocket(IPv4Layer.ProtocolType.Udp, Int64.MaxValue);
                                    }
                                }
                                break;
                        }
                    }
                    break;
            }

            /* if we could not create a socket, return -1. */
            return -1;
        }

        public static void listen(int handle, int backlog)
        {
            if (!_isInitialized) Initialize();

            _ipv4Layer.GetSocket(handle).Listen(backlog);
        }

        public static object[] getpeername_reflection(int handle) 
        { 
            byte[] address; 
            getpeername(handle, out address); 
            return new object[] { address }; 
        } 
 
        public static void getpeername(int handle, out byte[] address) 
        { 
            if (!_isInitialized) Initialize();

            Socket socket = _ipv4Layer.GetSocket(handle);
            UInt32 ipAddress = socket.DestinationIPAddress;
            UInt16 ipPort = socket.DestinationIPPort;
 
            address = new byte[8]; 
            if (SystemInfo.IsBigEndian) 
            { 
                address[0] = 0x00;  /* InterNetwork = 0x0002 */ 
                address[1] = 0x02;  /* InterNetwork = 0x0002 */ 
            } 
            else 
            { 
                address[0] = 0x02;  /* InterNetwork = 0x0002 */ 
                address[1] = 0x00;  /* InterNetwork = 0x0002 */ 
            } 
            address[2] = (byte)((ipPort >> 8) & 0xFF); 
            address[3] = (byte)(ipPort & 0xFF); 
            address[4] = (byte)((ipAddress >> 24) & 0xFF); 
            address[5] = (byte)((ipAddress >> 16) & 0xFF); 
            address[6] = (byte)((ipAddress >> 8) & 0xFF); 
            address[7] = (byte)(ipAddress & 0xFF); 
        } 
 
        public static object[] getsockname_reflection(int handle) 
        { 
            byte[] address; 
            getsockname(handle, out address); 
            return new object[] { address }; 
        } 
 
        public static void getsockname(int handle, out byte[] address) 
        { 
            if (!_isInitialized) Initialize(); 
 
            Socket socket = _ipv4Layer.GetSocket(handle);
            UInt32 ipAddress = socket.SourceIPAddress;
            UInt16 ipPort = socket.SourceIPPort;
 
            address = new byte[8]; 
            address[2] = (byte)((ipPort >> 8) & 0xFF); 
            address[3] = (byte)(ipPort & 0xFF); 
            address[4] = (byte)((ipAddress >> 24) & 0xFF); 
            address[5] = (byte)((ipAddress >> 16) & 0xFF); 
            address[6] = (byte)((ipAddress >> 8) & 0xFF); 
            address[7] = (byte)(ipAddress & 0xFF); 

            throw new NotImplementedException(); 
        }

        public static void getsockopt(int handle, int level, int optname, byte[] optval)
        {
            if (!_isInitialized) Initialize();

            switch (level)
            {
                case 0xffff: /* SocketOptionLevel.Socket */
                    {
                        /* filter for Netduino.IP-supported optnames */
                        switch (optname)
                        {
                            case 0x1001: /* SendBuffer */
                                {
                                    Socket socket = _ipv4Layer.GetSocket(handle);
                                    if (typeof(TcpSocket).IsInstanceOfType(socket))
                                    {
                                        UInt32 transmitBufferSize = ((TcpSocket)socket).TransmitBufferSize;
                                        optval[0] = (byte)((transmitBufferSize) & 0xFF);
                                        optval[1] = (byte)(((transmitBufferSize) >> 8) & 0xFF);
                                        optval[2] = (byte)(((transmitBufferSize) >> 16) & 0xFF);
                                        optval[3] = (byte)(((transmitBufferSize) >> 24) & 0xFF);
                                    }
                                    else
                                    {
                                        throw new NotSupportedException();
                                    }
                                }
                                break;
                            case 0x1002: /* ReceiveBuffer */
                                {
                                    Socket socket = _ipv4Layer.GetSocket(handle);
                                    if (typeof(TcpSocket).IsInstanceOfType(socket))
                                    {
                                        UInt32 receiveBufferSize = ((TcpSocket)socket).ReceiveBufferSize;
                                        optval[0] = (byte)((receiveBufferSize) & 0xFF);
                                        optval[1] = (byte)(((receiveBufferSize) >> 8) & 0xFF);
                                        optval[2] = (byte)(((receiveBufferSize) >> 16) & 0xFF);
                                        optval[3] = (byte)(((receiveBufferSize) >> 24) & 0xFF);
                                    }
                                    else
                                    {
                                        throw new NotSupportedException();
                                    }
                                }
                                break;
                            case 0x1005: /* SendTimeout */
                                {
                                    Socket socket = _ipv4Layer.GetSocket(handle);
                                    Int32 sendTimeout = socket.TransmitTimeoutInMilliseconds;
                                    optval[0] = (byte)((sendTimeout) & 0xFF);
                                    optval[1] = (byte)(((sendTimeout) >> 8) & 0xFF);
                                    optval[2] = (byte)(((sendTimeout) >> 16) & 0xFF);
                                    optval[3] = (byte)(((sendTimeout) >> 24) & 0xFF);
                                }
                                break;
                            case 0x1006: /* ReceiveTimeout */
                                {
                                    Socket socket = _ipv4Layer.GetSocket(handle);
                                    Int32 receiveTimeout = socket.ReceiveTimeoutInMilliseconds;
                                    optval[0] = (byte)((receiveTimeout) & 0xFF);
                                    optval[1] = (byte)(((receiveTimeout) >> 8) & 0xFF);
                                    optval[2] = (byte)(((receiveTimeout) >> 16) & 0xFF);
                                    optval[3] = (byte)(((receiveTimeout) >> 24) & 0xFF);
                                }
                                break;
                            case 0x001008: /* SocketOptionName.Type */
                                {
                                    Socket socket = _ipv4Layer.GetSocket(handle);
                                    Int32 socketType;
                                    if (socket.GetType() == typeof(TcpSocket))
                                    {
                                        socketType = 1; /* Stream */
                                    }
                                    else if (socket.GetType() == typeof(UdpSocket))
                                    {
                                        socketType = 2; /* Dgram */
                                    }
                                    else
                                    {
                                        throw new ArgumentException();
                                    }
                                    optval[0] = (byte)((socketType) & 0xFF);
                                    optval[1] = (byte)(((socketType) >> 8) & 0xFF);
                                    optval[2] = (byte)(((socketType) >> 16) & 0xFF);
                                    optval[3] = (byte)(((socketType) >> 24) & 0xFF);
                                }
                                break;
                            default:
                                throw new NotSupportedException();
                        }
                    }
                    break;
                default:
                    throw new NotSupportedException();
            }
        }

        public static void setsockopt(int handle, int level, int optname, byte[] optval)
        {
            if (!_isInitialized) Initialize();

            switch (level)
            {
                case 0xffff: /* SocketOptionLevel.Socket */
                    {
                        /* filter for CC3100-specific optnames */
                        switch (optname)
                        {
                            case 0x1001: /* SendBuffer */
                                {
                                    Int32 transmitBufferSize =
                                        (((Int32)optval[3]) << 24) |
                                        (((Int32)optval[2]) << 16) |
                                        (((Int32)optval[1]) << 8) |
                                        ((Int32)optval[0]);

                                    if (transmitBufferSize <= 0)
                                        throw new ArgumentOutOfRangeException();

                                    Socket socket = _ipv4Layer.GetSocket(handle);
                                    if (typeof(TcpSocket).IsInstanceOfType(socket))
                                    {
                                        socket.TransmitBufferSize = (UInt16)transmitBufferSize;
                                    }
                                    else
                                    {
                                        throw new NotSupportedException();
                                    }
                                }
                                break;
                            case 0x1002: /* ReceiveBuffer */
                                {
                                    Int32 receiveBufferSize =
                                        (((Int32)optval[3]) << 24) |
                                        (((Int32)optval[2]) << 16) |
                                        (((Int32)optval[1]) << 8) |
                                        ((Int32)optval[0]);

                                    if (receiveBufferSize <= 0)
                                        throw new ArgumentOutOfRangeException();

                                    Socket socket = _ipv4Layer.GetSocket(handle);
                                    if (typeof(TcpSocket).IsInstanceOfType(socket))
                                    {
                                        socket.ReceiveBufferSize = (UInt16)receiveBufferSize;
                                    }
                                    else
                                    {
                                        throw new NotSupportedException();
                                    }
                                }
                                break;
                            case 0x1005: /* SendTimeout */
                                {
                                    Int32 sendTimeout =
                                        (((Int32)optval[3]) << 24) |
                                        (((Int32)optval[2]) << 16) |
                                        (((Int32)optval[1]) << 8) |
                                        ((Int32)optval[0]);

                                    if (sendTimeout == -1)
                                        sendTimeout = 0;

                                    _ipv4Layer.GetSocket(handle).TransmitTimeoutInMilliseconds = sendTimeout;
                                }
                                break;
                            case 0x1006: /* ReceiveTimeout */
                                {
                                    Int32 receiveTimeout =
                                        (((Int32)optval[3]) << 24) |
                                        (((Int32)optval[2]) << 16) |
                                        (((Int32)optval[1]) << 8) |
                                        ((Int32)optval[0]);

                                    if (receiveTimeout == -1)
                                        receiveTimeout = 0;

                                    _ipv4Layer.GetSocket(handle).ReceiveTimeoutInMilliseconds = receiveTimeout;
                                }
                                break;
                            default:
                                break;
                        }
                    }
                    break;
                case 0x0000: /* IP */
                    {
                        switch (optname)
                        {
                            case 10: /* MulticastTimeToLive */
                                {
                                    /* TODO: potentially add optional IGMP support in the future. */
                                }
                                break;
                            case 12: /* AddMembership */
                                {
                                    /* TODO: potentially add optional IGMP support in the future. */
                                }
                                break;
                            case 13: /* DropMembership */
                                {
                                    /* TODO: potentially add optional IGMP support in the future. */
                                }
                                break;
                            default:
                                break;
                        }
                    }
                    break;
                case 0x0006: /* TCP */
                    {
                        switch (optname)
                        {
                            case 1: /* NoDelay */
                                break;
                            default:
                                break;
                        }
                    }
                    break;
                case 0x0011: /* UDP */
                    break;
                default:
                    break;
            }
        }


        public static bool poll(int handle, int mode, int microSeconds)
        {
            if (!_isInitialized) Initialize();

            return _ipv4Layer.GetSocket(handle).Poll(mode, microSeconds);
        }

        public static object[] ioctl_reflection(int handle, uint cmd, uint arg)
        {
            ioctl(handle, cmd, ref arg);
            return new object[] { arg };
        }

        public static void ioctl(int handle, uint cmd, ref uint arg)
        {
            if (!_isInitialized) Initialize();

            switch (cmd)
            {
                case FIONREAD:
                    {
                        arg = (UInt32)_ipv4Layer.GetSocket(handle).GetBytesToRead();
                    }
                    break;
                default:
                    {
                        throw new NotImplementedException();
                    }
            }
        }

        public static int recv(int handle, byte[] buf, int offset, int count, int flags, int timeout_ms)
        {
            if (!_isInitialized) Initialize();

            return _ipv4Layer.GetSocket(handle).Receive(buf, offset, count, flags, (timeout_ms == -1) ? Int64.MaxValue : (Int64)(Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks + (timeout_ms * System.TimeSpan.TicksPerMillisecond)));
        }

        public static object[] recvfrom_reflection(int handle, byte[] buf, int offset, int count, int flags, int timeout_ms, byte[] address)
        {
            int returnValue = recvfrom(handle, buf, offset, count, flags, timeout_ms, ref address);
            return new object[] { returnValue, address };
        }

        public static int recvfrom(int handle, byte[] buf, int offset, int count, int flags, int timeout_ms, ref byte[] address)
        {
            if (!_isInitialized) Initialize();

            UInt32 ipAddress;
            UInt16 ipPort;

            Int32 returnValue =_ipv4Layer.GetSocket(handle).ReceiveFrom(buf, offset, count, flags, (timeout_ms == -1) ? Int64.MaxValue : (Int64)(Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks + (timeout_ms * System.TimeSpan.TicksPerMillisecond)), out ipAddress, out ipPort);

            address[2] = (byte)((ipPort >> 8) & 0xFF);
            address[3] = (byte)(ipPort & 0xFF);
            address[4] = (byte)((ipAddress >> 24) & 0xFF);
            address[5] = (byte)((ipAddress >> 16) & 0xFF);
            address[6] = (byte)((ipAddress >> 8) & 0xFF);
            address[7] = (byte)(ipAddress & 0xFF);
            
            return returnValue;
        }

        public static int send(int handle, byte[] buf, int offset, int count, int flags, int timeout_ms)
        {
            if (!_isInitialized) Initialize();

            return _ipv4Layer.GetSocket(handle).Send(buf, offset, count, flags, (timeout_ms == -1) ? Int64.MaxValue : (Int64)(Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks + (timeout_ms * System.TimeSpan.TicksPerMillisecond)));
        }

        public static int sendto(int handle, byte[] buf, int offset, int count, int flags, int timeout_ms, byte[] address)
        {
            if (!_isInitialized) Initialize();

            UInt16 ipPort = (UInt16)(((UInt16)address[2] << 8) +
                (UInt16)address[3]);
            UInt32 ipAddress = ((UInt32)address[4] << 24) +
                ((UInt32)address[5] << 16) +
                ((UInt32)address[6] << 8) +
                (UInt32)address[7];

            return _ipv4Layer.GetSocket(handle).SendTo(buf, offset, count, flags, (timeout_ms == -1) ? Int64.MaxValue : (Int64)(Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks + (timeout_ms * System.TimeSpan.TicksPerMillisecond)), ipAddress, ipPort);
        }
    }
}
