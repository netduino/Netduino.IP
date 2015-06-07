using Microsoft.SPOT.Hardware;
using System;

namespace Netduino.IP
{
    static class SocketsInterface
    {
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
