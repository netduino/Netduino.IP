using Microsoft.SPOT;
using System;
using System.Threading;

namespace Netduino.IP
{
    internal class IPv4Layer : IDisposable 
    {
        EthernetInterface _ethernetInterface;
        bool _isDisposed = false;

        ArpResolver _arpResolver;

        DHCPv4Client _dhcpv4Client;

        ICMPv4Handler _icmpv4Handler;

        DnsResolver _dnsResolver;

        TcpHandler _tcpHandler;

        internal const byte MAX_SIMULTANEOUS_SOCKETS = 8; /* must be between 2 and 64; one socket (socket 0) is reserved for background operations such as the DHCP and DNS clients */
        static internal Netduino.IP.Socket[] _sockets = new Netduino.IP.Socket[MAX_SIMULTANEOUS_SOCKETS];
        UInt64 _handlesInUseBitmask = 0;
        object _handlesInUseBitmaskLockObject = new object();
        AutoResetEvent _socketHandleReservedFreed = new AutoResetEvent(false);
        AutoResetEvent _socketHandleUserFreed = new AutoResetEvent(false);

        // our IP configuration
        //UInt32 _ipv4configIPAddress = 0xC0A80564;     /* IP: 192.168.5.100 */
        //UInt32 _ipv4configSubnetMask = 0xFFFFFF00;  /* SM: 255.255.255.0 */
        //UInt32 _ipv4configGatewayAddress = 0xC0A80501;     /* GW: 192.168.5.1 */
        UInt32 _ipv4configIPAddress = 0x00000000;     /* IP: 0.0.0.0 = IPAddress.Any */
        UInt32 _ipv4configSubnetMask = 0x00000000;  /* SM: 0.0.0.0 = IPAddress.Any */
        UInt32 _ipv4configGatewayAddress = 0x00000000;     /* GW: 0.0.0.0 = IPAddress.Any */
        UInt32[] _ipv4configDnsServerAddresses = new UInt32[0];

        const UInt32 LOOPBACK_IP_ADDRESS = 0x7F000001;
        const UInt32 LOOPBACK_SUBNET_MASK = 0xFF000000;

        const Int32 MAX_IPV4_DATA_FRAGMENT_SIZE = 1500 - IPV4_HEADER_MIN_LENGTH; /* max IPv4 data fragment size */

        const Int32 LOOPBACK_BUFFER_SIZE = MAX_IPV4_DATA_FRAGMENT_SIZE; /* max IPv4 payload size */
        UInt32 _loopbackSourceIPAddress = 0;
        UInt32 _loopbackDestinationIPAddress = 0;
        ProtocolType _loopbackProtocol = (ProtocolType)0;
        byte[] _loopbackBuffer = null;
        const Int32 _loopbackBufferIndex = 0;
        Int32 _loopbackBufferCount = 0;
        bool _loopbackBufferInUse = false;
        AutoResetEvent _loopbackBufferFreedEvent = new AutoResetEvent(false);
        object _loopbackBufferLockObject = new object();
        System.Threading.Thread _loopbackThread;
        AutoResetEvent _loopbackBufferFilledEvent = new AutoResetEvent(false);

        internal struct ReceivedPacketBufferHoles
        {
            public Int32 FirstIndex;
            public Int32 LastIndex; /* set to Int32.MaxValue to indicate an unknown end of missing fragmetn */
        }

        const int REASSEMBLY_TIMEOUT_IN_SECONDS = 30;
        const int DEFAULT_NUM_BUFFER_HOLES_ENTRIES_PER_BUFFER = 4;
        internal class ReceivedPacketBuffer
        {
            public UInt32 SourceIPAddress;
            public UInt32 DestinationIPAddress;
            public UInt16 Identification;
            public byte[] Buffer;
            public Int32 ActualBufferLength;
            public ReceivedPacketBufferHoles[] Holes;
            public Int64 TimeoutInMachineTicks;
            public bool IsEmpty;
            public object LockObject;
        }
        const int DEFAULT_NUM_RECEIVED_PACKET_BUFFERS = 1;
        /* create our default # of received packet buffers; when we create new sockets we should dynamically create one or more buffers per socket (and then destroy those buffers the same way) */
        ReceivedPacketBuffer[] _receivedPacketBuffers = new ReceivedPacketBuffer[DEFAULT_NUM_RECEIVED_PACKET_BUFFERS];

        //// fixed buffer for IPv4 header
        internal const int IPV4_HEADER_MIN_LENGTH = 20;
        const int IPV4_HEADER_MAX_LENGTH = 60;
        byte[] _ipv4HeaderBuffer = new byte[IPV4_HEADER_MAX_LENGTH];
        object _ipv4HeaderBufferLockObject = new object();

        const int MAX_BUFFER_SEGMENT_COUNT = 3;
        byte[][] _bufferArray = new byte[MAX_BUFFER_SEGMENT_COUNT][];
        int[] _indexArray = new int[MAX_BUFFER_SEGMENT_COUNT];
        int[] _countArray = new int[MAX_BUFFER_SEGMENT_COUNT];

        UInt16 _nextDatagramID = 0;
        object _nextDatagramIDLockObject = new object();

        const UInt16 FIRST_EPHEMERAL_PORT = 0xC000;
        UInt16 _nextEphemeralPort = FIRST_EPHEMERAL_PORT;
        object _nextEphemeralPortLockObject = new object();

        const byte DEFAULT_TIME_TO_LIVE = 64; /* default TTL recommended by RFC 1122 */

        public event LinkStateChangedEventHandler LinkStateChanged;

        public enum ProtocolType : byte
        {
            ICMPv4 = 1,  // internet control message protocol
            Tcp    = 6,  // transmission control protocol
            Udp    = 17, // user datagram protocol
        }

        public IPv4Layer(EthernetInterface ethernetInterface)
        {
            // save a reference to our Ethernet; we'll use this to push IPv4 frames onto the Ethernet interface
            _ethernetInterface = ethernetInterface;

            // create and configure my ARP resolver; the ARP resolver will automatically wire itself up to receiving incoming ARP frames
            _arpResolver = new ArpResolver(ethernetInterface);

            // retrieve our IP address configuration from the config sector and configure ARP
            object networkInterface = Netduino.IP.Interop.NetworkInterface.GetNetworkInterface(0);
            bool dhcpIpConfigEnabled = (bool)networkInterface.GetType().GetMethod("get_IsDhcpEnabled").Invoke(networkInterface, new object[] { });
            /* NOTE: IsDynamicDnsEnabled is improperly implemented in NETMF; it should implement dynamic DNS--but instead it returns whether or not DNS addresses are assigned through DHCP */
            bool dhcpDnsConfigEnabled = (bool)networkInterface.GetType().GetMethod("get_IsDynamicDnsEnabled").Invoke(networkInterface, new object[] { });

            // randomize our ephemeral port assignment counter (so that we don't use the same port #s repeatedly after reboots)
            _nextEphemeralPort = (UInt16)(FIRST_EPHEMERAL_PORT + ((new Random()).NextDouble() * (UInt16.MaxValue - FIRST_EPHEMERAL_PORT - 1)));

            // configure our ARP resolver's default IP address settings
            if (dhcpIpConfigEnabled)
            {
                // in case of DHCP, temporarily set our IP address to IP_ADDRESS_ANY (0.0.0.0)
                _arpResolver.SetIpv4Address(0);
            }
            else
            {
                _ipv4configIPAddress = ConvertIPAddressStringToUInt32BE((string)networkInterface.GetType().GetMethod("get_IPAddress").Invoke(networkInterface, new object[] { }));
                _ipv4configSubnetMask = ConvertIPAddressStringToUInt32BE((string)networkInterface.GetType().GetMethod("get_SubnetMask").Invoke(networkInterface, new object[] { }));
                _ipv4configGatewayAddress = ConvertIPAddressStringToUInt32BE((string)networkInterface.GetType().GetMethod("get_GatewayAddress").Invoke(networkInterface, new object[] { }));
                _arpResolver.SetIpv4Address(_ipv4configIPAddress);
            }

            // retrieve our DnsServer IP address configuration
            if (!dhcpDnsConfigEnabled)
            {
                string[] dnsAddressesString = (string[])networkInterface.GetType().GetMethod("get_DnsAddresses").Invoke(networkInterface, new object[] { });
                if (dnsAddressesString.Length > 0)
                {
                    _ipv4configDnsServerAddresses = new UInt32[dnsAddressesString.Length];
                    for (int iDnsAddress = 0; iDnsAddress < _ipv4configDnsServerAddresses.Length; iDnsAddress++)
                    {
                        _ipv4configDnsServerAddresses[iDnsAddress] = ConvertIPAddressStringToUInt32BE(dnsAddressesString[iDnsAddress]);
                    }
                }
                else // (dnsAddressesString.Length == 0)
                {
                    /* WORKAROUND: when DHCP is enabled, NETMF traditionally considers the DNS servers to be DHCP-allocated if no DNS servers are present.  Change the dhcpDnsConfigEnabled flag to true here if no DNS servers addresses are configured. */
                    if (dhcpIpConfigEnabled)
                            dhcpDnsConfigEnabled = true;
                }
            }

            // initialize our buffers
            for (int i = 0; i < _receivedPacketBuffers.Length; i++ )
            {
                _receivedPacketBuffers[i] = new ReceivedPacketBuffer();
                InitializeReceivedPacketBuffer(_receivedPacketBuffers[i]);
            }

            // wire up our IPv4PacketReceived handler
            _ethernetInterface.IPv4PacketReceived += _ethernetInterface_IPv4PacketReceived;
            // wire up our LinkStateChanged event handler
            _ethernetInterface.LinkStateChanged += _ethernetInterface_LinkStateChanged;

            // start our "loopback thread"
            _loopbackThread = new Thread(LoopbackInBackgroundThread);
            _loopbackThread.Start();

            // create our ICMPv4 handler instance
            _icmpv4Handler = new ICMPv4Handler(this);

            // create our DNS resolver instance
            _dnsResolver = new DnsResolver(this);

            // create our DHCP client instance
            _dhcpv4Client = new DHCPv4Client(this);
            _dhcpv4Client.IpConfigChanged += _dhcpv4Client_IpConfigChanged;
            _dhcpv4Client.DnsConfigChanged += _dhcpv4Client_DnsConfigChanged;

            // if we are configured to use DHCP, then create our DHCPv4Client instance now; its state machine will take care of ip configuration from there
            if (dhcpIpConfigEnabled || dhcpDnsConfigEnabled)
            {
                _dhcpv4Client.IsDhcpIpConfigEnabled = dhcpIpConfigEnabled;
                _dhcpv4Client.IsDhcpDnsConfigEnabled = dhcpDnsConfigEnabled;
            }

            // create our TCP handler instance
            _tcpHandler = new TcpHandler(this);

            // manually fire our LinkStateChanged event to set the initial state of our link.
            _ethernetInterface_LinkStateChanged(_ethernetInterface, _ethernetInterface.GetLinkState());
        }

        void _dhcpv4Client_DnsConfigChanged(object sender, uint[] dnsAddresses)
        {
            _ipv4configDnsServerAddresses = new UInt32[System.Math.Min(dnsAddresses.Length, 2)];
            Array.Copy(dnsAddresses, _ipv4configDnsServerAddresses, _ipv4configDnsServerAddresses.Length);
        }

        void _dhcpv4Client_IpConfigChanged(object sender, uint ipAddress, uint gatewayAddress, uint subnetMask)
        {
            _ipv4configIPAddress = ipAddress;
            _ipv4configSubnetMask = subnetMask;
            _ipv4configGatewayAddress = gatewayAddress;

            _arpResolver.SetIpv4Address(_ipv4configIPAddress);

            /*** TODO: we should re-send our gratuitious (or probe) ARP every time that our IP address changes (via DHCP or otherwise), and also every time our network link goes up ***/
            /*** TODO: if our IP address changes, should we update the source IP address on all of our sockets?  Should we close any active connection-based sockets?  ***/

            Type networkChangeListenerType = Type.GetType("Microsoft.SPOT.Net.NetworkInformation.NetworkChange+NetworkChangeListener, Microsoft.SPOT.Net");
            if (networkChangeListenerType != null)
            {
                // create instance of NetworkChangeListener
                System.Reflection.ConstructorInfo networkChangeListenerConstructor = networkChangeListenerType.GetConstructor(new Type[] { });
                object networkChangeListener = networkChangeListenerConstructor.Invoke(new object[] { });

                // now call the ProcessEvent function to create a NetworkEvent class.
                System.Reflection.MethodInfo processEventMethodType = networkChangeListenerType.GetMethod("ProcessEvent");
                object networkEvent = processEventMethodType.Invoke(networkChangeListener, new object[] { (UInt32)(((UInt32)2 /* AddressChanged*/)), (UInt32)0, DateTime.Now }); /* TODO: should this be DateTime.Now or DateTime.UtcNow? */

                // and finally call the static NetworkChange.OnNetworkChangeCallback function to raise the event.
                Type networkChangeType = Type.GetType("Microsoft.SPOT.Net.NetworkInformation.NetworkChange, Microsoft.SPOT.Net");
                if (networkChangeType != null)
                {
                    System.Reflection.MethodInfo onNetworkChangeCallbackMethod = networkChangeType.GetMethod("OnNetworkChangeCallback", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                    onNetworkChangeCallbackMethod.Invoke(networkChangeType, new object[] { networkEvent });
                }
            }
        }

        void _ethernetInterface_LinkStateChanged(object sender, bool state)
        {
            object networkInterface = Netduino.IP.Interop.NetworkInterface.GetNetworkInterface(0);
            bool dhcpEnabled = (bool)networkInterface.GetType().GetMethod("get_IsDhcpEnabled").Invoke(networkInterface, new object[] { });
            if (!dhcpEnabled)
            {
                /* TODO: we appear to be sending the gratuitous ARP too quickly upon first link; do we need to wait a few milliseconds before replying on the link to truly be "link up"?
                 *       consider adding a "delayUntilTicks" parameter to all ARP background-queued frames (which may also require them to be sorted by time).  
                 *       Also...if we are going to include an abritrary delay to our initial gratuitous ARP, perhaps we should do so by delaying the EthernetInterface.LinkStateChanged(true) event in the ILinkLayer driver
                 *       (on a per-chip basis, in case the delay is specific to certain MAC/PHY chips' requrirements for startup time rather than a network router requirement */
                if (state == true)
                    _arpResolver.SendArpGratuitousInBackground();
            }

            // if we have objects interested in link changes (such as a DHCPv4Client), let them know about the change now too.
            if (LinkStateChanged != null)
                LinkStateChanged(this, state);
        }

        void _ethernetInterface_IPv4PacketReceived(object sender, byte[] buffer, int index, int count)
        {
            // if IPv4 header is less than 20 bytes then drop packet
            if (count < 20) return;

            // if version field is not 4 (IPv4) then drop packet
            if ((buffer[index] >> 4) != 4) return;

            // get header and datagram lengths
            byte headerLength = (byte)((buffer[index] & 0x0F) * 4);
            UInt16 totalLength = (UInt16)((buffer[index + 2] << 8) + buffer[index + 3]);
            // check header checksum; calculating checksum over the entire header--including the checksum value--should result in 0x000.
            UInt16 checksum = Utility.CalculateInternetChecksum(buffer, index, headerLength);
            // if checksum does not match then drop packet
            if (checksum != 0x0000) return;

            /* NOTE: while we support reassmbly of fragmented frames, we will not process frames larger than 1536 bytes total. */
            /* NOTE: we have to cache partial frames in this class until all fragments are received (or timeout occurs) since we may not receive the fragment which indicates which socketHandle is the target until the 2nd or later fragment */

            /* TODO: add timer-based function which cleans up timed-out ReceivedPacketBuffers, in case we did not receive all fragments of a frame. */
            /* NOTE: we will enable a 30 second timeout PER INCOMING DATAGRAM; if all fragments have not been received before the timeout then we will discard the datagram from our buffers and send an ICMPv4 Time Exceeded (code 1) message;
             *       we do not restart the timeout after every fragment...it is a maximum timeout for the entire datagram.  also note that we can only send the ICMP timeout if we have received frame 0 (with the source port information) */
            UInt16 identification = (UInt16)((buffer[index + 4] << 8) + buffer[index + 5]);
            byte flags = (byte)(buffer[index + 6] >> 5);
            bool moreFragments = ((flags & 0x1) == 0x1);

            UInt16 fragmentOffset = (UInt16)((((((UInt16)buffer[index + 6]) & 0x1F) << 8) + buffer[index + 7]) << 3);

            ProtocolType protocol = (ProtocolType)buffer[index + 9];

            // get our source and destination IP addresses
            UInt32 sourceIPAddress = (UInt32)((buffer[index + 12] << 24) + (buffer[index + 13] << 16) + (buffer[index + 14] << 8) + buffer[index + 15]);
            UInt32 destinationIPAddress = (UInt32)((buffer[index + 16] << 24) + (buffer[index + 17] << 16) + (buffer[index + 18] << 8) + buffer[index + 19]);

            /* save this datagram in an incoming frame buffer; discard it if we do not have enough incoming frame buffers */
            ReceivedPacketBuffer receivedPacketBuffer = GetReceivedPacketBuffer(sourceIPAddress, destinationIPAddress, identification);
            if (receivedPacketBuffer == null) /* if we cannot retrieve or allocate a buffer, drop the packet */
                return;
            bool fragmentsMissing = false;
            Int32 actualFragmentLength = System.Math.Max(System.Math.Min(totalLength, count) - headerLength, 0);
            lock (receivedPacketBuffer.LockObject)
            {
                // copy the fragment into our packet buffer
                Array.Copy(buffer, index + headerLength, receivedPacketBuffer.Buffer, fragmentOffset, actualFragmentLength);
                // now recalculate the "holes" in our fragment
                for (int i = 0; i < receivedPacketBuffer.Holes.Length; i++)
                {
                    // if this fragment does not fill this hole, proceed to the next hole
                    if (fragmentOffset > receivedPacketBuffer.Holes[i].LastIndex)
                        continue;
                    if (fragmentOffset < receivedPacketBuffer.Holes[i].FirstIndex)
                        continue;

                    // this fragment fills part/all of the hole at the current index; clear the current index so we can populate new hole entr(ies)
                    Int32 holeFirstIndex = receivedPacketBuffer.Holes[i].FirstIndex;
                    Int32 holeLastIndex = receivedPacketBuffer.Holes[i].LastIndex;
                    receivedPacketBuffer.Holes[i].FirstIndex = -1;
                    receivedPacketBuffer.Holes[i].LastIndex = -1;

                    if (fragmentOffset > holeFirstIndex)
                    {
                        // our fragment does not fill the entire hole; create a hole entry to indicate that the first part of the hole is still not filled.
                        AddReceivedPacketBufferHoleEntry(receivedPacketBuffer, holeFirstIndex, fragmentOffset - 1);
                    }

                    if (fragmentOffset + actualFragmentLength - 1 < holeLastIndex)
                    {
                        // our fragment does not fill the entire hole; create a hole entry to indicate that the last part of the hole is still not filled.
                        AddReceivedPacketBufferHoleEntry(receivedPacketBuffer, fragmentOffset + actualFragmentLength , holeLastIndex);
                    }
                }
                // now determine if there are any missing fragments
                for (int i = 0; i < receivedPacketBuffer.Holes.Length; i++)
                {
                    // if this fragment is the final fragment (which lets us know the total size of our buffer), eliminate the "infinite-reaching" hole
                    if (moreFragments == false)
                    {
                        receivedPacketBuffer.ActualBufferLength = fragmentOffset + actualFragmentLength;
                        if (receivedPacketBuffer.Holes[i].LastIndex == Int32.MaxValue)
                        {
                            if (receivedPacketBuffer.Holes[i].FirstIndex == fragmentOffset + actualFragmentLength)
                            {
                                // this is now just dummy area beyond the datagram; remove it.
                                receivedPacketBuffer.Holes[i].FirstIndex = -1;
                                receivedPacketBuffer.Holes[i].LastIndex = -1;
                            }
                            else
                            {
                                // modify this hole to end at the proper LastIndex (since we now know the length of our reassembled datagram
                                receivedPacketBuffer.Holes[i].LastIndex = fragmentOffset + actualFragmentLength - 1;
                            }
                        }
                    }

                    if (receivedPacketBuffer.Holes[i].FirstIndex != -1 || receivedPacketBuffer.Holes[i].LastIndex != -1)
                        fragmentsMissing = true;
                }
            }

            if (!fragmentsMissing)
            {
                CallSocketPacketReceivedHandler(sourceIPAddress, destinationIPAddress, protocol, receivedPacketBuffer.Buffer, 0, receivedPacketBuffer.ActualBufferLength);
                // since we are caching the buffer locally in the socket class, free the packet buffer.
                InitializeReceivedPacketBuffer(receivedPacketBuffer);
            }
        }

        void AddReceivedPacketBufferHoleEntry(ReceivedPacketBuffer receivedPacketBuffer, Int32 firstIndex, Int32 lastIndex)
        {
            Int32 availableIndex = -1;
            for (Int32 i = 0; i < receivedPacketBuffer.Holes.Length; i++)
            {
                if (receivedPacketBuffer.Holes[i].FirstIndex == -1 && receivedPacketBuffer.Holes[i].LastIndex == -1)
                {
                    availableIndex = i;
                    break;
                }
            }

            // if we could not find an empty entry, enlarge the array.
            if (availableIndex == -1)
            {
                ReceivedPacketBufferHoles[] newHoles = new ReceivedPacketBufferHoles[receivedPacketBuffer.Holes.Length + 1];
                Array.Copy(receivedPacketBuffer.Holes, newHoles, receivedPacketBuffer.Holes.Length);
                receivedPacketBuffer.Holes = newHoles;
                availableIndex = receivedPacketBuffer.Holes.Length - 1;
            }

            receivedPacketBuffer.Holes[availableIndex].FirstIndex = firstIndex;
            receivedPacketBuffer.Holes[availableIndex].LastIndex = lastIndex;
        }

        void CallSocketPacketReceivedHandler(UInt32 sourceIPAddress, UInt32 destinationIPAddress, ProtocolType protocol, byte[] buffer, Int32 index, Int32 count)
        {
            Int32 socketHandle = -1;

            switch (protocol)
            {
                case ProtocolType.ICMPv4:
                    {
                        _icmpv4Handler.OnPacketReceived(sourceIPAddress, buffer, index, count);
                    }
                    break;
                case ProtocolType.Udp: /* UDP */
                    {
                        // find the destination port # for our packet (looking into the packet) 
                        UInt16 destinationIPPort = (UInt16)((((UInt16)buffer[index + 2]) << 8) + buffer[index + 3]);

                        for (int i = 0; i < MAX_SIMULTANEOUS_SOCKETS; i++)
                        {
                            if ((_sockets[i] != null) &&
                                (_sockets[i].ProtocolType == ProtocolType.Udp) &&
                                (_sockets[i].SourceIPPort == destinationIPPort))
                            {
                                if ((_ipv4configIPAddress == 0) || (destinationIPAddress == 0xFFFFFFFF) || (_ipv4configIPAddress == destinationIPAddress))
                                {
                                    socketHandle = i;
                                }
                            }
                        }

                        if (socketHandle != -1)
                        {
                            _sockets[socketHandle].OnPacketReceived(sourceIPAddress, destinationIPAddress, buffer, index, count);
                        }
                    }
                    break;
                case ProtocolType.Tcp:
                    {
                        _tcpHandler.OnPacketReceived(sourceIPAddress, destinationIPAddress, buffer, index, count);
                    }
                    break;
                //case ProtocolType.Igmp:
                default:   /* unsupported protocol; drop packet */
                    return;
            }
        }

        internal void InitializeReceivedPacketBuffer(ReceivedPacketBuffer buffer)
        {
            buffer.SourceIPAddress = 0;
            buffer.DestinationIPAddress = 0;
            buffer.Identification = 0;

            if (buffer.Buffer == null)
                buffer.Buffer = new byte[1500]; /* TODO: determine correct maximum size and make this a const */
            if ((buffer.Holes == null) || (buffer.Holes.Length != DEFAULT_NUM_BUFFER_HOLES_ENTRIES_PER_BUFFER))
                buffer.Holes = new ReceivedPacketBufferHoles[DEFAULT_NUM_BUFFER_HOLES_ENTRIES_PER_BUFFER];
            buffer.Holes[0].FirstIndex = 0;
            buffer.Holes[0].LastIndex = Int32.MaxValue;
            for (int i = 1; i < buffer.Holes.Length; i++)
            {
                buffer.Holes[i].FirstIndex = -1;
                buffer.Holes[i].LastIndex = -1;
            }
            buffer.ActualBufferLength = 0;

            buffer.TimeoutInMachineTicks = Int64.MaxValue;
            buffer.IsEmpty = true;
            if (buffer.LockObject == null)
                buffer.LockObject = new object();
        }

        internal ReceivedPacketBuffer GetReceivedPacketBuffer(UInt32 sourceIPAddress, UInt32 destinationIPAddress, UInt16 identification)
        {
            // first, look for an existing packet buffer
            for (int i = 0; i < _receivedPacketBuffers.Length; i++)
            {
                if ((_receivedPacketBuffers[i].IsEmpty == false) &&
                    (_receivedPacketBuffers[i].Identification == identification) &&
                    (_receivedPacketBuffers[i].SourceIPAddress == sourceIPAddress) &&
                    (_receivedPacketBuffers[i].DestinationIPAddress == destinationIPAddress))
                {
                    return _receivedPacketBuffers[i];
                }
            }

            // if no packet buffer exists for this packet, allocate one.
            for (int i = 0; i < _receivedPacketBuffers.Length; i++)
            {
                lock (_receivedPacketBuffers[i].LockObject)
                {
                    if (_receivedPacketBuffers[i].IsEmpty == false)
                        continue;

                    _receivedPacketBuffers[i].IsEmpty = false;
                    _receivedPacketBuffers[i].Identification = identification;
                    _receivedPacketBuffers[i].SourceIPAddress = sourceIPAddress;
                    _receivedPacketBuffers[i].DestinationIPAddress = destinationIPAddress;
                    _receivedPacketBuffers[i].TimeoutInMachineTicks = Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks + (TimeSpan.TicksPerMillisecond * REASSEMBLY_TIMEOUT_IN_SECONDS * 1000);
                    return _receivedPacketBuffers[i];
                }
            }

            // if we could not obtain or allocate a packet buffer, return null.
            return null;
        }

        internal void CloseSocket(int handle)
        {
            if (_sockets[handle] != null)
            {
                try
                {
                _sockets[handle].Close();
                }
                catch { }
                _sockets[handle].Dispose();
                _sockets[handle] = null;
            }

            lock (_handlesInUseBitmaskLockObject)
            {
                _handlesInUseBitmask &= ~((UInt64)1 << handle);
            }

            if (handle == 0)
            {
                _socketHandleReservedFreed.Set();
            }
            else
            {
                _socketHandleUserFreed.Set();
            }
        }

        /* this function returns a new handle...or -1 if no socket could be allocated */
        internal Int32 CreateSocket(ProtocolType protocolType, Int64 timeoutInMachineTicks, bool reservedSocket = false)
        {
            switch (protocolType)
            {
                case ProtocolType.Tcp:
                    {
                        int handle = reservedSocket ? GetReservedHandle(timeoutInMachineTicks) : GetNextHandle(timeoutInMachineTicks);
                        if (handle != -1)
                        {
                            _sockets[handle] = new TcpSocket(_tcpHandler, handle);
                            return handle;
                        }
                        else
                        {
                            // no handle available
                            //throw Utility.NewSocketException(SocketError.TooManyOpenSockets);
                            return -1;
                        }
                    }
                case ProtocolType.Udp:
                    {
                        int handle = reservedSocket ? GetReservedHandle(timeoutInMachineTicks) : GetNextHandle(timeoutInMachineTicks);
                        if (handle != -1)
                        {
                            _sockets[handle] = new UdpSocket(this, handle);
                            return handle;
                        }
                        else
                        {
                            // no handle available
                            //throw Utility.NewSocketException(SocketError.TooManyOpenSockets);
                            return -1;
                        }
                    }
                default:
                    throw new NotSupportedException();
            }
        }

        internal Socket GetSocket(int handle)
        {
            return _sockets[handle];
        }

        /* this function returns the reserved handle (handle zero) if it is available */
        /* TODO: enable a timeout on this function which waits until the reserved handle is available */
        int GetReservedHandle(Int64 timeoutInMachineTicks)
        {
            while (true)
            {
                lock (_handlesInUseBitmaskLockObject)
                {
                    if ((_handlesInUseBitmask & (1 << 0)) == 0)
                    {
                        _handlesInUseBitmask |= (1 << 0);
                        return 0;
                    }
                }

                /* if we could not immediately retrieve the handle, wait for it to be freed. */
                Int32 waitTimeout = (Int32)((timeoutInMachineTicks != Int64.MaxValue) ? System.Math.Max((timeoutInMachineTicks - Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks) / System.TimeSpan.TicksPerMillisecond, 0) : System.Threading.Timeout.Infinite);
                if (!_socketHandleReservedFreed.WaitOne(waitTimeout, false))
                    break; /* if we could not obtain the WaitHandle before timeout, break free and return "no free handles" */
            }

            /* if we reach here, there are no free handles. */
            return -1;
        }

        int GetNextHandle(Int64 timeoutInMachineTicks)
        {
            while (true)
            {
                lock (_handlesInUseBitmaskLockObject)
                {
                    /* check all available handles from 1 to MAX_SIMULTANEOUS_SOCKETS - 1; handle #0 is reserved for our internal (DHCP, DNS, etc.) use */
                    for (int i = 1; i < MAX_SIMULTANEOUS_SOCKETS; i++)
                    {
                        if ((_handlesInUseBitmask & ((UInt64)1 << i)) == 0)
                        {
                            _handlesInUseBitmask |= ((UInt64)1 << i);
                            return i;
                        }
                    }
                }

                /* if we could not immediately retrieve the handle, wait for it to be freed. */
                Int32 waitTimeout = (Int32)((timeoutInMachineTicks != Int64.MaxValue) ? System.Math.Max((timeoutInMachineTicks - Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks) / System.TimeSpan.TicksPerMillisecond, 0) : System.Threading.Timeout.Infinite);
                if (!_socketHandleUserFreed.WaitOne(waitTimeout, false))
                    break; /* if we could not obtain the WaitHandle before timeout, break free and return "no free handles" */
            }

            /* if we reach here, there are no free handles. */
            return -1;
        }

        void LoopbackInBackgroundThread()
        {
            while (true)
            {
                _loopbackBufferFilledEvent.WaitOne();

                 // if we have been disposed, shut down our thread now.
                if (_isDisposed)
                    return;

                if (_loopbackBufferInUse)
                {
                    lock (_loopbackBufferLockObject)
                    {
                        // send our loopback frame data to our incoming frame handler
                        CallSocketPacketReceivedHandler(_loopbackSourceIPAddress, _loopbackDestinationIPAddress, _loopbackProtocol, _loopbackBuffer, _loopbackBufferIndex, _loopbackBufferCount);
                        // free our loopback frame
                        _loopbackBufferInUse = false;
                        _loopbackBufferFreedEvent.Set();
                    }
                }
            }
        }

        public void Send(byte protocol, UInt32 srcIPAddress, UInt32 dstIPAddress, byte[][] buffer, int[] offset, int[] count, Int64 timeoutInMachineTicks)
        {
            /* if we are receiving more than (MAX_BUFFER_COUNT - 1) buffers, abort; if we need more, we'll have to change our array sizes at top */
            if (buffer.Length > MAX_BUFFER_SEGMENT_COUNT - 1)
                throw new ArgumentException();

            // determine whether dstIPAddress is a local address or a remote address.
            UInt64 dstPhysicalAddress;
            if ((dstIPAddress == _ipv4configIPAddress) || ((dstIPAddress & LOOPBACK_SUBNET_MASK) == (LOOPBACK_IP_ADDRESS & LOOPBACK_SUBNET_MASK)))
            {
                // loopback: the destination is ourselves

                // if the loopback buffer is in use then wait for it to be freed (or until our timeout occurs); if timeout occrs then drop the packet
                bool loopbackBufferInUse = _loopbackBufferInUse;
                if (loopbackBufferInUse)
                {
                    Int32 waitTimeout = (Int32)((timeoutInMachineTicks != Int64.MaxValue) ? System.Math.Max((timeoutInMachineTicks - Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks) / System.TimeSpan.TicksPerMillisecond, 0) : System.Threading.Timeout.Infinite);
                    loopbackBufferInUse = !(_loopbackBufferFreedEvent.WaitOne(waitTimeout, false));
                }

                if (!loopbackBufferInUse)
                {
                    lock (_loopbackBufferLockObject)
                    {
                        _loopbackProtocol = (ProtocolType)protocol;
                        _loopbackSourceIPAddress = srcIPAddress;
                        _loopbackDestinationIPAddress = dstIPAddress;

                        // if we haven't needed loopback yet, allocate our loopback buffer now.
                        if (_loopbackBuffer == null)
                            _loopbackBuffer = new byte[LOOPBACK_BUFFER_SIZE];

                        int loopbackBufferCount = 0;
                        for (int iBuffer = 0; iBuffer < buffer.Length; iBuffer++)
                        {
                            Array.Copy(buffer[iBuffer], offset[iBuffer], _loopbackBuffer, loopbackBufferCount, count[iBuffer]);
                            loopbackBufferCount += count[iBuffer];
                        }
                        _loopbackBufferCount = loopbackBufferCount;
                        _loopbackBufferInUse = true;

                        _loopbackBufferFilledEvent.Set();
                    }
                }
                return;
            }
            else if (dstIPAddress == 0xFFFFFFFF)
            {
                // direct delivery: this destination address is our broadcast address
                /* get destinationPhysicalAddress of dstIPAddress */
                dstPhysicalAddress = _arpResolver.TranslateIPAddressToPhysicalAddress(dstIPAddress, timeoutInMachineTicks);
            }
            else if ((dstIPAddress & _ipv4configSubnetMask) == (_ipv4configIPAddress & _ipv4configSubnetMask))
            {
                // direct delivery: this destination address is on our local subnet
                /* get destinationPhysicalAddress of dstIPAddress */
                dstPhysicalAddress = _arpResolver.TranslateIPAddressToPhysicalAddress(dstIPAddress, timeoutInMachineTicks);
            }
            else
            {
                // indirect delivery; send the frame to our gateway instead
                /* get destinationPhysicalAddress of dstIPAddress */
                dstPhysicalAddress = _arpResolver.TranslateIPAddressToPhysicalAddress(_ipv4configGatewayAddress, timeoutInMachineTicks);
            }
            if (dstPhysicalAddress == 0)
                throw Utility.NewSocketException(SocketError.HostUnreachable);  /* TODO: consider returning a success/fail as bool from this function */

            lock (_ipv4HeaderBufferLockObject)
            {
                int headerLength = IPV4_HEADER_MIN_LENGTH;
                int dataLength = 0;
                for (int i = 0; i < buffer.Length; i++)
                {
                    dataLength += count[i];
                }

                // we will send the data in fragments if the total data length exceeds 1500 bytes
                Int32 fragmentOffset = 0;
                Int32 fragmentLength;

                /* NOTE: we send the fragment offsets in reverse order so that the destination host has a chance to create the full buffer size before receiving additional fragments */
                if (dataLength > MAX_IPV4_DATA_FRAGMENT_SIZE)
                    fragmentOffset = dataLength - (dataLength % MAX_IPV4_DATA_FRAGMENT_SIZE);
                while (fragmentOffset >= 0)
                {
                    fragmentLength = System.Math.Min(dataLength - fragmentOffset, MAX_IPV4_DATA_FRAGMENT_SIZE);

                    // populate the header fields
                    _ipv4HeaderBuffer[1] = 0; /* leave the DSField/ECN fields blank */
                    UInt16 identification = GetNextDatagramID();
                    _ipv4HeaderBuffer[4] = (byte)((identification >> 8) & 0xFF);
                    _ipv4HeaderBuffer[5] = (byte)(identification & 0xFF);
                    // TODO: populate flags and fragmentation fields, if necessary
                    _ipv4HeaderBuffer[6] = (byte)(
                        (/* (flags << 5) + */ ((fragmentOffset >> 11) & 0xFF))
                        | ((fragmentOffset + fragmentLength == dataLength) ? 0 : 0x20) /* set MF (More Fragments) bit if this is not the only/last fragment in a datagram */
                        ); 
                    _ipv4HeaderBuffer[7] = (byte)((fragmentOffset >> 3) & 0xFF);
                    // populate the TTL (MaxHopCount) and protocol fields
                    _ipv4HeaderBuffer[8] = DEFAULT_TIME_TO_LIVE;
                    _ipv4HeaderBuffer[9] = protocol;
                    // fill in source and destination addresses
                    _ipv4HeaderBuffer[12] = (byte)((srcIPAddress >> 24) & 0xFF);
                    _ipv4HeaderBuffer[13] = (byte)((srcIPAddress >> 16) & 0xFF);
                    _ipv4HeaderBuffer[14] = (byte)((srcIPAddress >> 8) & 0xFF);
                    _ipv4HeaderBuffer[15] = (byte)(srcIPAddress & 0xFF);
                    _ipv4HeaderBuffer[16] = (byte)((dstIPAddress >> 24) & 0xFF);
                    _ipv4HeaderBuffer[17] = (byte)((dstIPAddress >> 16) & 0xFF);
                    _ipv4HeaderBuffer[18] = (byte)((dstIPAddress >> 8) & 0xFF);
                    _ipv4HeaderBuffer[19] = (byte)(dstIPAddress & 0xFF);

                    /* TODO: populate any datagram options */
                    // pseudocode: while (options) { AddOptionAt(_upV4HeaderBuffer[20 + offset]); headerLength += 4 };

                    // insert the length (and header length)
                    _ipv4HeaderBuffer[0] = (byte)((0x04 << 4) /* version: IPv4 */ + (headerLength / 4)) /* Internet Header Length: # of 32-bit words */;
                    _ipv4HeaderBuffer[2] = (byte)(((headerLength + fragmentLength) >> 8) & 0xFF); /* MSB of total datagram length */
                    _ipv4HeaderBuffer[3] = (byte)((headerLength + fragmentLength) & 0xFF);        /* LSB of total datagram length */

                    // finally calculate the header checksum
                    // for checksum calculation purposes, the checksum field must be empty.
                    _ipv4HeaderBuffer[10] = 0;
                    _ipv4HeaderBuffer[11] = 0;
                    UInt16 checksum = Netduino.IP.Utility.CalculateInternetChecksum(_ipv4HeaderBuffer, 0, headerLength);
                    _ipv4HeaderBuffer[10] = (byte)((checksum >> 8) & 0xFF);
                    _ipv4HeaderBuffer[11] = (byte)(checksum & 0xFF);

                    // queue up our buffer arrays
                    _bufferArray[0] = _ipv4HeaderBuffer;
                    _indexArray[0] = 0;
                    _countArray[0] = headerLength;

                    Int32 totalBufferOffset = 0;
                    Int32 bufferArrayLength = 1; /* we start with index 1, after our IPv4 header */
                    for (int i = 0; i < buffer.Length; i++)
                    {
                        if (totalBufferOffset + count[i] > fragmentOffset)
                        {
                            // add data from this buffer to our set of downstream buffers
                            _bufferArray[bufferArrayLength] = buffer[i];
                            _indexArray[bufferArrayLength] = offset[i] + System.Math.Max(0, (fragmentOffset - totalBufferOffset));
                            _countArray[bufferArrayLength] = System.Math.Min(count[i] - System.Math.Max(0, (fragmentOffset - totalBufferOffset)), fragmentLength - totalBufferOffset);
                            bufferArrayLength++;
                        }
                        else
                        {
                            // we have not yet reached our fragment point; increment our totalBufferOffset and move to the next buffer.
                        }
                        totalBufferOffset += count[i];

                        // if we have filled our fragment buffer set completely, break out now.
                        if (totalBufferOffset >= fragmentOffset + fragmentLength)
                            break;
                    }

                    // send the datagram (or datagram fragment)
                    _ethernetInterface.Send(dstPhysicalAddress, 0x0800 /* dataType: IPV4 */, srcIPAddress, dstIPAddress, bufferArrayLength, _bufferArray, _indexArray, _countArray, timeoutInMachineTicks);

                    fragmentOffset -= MAX_IPV4_DATA_FRAGMENT_SIZE;
                }
            }
        }

        UInt16 GetNextDatagramID()
        {
            lock (_nextDatagramIDLockObject)
            {
                return _nextDatagramID++;
            }
        }

        internal UInt16 GetNextEphemeralPortNumber(ProtocolType protocolType)
        {
            UInt16 nextEphemeralPort = 0;
            bool foundAvailablePort = false;
            while (!foundAvailablePort)
            {
                lock (_nextEphemeralPortLockObject)
                {
                    nextEphemeralPort = _nextEphemeralPort++;
                    // if we have wrapped around, then reset to the first ephemeral port
                    if (_nextEphemeralPort == 0)
                        _nextEphemeralPort = FIRST_EPHEMERAL_PORT;
                    foundAvailablePort = true; /* default to "port is available" */
                }

                /* NOTE: for purposes of ephemeral ports, we do not distinguish between multiple potential IP addresses assigned to this NIC. */
                // check and make sure we're not already using this port # on another socket (although ports used on TCP can be re-used on UDP, etc.)
                foreach (Socket socket in _sockets)
                {
                    if (socket == null)
                        continue;

                    if (socket.SourceIPPort == nextEphemeralPort && socket.ProtocolType == protocolType)
                        foundAvailablePort = false;
                }
            }

            return nextEphemeralPort;
        }

        static UInt32 ConvertIPAddressStringToUInt32BE(string ipAddress)
        {
            if (ipAddress == null)
                throw new ArgumentNullException();

            ulong ipAddressValue = 0;
            int lastIndex = 0;
            int shiftIndex = 24;
            ulong mask = 0x00000000FF000000;
            ulong octet = 0L;
            int length = ipAddress.Length;

            for (int i = 0; i < length; ++i)
            {
                // Parse to '.' or end of IP address
                if (ipAddress[i] == '.' || i == length - 1)
                    // If the IP starts with a '.'
                    // or a segment is longer than 3 characters or shiftIndex > last bit position throw.
                    if (i == 0 || i - lastIndex > 3 || shiftIndex > 24)
                    {
                        throw new ArgumentException();
                    }
                    else
                    {
                        i = i == length - 1 ? ++i : i;
                        octet = (ulong)(ConvertStringToInt32(ipAddress.Substring(lastIndex, i - lastIndex)) & 0x00000000000000FF);
                        ipAddressValue = ipAddressValue + (ulong)((octet << shiftIndex) & mask);
                        lastIndex = i + 1;
                        shiftIndex = shiftIndex - 8;
                        mask = (mask >> 8);
                    }
            }

            return (uint)ipAddressValue;
        }

        static int ConvertStringToInt32(string value)
        {
            char[] num = value.ToCharArray();
            int result = 0;

            bool isNegative = false;
            int signIndex = 0;

            if (num[0] == '-')
            {
                isNegative = true;
                signIndex = 1;
            }
            else if (num[0] == '+')
            {
                signIndex = 1;
            }

            int exp = 1;
            for (int i = num.Length - 1; i >= signIndex; i--)
            {
                if (num[i] < '0' || num[i] > '9')
                {
                    throw new ArgumentException();
                }

                result += ((num[i] - '0') * exp);
                exp *= 10;
            }

            return (isNegative) ? (-1 * result) : result;
        }

        internal UInt32 IPAddress
        {
            get
            {
                return _ipv4configIPAddress;
            }
        }

        internal UInt32 SubnetMask
        {
            get
            {
                return _ipv4configSubnetMask;
            }
        }

        internal UInt32 GatewayAddress
        {
            get
            {
                return _ipv4configGatewayAddress;
            }
        }

        internal UInt32[] DnsServerAddresses
        {
            get
            {
                return _ipv4configDnsServerAddresses;
            }
        }

        internal UInt64 GetPhysicalAddressAsUInt64()
        {
            return _ethernetInterface.PhysicalAddressAsUInt64;
        }

        internal UInt32[] ResolveHostNameToIpAddresses(string name, out string canonicalName)
        {
            /* special case: if the passed-in name is empty, return our local IP address */
            if (name == string.Empty)
            {
                canonicalName = string.Empty;
                return new UInt32[] { _ipv4configIPAddress };
            }

            return _dnsResolver.ResolveHostNameToIpAddresses(name, out canonicalName, Int64.MaxValue);
        }

        public void Dispose()
        {
            if (_isDisposed) return;

            _isDisposed = true;

            // shut down our loopback thread
            if (_loopbackBufferFilledEvent != null)
            {
                _loopbackBufferFilledEvent.Set();
                _loopbackBufferFilledEvent = null;
            }

            _ethernetInterface = null;
            _ipv4HeaderBuffer = null;
            _ipv4HeaderBufferLockObject = null;

            _dhcpv4Client.Dispose();
            _dhcpv4Client = null;

            _icmpv4Handler.Dispose();
            _icmpv4Handler = null;

            _dnsResolver.Dispose();
            _dnsResolver = null;

            _tcpHandler.Dispose();
            _tcpHandler = null;

            _bufferArray = null;
            _indexArray = null;
            _countArray = null;
        }
    }
}
