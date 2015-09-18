using System;
using System.Threading;

namespace Netduino.IP
{
    internal class DHCPv4Client : IDisposable 
    {
        const int DHCP_FRAME_BUFFER_LENGTH = 548;
        const int DHCP_MESSAGE_MAXIMUM_SIZE = DHCP_FRAME_BUFFER_LENGTH + IPv4Layer.IPV4_HEADER_MIN_LENGTH + UdpSocket.UDP_HEADER_LENGTH; /* = 576 bytes */

        const byte HARDWARE_TYPE_ETHERNET = 0x01;
        const byte HARDWARE_ADDRESS_SIZE = 6;

        const UInt16 DHCP_CLIENT_PORT = 68;
        const UInt16 DHCP_SERVER_PORT = 67;

        System.Random _randomGenerator = new Random();

        bool _isDhcpDnsConfigEnabled = false;
        bool _isDhcpIpConfigEnabled = false;
        bool _linkState = false;

        UInt32 _dhcpServerAddress = 0;
        UInt32 _ipConfigIPAddress = 0;
        UInt32 _ipConfigSubnetMask = 0;
        UInt32 _ipConfigGatewayAddress = 0;

        Int64 _leaseExpirationTimeInMachineTicks = Int64.MaxValue;
        Int64 _leaseRenewalTimeInMachineTicks = Int64.MaxValue;
        Int64 _leaseRebindingTimeInMachineTicks = Int64.MaxValue;

        System.Threading.Timer _leaseUpdateTimer;

        public delegate void IpConfigChangedEventHandler(object sender, UInt32 ipAddress, UInt32 gatewayAddress, UInt32 subnetMask);
        internal event IpConfigChangedEventHandler IpConfigChanged;

        public delegate void DnsConfigChangedEventHandler(object sender, UInt32[] dnsAddresses);
        internal event DnsConfigChangedEventHandler DnsConfigChanged;

        struct DhcpOption
        {
            public DhcpOptionCode Code;
            public byte[] Value;

            public DhcpOption(DhcpOptionCode code, byte[] value)
            {
                this.Code = code;
                this.Value = value;
            }
        }

        struct DhcpOptionsBlockRange
        {
            public Int32 BeginOffset;
            public Int32 EndOffset;

            public DhcpOptionsBlockRange(Int32 beginOffset, Int32 endOffset)
            {
                this.BeginOffset = beginOffset;
                this.EndOffset = endOffset;
            }
        }

        enum BootpOperation : ushort
        {
            BOOTREQUEST = 0x01,
            BOOTREPLY = 0x02,
        }

        enum DhcpOptionCode : byte
        {
            Pad = 0,
            SubnetMask = 1,
            //TimeOffset = 2,
            Router = 3,
            //TimeServer = 4,
            //NameServer = 5,
            DomainNameServer = 6,
            //LogServer = 7,
            //CookieServer = 8,
            //LprServer = 9,
            //ImpressServer = 10,
            //ResourceLocationServer = 11,
            //HostName = 12,
            //BootFileSize = 13,
            //MeritDumpFile = 14,
            //DomainName = 15,
            //SwapServer = 16,
            //RootPath = 17,
            //ExtensionsPath = 18,
            //IPForwardingEnableDisable = 19,
            //NonLocalSourceRoutingEnableDisable = 20,
            //PolicyFilter = 21,
            //MaximumDatagramReasssemblySize = 22,
            //DefaultIPTimeToLive = 23,
            //PathMtuAgingTimeout = 24,
            //PathMtuPlateauTable = 25,
            //InterfaceMtu = 26,
            //AllSubnetsAreLocal = 27,
            //BroadcastAddress = 28,
            //PerformMaskDiscovery = 29,
            //MaskSupplier = 30,
            //PerformRouterDiscovery = 31,
            //RouterSolicitationAddress = 32,
            //StaticRoute = 33,
            //TrailerEncapsulation = 34,
            //ArpCacheTimeout = 35,
            //EthernetEncapsulation = 36,
            //TcpDefaultTtl = 37,
            //TcpKeepaliveInterval = 38,
            //TcpKeepaliveGarbage = 39,
            //NetworkInformationServiceDomain = 40,
            //NetworkInformationServers = 41,
            //NetworkTimeProtocolServers = 42,
            //VendorSpecificInformation = 43,
            //NetbiosOverTCPIPNameServer = 44,
            //NetbiosOverTCPIPDatagramDistributionServer = 45,
            //NetbiosOverTCPIPNodeType = 46,
            //NetbiosOverTCPIPScope = 47,
            //XWindowSystemFontServer = 48,
            //XWindowSystemDisplayManager = 49,
            RequestedIPAddress = 50,
            IPAddressLeaseTime = 51,
            OptionOverload = 52,
            DhcpMessageType = 53,
            ServerIdentifier = 54,
            ParameterRequestList = 55,
            //Message = 56,
            MaximumDhcpMessageSize = 57,
            RenewalTimeValue = 58,
            RebindingTimeValue = 59,
            //ClassIdentifier = 60,
            ClientIdentifier = 61,
            End = 255
        }

        enum DhcpMessageType : byte
        {
            DHCPDISCOVER = 0x01,
            DHCPOFFER = 0x02,
            DHCPREQUEST = 0x03,
            //DHCPDECLINE = 0x04,
            DHCPACK = 0x05,
            DHCPNAK = 0x06,
            //DHCPRELEASE = 0x07,
            DHCPINFORM = 0x08,
            //DHCPFORCERENEW = 0x09,
            //DHCPLEASEQUERY = 0x0A,
            //DHCPLEASEUNASSIGNED = 0x0B,
            //DHCPLEASEUNKNOWN = 0x0C,
            //DHCPLEASEACTIVE = 0x0D,
        }

        enum DhcpStateMachineState : byte
        {
            InitReboot,
            Bound,
            Renewing,
            Rebinding,
        }
        Thread _dhcpStateMachineThread;
        AutoResetEvent _dhcpStateMachineEvent = new AutoResetEvent(false);
        DhcpStateMachineState _dhcpStateMachineState = DhcpStateMachineState.InitReboot;

        struct DhcpOffer
        {
            public UInt32 IPAddress;
            public UInt32 SubnetMask;
            public UInt32 GatewayAddress;
            public UInt32[] DnsAddresses;

            public UInt32 ServerIdentifier;
            public UInt32 LeaseExpirationTimeInSeconds;
            public UInt32 LeaseRenewalTimeInSeconds;
            public UInt32 LeaseRebindingTimeInSeconds;

            public UInt32 TransactionID;

            public DhcpOffer(UInt32 transactionID, UInt32 serverIdentifier, UInt32 ipAddress, UInt32 subnetMask, UInt32 gatewayAddress, UInt32[] dnsAddreses)
            {
                this.TransactionID = transactionID;
                this.ServerIdentifier = serverIdentifier;
                this.IPAddress = ipAddress;
                this.SubnetMask = subnetMask;
                this.GatewayAddress = gatewayAddress;
                if (dnsAddreses != null)
                {
                    this.DnsAddresses = dnsAddreses;
                }
                else
                {
                    this.DnsAddresses = new UInt32[0];
                }

                this.LeaseExpirationTimeInSeconds = UInt32.MaxValue;
                this.LeaseRenewalTimeInSeconds = UInt32.MaxValue;
                this.LeaseRebindingTimeInSeconds = UInt32.MaxValue;
            }
        }

        IPv4Layer _ipv4Layer;
        UInt64 _physicalAddress;

        bool _isDisposed = false;

        public DHCPv4Client(IPv4Layer ipv4Layer)
        {
            _ipv4Layer = ipv4Layer;
            _physicalAddress = ipv4Layer.GetPhysicalAddressAsUInt64();

            // create DHCP state machine thread
            _dhcpStateMachineThread = new Thread(DhcpStateMachine);
            _dhcpStateMachineThread.Start();

            _leaseUpdateTimer = new Timer(LeaseUpdateTimerCallback, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

            // wire up LinkStateChanged event
            _ipv4Layer.LinkStateChanged += _ipv4Layer_LinkStateChanged;
        }

        public void Dispose()
        {
            if (_isDisposed) return;

            _isDisposed = true;

            if (_leaseUpdateTimer != null)
                _leaseUpdateTimer.Dispose();

            // shut down our state machine thread
            if (_dhcpStateMachineEvent != null)
            {
                _dhcpStateMachineEvent.Set();
                _dhcpStateMachineEvent = null;
            }

            //_cleanupArpCacheTimer.Dispose();

            try
            {
                _ipv4Layer.LinkStateChanged -= _ipv4Layer_LinkStateChanged;
            }
            catch { }
            _ipv4Layer = null;

            //_bufferArray = null;
            //_indexArray = null;
            //_countArray = null;

            _randomGenerator = null;

            // unwire all event handlers
            IpConfigChanged = null;
            DnsConfigChanged = null;
        }

        void _ipv4Layer_LinkStateChanged(object sender, bool state)
        {
            _linkState = state;
            // let our DHCP state machine know that the link state has changed
            _dhcpStateMachineEvent.Set();
        }

        internal bool IsDhcpDnsConfigEnabled
        {
            get
            {
                return _isDhcpDnsConfigEnabled;
            }
            set
            {
                if (_isDhcpDnsConfigEnabled != value)
                { 
                    _isDhcpDnsConfigEnabled = value;
                    // let our DHCP state machine know that the dhcp config setting has changed
                    _dhcpStateMachineEvent.Set();
                }
            }
        }

        internal bool IsDhcpIpConfigEnabled
        {
            get
            {
                return _isDhcpIpConfigEnabled;
            }
            set
            {
                if (_isDhcpIpConfigEnabled != value)
                {
                    _isDhcpIpConfigEnabled = value;
                    // let our DHCP state machine know that the dhcp ip setting has changed
                    _dhcpStateMachineEvent.Set();
                }
            }
        }

        void DhcpStateMachine()
        {
            while (true)
            {
                // wait for a change which requires the DHCPv4 state machine to process data
                _dhcpStateMachineEvent.WaitOne();

                if (_isDisposed)
                    return;

                if (_linkState == false)
                {
                    if (_isDhcpIpConfigEnabled && _dhcpStateMachineState != DhcpStateMachineState.InitReboot)
                    {
                        if (IpConfigChanged != null)
                            IpConfigChanged(this, 0, 0, 0);

                        if (_isDhcpDnsConfigEnabled)
                        {
                            if (DnsConfigChanged != null)
                                DnsConfigChanged(this, new UInt32[] { });
                        }
                    }

                    _dhcpStateMachineState = DhcpStateMachineState.InitReboot;
                }
                else /* if(_linkState == true) */
                {
                    // if DHCPv4 config is enabled, process our state machine now.
                    if (_isDhcpIpConfigEnabled)
                    {
                        Int64 currentMachineTicks = Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks;
                        if (currentMachineTicks > _leaseExpirationTimeInMachineTicks)
                        {
                            // lease has expired
                            _ipConfigIPAddress = 0;
                            _ipConfigSubnetMask = 0;
                            _ipConfigGatewayAddress = 0;
                            _dhcpServerAddress = 0;
                            _leaseRenewalTimeInMachineTicks = Int64.MaxValue;
                            _leaseRebindingTimeInMachineTicks = Int64.MaxValue;
                            _leaseExpirationTimeInMachineTicks = Int64.MaxValue;
                            _dhcpStateMachineState = DhcpStateMachineState.InitReboot;

                            _dhcpStateMachineEvent.Set(); /* restart DHCP inquiry process */
                            continue;
                        }

                        switch (_dhcpStateMachineState)
                        {
                            case DhcpStateMachineState.InitReboot:
                                {
                                    /* we are now discovering DhcpServers and collecting offers */
                                    UInt16 secondsElapsed;
                                    DhcpOffer[] dhcpOffers = SendDhcpDiscoverAndCollectOffers(out secondsElapsed, Int64.MaxValue);

                                    if (dhcpOffers.Length == 0)
                                    {
                                        _dhcpStateMachineState = DhcpStateMachineState.InitReboot;
                                        _dhcpStateMachineEvent.Set(); /* restart DHCP inquiry process */
                                        break;
                                    }

                                    // we received one or more dhcp offers; generate a DhcpRequest now.
                                    DhcpOffer dhcpOffer = dhcpOffers[0];

                                    bool success = SendDhcpRequestAndWaitForAck(dhcpOffer, secondsElapsed, 0xFFFFFFFF, 0, Int64.MaxValue);
                                    if (!success)
                                    {
                                        _dhcpStateMachineState = DhcpStateMachineState.InitReboot;
                                        _dhcpStateMachineEvent.Set(); /* restart DHCP inquiry process */
                                        break;
                                    }

                                    // we have set our new IP address!
                                    _dhcpServerAddress = dhcpOffer.ServerIdentifier;
                                    _ipConfigIPAddress = dhcpOffer.IPAddress;
                                    _ipConfigSubnetMask = dhcpOffer.SubnetMask;
                                    _ipConfigGatewayAddress = dhcpOffer.GatewayAddress;
                                    _dhcpStateMachineState = DhcpStateMachineState.Bound;

                                    /* configure our renewing/rebinding/expiration timers */
                                    if (dhcpOffer.LeaseExpirationTimeInSeconds == UInt32.MaxValue)
                                    {
                                        _leaseExpirationTimeInMachineTicks = Int64.MaxValue;
                                        _leaseRenewalTimeInMachineTicks = Int64.MaxValue;
                                        _leaseRebindingTimeInMachineTicks = Int64.MaxValue;
                                    }
                                    else
                                    {
                                        _leaseExpirationTimeInMachineTicks = currentMachineTicks + (dhcpOffer.LeaseExpirationTimeInSeconds * TimeSpan.TicksPerSecond);
                                        _leaseRenewalTimeInMachineTicks = currentMachineTicks + (dhcpOffer.LeaseRenewalTimeInSeconds * TimeSpan.TicksPerSecond);
                                        _leaseRebindingTimeInMachineTicks = currentMachineTicks + (dhcpOffer.LeaseRebindingTimeInSeconds * TimeSpan.TicksPerSecond);

                                        _leaseUpdateTimer.Change((Int32)((_leaseRenewalTimeInMachineTicks - currentMachineTicks) / TimeSpan.TicksPerMillisecond), System.Threading.Timeout.Infinite);
                                    }

                                    if (IpConfigChanged != null)
                                    {
                                        IpConfigChanged(this, _ipConfigIPAddress, _ipConfigGatewayAddress, _ipConfigSubnetMask);
                                    }
                                    if ((_isDhcpDnsConfigEnabled) && (DnsConfigChanged != null))
                                    {
                                        DnsConfigChanged(this, dhcpOffer.DnsAddresses);
                                    }
                                }
                                break;
                            case DhcpStateMachineState.Bound:
                                {
                                    if (currentMachineTicks > _leaseRenewalTimeInMachineTicks)
                                    {
                                        _dhcpStateMachineState = DhcpStateMachineState.Renewing;

                                        DhcpOffer dhcpOffer = new DhcpOffer(GenerateRandomTransactionID(), 0, 0, 0, 0, new UInt32[0]);
                                        bool success = SendDhcpRequestAndWaitForAck(dhcpOffer, 0, _dhcpServerAddress, _ipConfigIPAddress, Int64.MaxValue);
                                        if (success)
                                        {
                                            _dhcpStateMachineState = DhcpStateMachineState.Bound;

                                            /* configure our renewing/rebinding/expiration timers */
                                            if (dhcpOffer.LeaseExpirationTimeInSeconds == UInt32.MaxValue)
                                            {
                                                _leaseExpirationTimeInMachineTicks = Int64.MaxValue;
                                                _leaseRenewalTimeInMachineTicks = Int64.MaxValue;
                                                _leaseRebindingTimeInMachineTicks = Int64.MaxValue;
                                            }
                                            else
                                            {
                                                _leaseExpirationTimeInMachineTicks = currentMachineTicks + (dhcpOffer.LeaseExpirationTimeInSeconds * TimeSpan.TicksPerSecond);
                                                _leaseRenewalTimeInMachineTicks = currentMachineTicks + (dhcpOffer.LeaseRenewalTimeInSeconds * TimeSpan.TicksPerSecond);
                                                _leaseRebindingTimeInMachineTicks = currentMachineTicks + (dhcpOffer.LeaseRebindingTimeInSeconds * TimeSpan.TicksPerSecond);
                                            }
                                        }
                                        else
                                        {
                                            _leaseUpdateTimer.Change((Int32)((_leaseRebindingTimeInMachineTicks - currentMachineTicks) / TimeSpan.TicksPerMillisecond), System.Threading.Timeout.Infinite);
                                        }
                                    }
                                    else
                                    {
                                        _leaseUpdateTimer.Change((Int32)((_leaseRenewalTimeInMachineTicks - currentMachineTicks) / TimeSpan.TicksPerMillisecond), System.Threading.Timeout.Infinite);
                                    }
                                }
                                break;
                            case DhcpStateMachineState.Renewing:
                                {
                                    if (currentMachineTicks > _leaseRebindingTimeInMachineTicks)
                                    {
                                        _dhcpStateMachineState = DhcpStateMachineState.Rebinding;

                                        DhcpOffer dhcpOffer = new DhcpOffer(GenerateRandomTransactionID(), 0, 0, 0, 0, new UInt32[0]);
                                        bool success = SendDhcpRequestAndWaitForAck(dhcpOffer, 0, 0xFFFFFFFF, _ipConfigIPAddress, Int64.MaxValue);
                                        if (success)
                                        {
                                            _dhcpStateMachineState = DhcpStateMachineState.Bound;

                                            /* configure our renewing/rebinding/expiration timers */
                                            if (dhcpOffer.LeaseExpirationTimeInSeconds == UInt32.MaxValue)
                                            {
                                                _leaseExpirationTimeInMachineTicks = Int64.MaxValue;
                                                _leaseRenewalTimeInMachineTicks = Int64.MaxValue;
                                                _leaseRebindingTimeInMachineTicks = Int64.MaxValue;
                                            }
                                            else
                                            {
                                                _leaseExpirationTimeInMachineTicks = currentMachineTicks + (dhcpOffer.LeaseExpirationTimeInSeconds * TimeSpan.TicksPerSecond);
                                                _leaseRenewalTimeInMachineTicks = currentMachineTicks + (dhcpOffer.LeaseRenewalTimeInSeconds * TimeSpan.TicksPerSecond);
                                                _leaseRebindingTimeInMachineTicks = currentMachineTicks + (dhcpOffer.LeaseRebindingTimeInSeconds * TimeSpan.TicksPerSecond);
                                            }
                                        }
                                        else
                                        {
                                            _leaseUpdateTimer.Change((Int32)((_leaseExpirationTimeInMachineTicks - currentMachineTicks) / TimeSpan.TicksPerMillisecond), System.Threading.Timeout.Infinite);
                                        }
                                    }
                                    else
                                    {
                                        _leaseUpdateTimer.Change((Int32)((_leaseRebindingTimeInMachineTicks - currentMachineTicks) / TimeSpan.TicksPerMillisecond), System.Threading.Timeout.Infinite);
                                    }
                                }
                                break;
                            //case DhcpStateMachineState.Rebinding:
                            //    break;
                        }
                    }
                }
            }
        }

        void LeaseUpdateTimerCallback(object state)
        {
            _dhcpStateMachineEvent.Set();
        }

        UInt32 GenerateRandomTransactionID()
        {
            /* NOTE: since our hardware likely does not include an RTC, this random generator will likely generate the same random number(s) after every boot */
            return (UInt32)(_randomGenerator.Next() + _randomGenerator.Next());
        }

        // this function returns a value in the range of -1 and 1, not including -1 or 1.
        double GenerateRandomPlusMinusOne()
        {
            return 1 - (_randomGenerator.NextDouble() * 2);
        }

        //void SendDhcpDecline(UInt32 ipAddress, Int64 timeoutInMachineTicks)
        //{
        //    if (_isDisposed)
        //        return;

        //    // obtain an exclusive handle to the reserved socket
        //    int socketHandle = _ipv4Layer.CreateSocket(IPv4Layer.ProtocolType.Udp, timeoutInMachineTicks, true);
        //    // instantiate the reserved socket
        //    UdpSocket socket = (UdpSocket)_ipv4Layer.GetSocket(socketHandle);

        //    try
        //    {
        //        // bind the reserved socket to the DHCPv4 client port
        //        socket.Bind(0 /* IP_ADDRESS_ANY */, DHCP_CLIENT_PORT);

        //        // generate unique transaction ID
        //        UInt32 transactionID = GenerateRandomTransactionID();

        //        // set our clientIdentifier
        //        byte[] clientIdentifier = new byte[1 + HARDWARE_ADDRESS_SIZE];
        //        clientIdentifier[0] = HARDWARE_TYPE_ETHERNET;
        //        clientIdentifier[1] = (byte)((_physicalAddress >> 40) & 0xFF);
        //        clientIdentifier[2] = (byte)((_physicalAddress >> 32) & 0xFF);
        //        clientIdentifier[3] = (byte)((_physicalAddress >> 24) & 0xFF);
        //        clientIdentifier[4] = (byte)((_physicalAddress >> 16) & 0xFF);
        //        clientIdentifier[5] = (byte)((_physicalAddress >> 8) & 0xFF);
        //        clientIdentifier[6] = (byte)(_physicalAddress & 0xFF);

        //        byte[] requestedIPAddress = new byte[4];
        //        requestedIPAddress[0] = (byte)((ipAddress >> 24) & 0xFF);
        //        requestedIPAddress[1] = (byte)((ipAddress >> 16) & 0xFF);
        //        requestedIPAddress[2] = (byte)((ipAddress >> 8) & 0xFF);
        //        requestedIPAddress[3] = (byte)(ipAddress & 0xFF);

        //        byte[] serverIdentifier = new byte[4];
        //        serverIdentifier[0] = (byte)((_dhcpServerAddress >> 24) & 0xFF);
        //        serverIdentifier[1] = (byte)((_dhcpServerAddress >> 16) & 0xFF);
        //        serverIdentifier[2] = (byte)((_dhcpServerAddress >> 8) & 0xFF);
        //        serverIdentifier[3] = (byte)(_dhcpServerAddress & 0xFF);

        //        DhcpOption[] options = new DhcpOption[3];
        //        options[0] = new DhcpOption(DhcpOptionCode.ClientIdentifier, clientIdentifier);
        //        options[1] = new DhcpOption(DhcpOptionCode.ServerIdentifier, serverIdentifier);
        //        options[2] = new DhcpOption(DhcpOptionCode.RequestedIPAddress, requestedIPAddress);

        //        // send DHCP message
        //        SendDhcpMessage(socket, DhcpMessageType.DHCPDECLINE, 0xFFFFFFFF, transactionID, 0, 0, _physicalAddress, options, timeoutInMachineTicks);
        //    }
        //    finally
        //    {
        //        // close the reserved socket
        //        _ipv4Layer.CloseSocket(socketHandle);
        //    }
        //}

        //void SendDhcpRelease(Int64 timeoutInMachineTicks)
        //{
        //    if (_isDisposed)
        //        return;

        //    if (_ipConfigIPAddress == 0 || _dhcpServerAddress == 0)
        //        return;

        //    // obtain an exclusive handle to the reserved socket
        //    int socketHandle = _ipv4Layer.CreateSocket(IPv4Layer.ProtocolType.Udp, timeoutInMachineTicks, true);
        //    // instantiate the reserved socket
        //    UdpSocket socket = (UdpSocket)_ipv4Layer.GetSocket(socketHandle);

        //    try
        //    {
        //        // bind the reserved socket to the DHCPv4 client port
        //        socket.Bind(0 /* IP_ADDRESS_ANY */, DHCP_CLIENT_PORT);

        //        // generate unique transaction ID
        //        UInt32 transactionID = GenerateRandomTransactionID();

        //        // set our clientIdentifier
        //        byte[] clientIdentifier = new byte[1 + HARDWARE_ADDRESS_SIZE];
        //        clientIdentifier[0] = HARDWARE_TYPE_ETHERNET;
        //        clientIdentifier[1] = (byte)((_physicalAddress >> 40) & 0xFF);
        //        clientIdentifier[2] = (byte)((_physicalAddress >> 32) & 0xFF);
        //        clientIdentifier[3] = (byte)((_physicalAddress >> 24) & 0xFF);
        //        clientIdentifier[4] = (byte)((_physicalAddress >> 16) & 0xFF);
        //        clientIdentifier[5] = (byte)((_physicalAddress >> 8) & 0xFF);
        //        clientIdentifier[6] = (byte)(_physicalAddress & 0xFF);

        //        byte[] serverIdentifier = new byte[4];
        //        serverIdentifier[0] = (byte)((_dhcpServerAddress >> 24) & 0xFF);
        //        serverIdentifier[1] = (byte)((_dhcpServerAddress >> 16) & 0xFF);
        //        serverIdentifier[2] = (byte)((_dhcpServerAddress >> 8) & 0xFF);
        //        serverIdentifier[3] = (byte)(_dhcpServerAddress & 0xFF);

        //        DhcpOption[] options = new DhcpOption[2];
        //        options[0] = new DhcpOption(DhcpOptionCode.ClientIdentifier, clientIdentifier);
        //        options[1] = new DhcpOption(DhcpOptionCode.ServerIdentifier, serverIdentifier);

        //        // send DHCP message
        //        SendDhcpMessage(socket, DhcpMessageType.DHCPRELEASE, _dhcpServerAddress, transactionID, 0, _ipConfigIPAddress, _physicalAddress, options, timeoutInMachineTicks);
        //    }
        //    finally
        //    {
        //        // close the reserved socket
        //        _ipv4Layer.CloseSocket(socketHandle);
        //    }

        //    // clear our DHCP settings and move to init state
        //    _ipConfigIPAddress = 0;
        //    _ipConfigSubnetMask = 0;
        //    _ipConfigGatewayAddress = 0;
        //    _dhcpServerAddress = 0;
        //    _dhcpStateMachineState = DhcpStateMachineState.InitReboot;

        //    /* NOTE: uncomment the following line if you want to automatically retrieve a new address after DHCPRELEASE */
        //    //_dhcpStateMachineEvent.Set();
        //}

        /* this function returns true if an offer has been received */
        DhcpOffer[] SendDhcpDiscoverAndCollectOffers(out UInt16 secondsElapsed, Int64 timeoutInMachineTicks)
        {
            // obtain an exclusive handle to the reserved socket
            int socketHandle = _ipv4Layer.CreateSocket(IPv4Layer.ProtocolType.Udp, timeoutInMachineTicks, true);
            // instantiate the reserved socket
            UdpSocket socket = (UdpSocket)_ipv4Layer.GetSocket(socketHandle);

            System.Collections.ArrayList dhcpOffers;
            try
            {
                // bind the reserved socket to the DHCPv4 client port
                socket.Bind(0 /* IP_ADDRESS_ANY */, DHCP_CLIENT_PORT);

                // generate unique transaction ID
                UInt32 transactionID = GenerateRandomTransactionID();

                // we will retry the DHCP request up to four times.  first delay will be 4 +/-1 seconds; second delay will be 8 +/-1 seconds; third delay will be 16 +/-1 seconds; fourth delay will be 32 +/-1 seconds.
                // if our current timeoutInMachineTicks is longer than 64 seconds (the maximum wait for DHCP transmission) then reduce it to the maximum
                timeoutInMachineTicks = (Int64)System.Math.Max(timeoutInMachineTicks, Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks + (TimeSpan.TicksPerSecond * 64));
                Int64 startTicks = Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks;
                secondsElapsed = 0;
                byte nextRetrySeconds = 4;
                Int64 nextRetryInMachineTicks = (Int64)(Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks + (((double)nextRetrySeconds + GenerateRandomPlusMinusOne()) * TimeSpan.TicksPerSecond));

                // set our clientIdentifier
                byte[] clientIdentifier = new byte[1 + HARDWARE_ADDRESS_SIZE];
                clientIdentifier[0] = HARDWARE_TYPE_ETHERNET;
                clientIdentifier[1] = (byte)((_physicalAddress >> 40) & 0xFF);
                clientIdentifier[2] = (byte)((_physicalAddress >> 32) & 0xFF);
                clientIdentifier[3] = (byte)((_physicalAddress >> 24) & 0xFF);
                clientIdentifier[4] = (byte)((_physicalAddress >> 16) & 0xFF);
                clientIdentifier[5] = (byte)((_physicalAddress >> 8) & 0xFF);
                clientIdentifier[6] = (byte)(_physicalAddress & 0xFF);

                byte[] parameterRequestList;
                if (_isDhcpDnsConfigEnabled)
                {
                    parameterRequestList = new byte[] { (byte)DhcpOptionCode.SubnetMask, (byte)DhcpOptionCode.Router, (byte)DhcpOptionCode.DomainNameServer };
                }
                else
                {
                    parameterRequestList = new byte[] { (byte)DhcpOptionCode.SubnetMask, (byte)DhcpOptionCode.Router };
                }

                byte[] maximumDhcpMessageSize = new byte[2];
                maximumDhcpMessageSize[0] = (byte)((DHCP_MESSAGE_MAXIMUM_SIZE >> 8) & 0xFF);
                maximumDhcpMessageSize[1] = (byte)(DHCP_MESSAGE_MAXIMUM_SIZE & 0xFF);

                dhcpOffers = new System.Collections.ArrayList();
                while (timeoutInMachineTicks > Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks)
                {
                    // assemble options
                    DhcpOption[] options = new DhcpOption[3];
                    options[0] = new DhcpOption(DhcpOptionCode.ClientIdentifier, clientIdentifier);
                    options[1] = new DhcpOption(DhcpOptionCode.ParameterRequestList, parameterRequestList);
                    options[2] = new DhcpOption(DhcpOptionCode.MaximumDhcpMessageSize, maximumDhcpMessageSize);

                    // send DHCP message
                    SendDhcpMessage(socket, DhcpMessageType.DHCPDISCOVER, 0xFFFFFFFF, transactionID, secondsElapsed, 0, _physicalAddress, options, timeoutInMachineTicks);

                    // collect offers
                    DhcpOffer dhcpOffer;
                    bool success = RetrieveDhcpOffer(socket, transactionID, _physicalAddress, out dhcpOffer, nextRetryInMachineTicks);

                    if (success)
                    {
                        dhcpOffers.Add(dhcpOffer);
                        break;
                    }

                    secondsElapsed = (UInt16)((Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks - startTicks) / TimeSpan.TicksPerSecond);
                    nextRetrySeconds *= 2;
                    nextRetryInMachineTicks = (Int64)(Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks + (((double)nextRetrySeconds + GenerateRandomPlusMinusOne()) * TimeSpan.TicksPerSecond));
                }
            }
            finally
            {
                // close the reserved socket
                _ipv4Layer.CloseSocket(socketHandle);
            }

            return (DhcpOffer[])dhcpOffers.ToArray(typeof(DhcpOffer));
        }

        bool SendDhcpRequestAndWaitForAck(DhcpOffer dhcpOffer, UInt16 secondsElapsed, UInt32 dhcpServerIPAddress, UInt32 clientIPAddress, Int64 timeoutInMachineTicks)
        {
            bool success = false;

            // obtain an exclusive handle to the reserved socket
            int socketHandle = _ipv4Layer.CreateSocket(IPv4Layer.ProtocolType.Udp, timeoutInMachineTicks, true);
            // instantiate the reserved socket
            UdpSocket socket = (UdpSocket)_ipv4Layer.GetSocket(socketHandle);

            try
            {
                // bind the reserved socket to the DHCPv4 client port
                socket.Bind(0 /* IP_ADDRESS_ANY */, DHCP_CLIENT_PORT);

                // we will retry the DHCP request up to four times.  first delay will be 4 +/-1 seconds; second delay will be 8 +/-1 seconds; third delay will be 16 +/-1 seconds; fourth delay will be 32 +/-1 seconds.
                // if our current timeoutInMachineTicks is longer than 64 seconds (the maximum wait for DHCP transmission) then reduce it to the maximum
                timeoutInMachineTicks = (Int64)System.Math.Max(timeoutInMachineTicks, Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks + (TimeSpan.TicksPerSecond * 64));
                byte nextRetrySeconds = 4;
                Int64 nextRetryInMachineTicks = (Int64)(Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks + (((double)nextRetrySeconds + GenerateRandomPlusMinusOne()) * TimeSpan.TicksPerSecond));

                // set our clientIdentifier
                byte[] clientIdentifier = new byte[1 + HARDWARE_ADDRESS_SIZE];
                clientIdentifier[0] = HARDWARE_TYPE_ETHERNET;
                clientIdentifier[1] = (byte)((_physicalAddress >> 40) & 0xFF);
                clientIdentifier[2] = (byte)((_physicalAddress >> 32) & 0xFF);
                clientIdentifier[3] = (byte)((_physicalAddress >> 24) & 0xFF);
                clientIdentifier[4] = (byte)((_physicalAddress >> 16) & 0xFF);
                clientIdentifier[5] = (byte)((_physicalAddress >> 8) & 0xFF);
                clientIdentifier[6] = (byte)(_physicalAddress & 0xFF);

                byte[] parameterRequestList;
                if (_isDhcpDnsConfigEnabled)
                {
                    parameterRequestList = new byte[] { (byte)DhcpOptionCode.SubnetMask, (byte)DhcpOptionCode.Router, (byte)DhcpOptionCode.DomainNameServer };
                }
                else
                {
                    parameterRequestList = new byte[] { (byte)DhcpOptionCode.SubnetMask, (byte)DhcpOptionCode.Router };
                }

                byte[] maximumDhcpMessageSize = new byte[2];
                maximumDhcpMessageSize[0] = (byte)((DHCP_MESSAGE_MAXIMUM_SIZE >> 8) & 0xFF);
                maximumDhcpMessageSize[1] = (byte)(DHCP_MESSAGE_MAXIMUM_SIZE & 0xFF);

                byte[] requestedIPAddress = new byte[4];
                requestedIPAddress[0] = (byte)((dhcpOffer.IPAddress >> 24) & 0xFF);
                requestedIPAddress[1] = (byte)((dhcpOffer.IPAddress >> 16) & 0xFF);
                requestedIPAddress[2] = (byte)((dhcpOffer.IPAddress >> 8) & 0xFF);
                requestedIPAddress[3] = (byte)(dhcpOffer.IPAddress & 0xFF);

                byte[] serverIdentifier = new byte[4];
                serverIdentifier[0] = (byte)((dhcpOffer.ServerIdentifier >> 24) & 0xFF);
                serverIdentifier[1] = (byte)((dhcpOffer.ServerIdentifier >> 16) & 0xFF);
                serverIdentifier[2] = (byte)((dhcpOffer.ServerIdentifier >> 8) & 0xFF);
                serverIdentifier[3] = (byte)(dhcpOffer.ServerIdentifier & 0xFF);

                while (timeoutInMachineTicks > Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks)
                {
                    // assemble options
                    DhcpOption[] options = new DhcpOption[5];
                    options[0] = new DhcpOption(DhcpOptionCode.ClientIdentifier, clientIdentifier);
                    options[1] = new DhcpOption(DhcpOptionCode.ParameterRequestList, parameterRequestList);
                    options[2] = new DhcpOption(DhcpOptionCode.MaximumDhcpMessageSize, maximumDhcpMessageSize);
                    if (dhcpOffer.IPAddress != 0)
                        options[3] = new DhcpOption(DhcpOptionCode.RequestedIPAddress, requestedIPAddress);
                    if (dhcpOffer.ServerIdentifier != 0)
                        options[4] = new DhcpOption(DhcpOptionCode.ServerIdentifier, serverIdentifier);

                    // send DHCP message
                    SendDhcpMessage(socket, DhcpMessageType.DHCPREQUEST, dhcpServerIPAddress, dhcpOffer.TransactionID, secondsElapsed, clientIPAddress, _physicalAddress, options, timeoutInMachineTicks);

                    // wait for ACK/NAK
                    bool responseIsAck;
                    bool ackNakReceived = RetrieveAckNak(socket, dhcpOffer.TransactionID, _physicalAddress, ref dhcpOffer, out responseIsAck, nextRetryInMachineTicks);

                    if (ackNakReceived)
                    {
                        success = responseIsAck;
                        break;
                    }

                    secondsElapsed += nextRetrySeconds;
                    nextRetrySeconds *= 2;
                    nextRetryInMachineTicks = (Int64)(Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks + (((double)nextRetrySeconds + GenerateRandomPlusMinusOne()) * TimeSpan.TicksPerSecond));
                }
            }
            finally
            {
                // close the reserved socket
                _ipv4Layer.CloseSocket(socketHandle);
            }

            return success;
        }

        //bool SendDhcpInformAndWaitForAck(ref DhcpOffer dhcpOffer, UInt16 secondsElapsed, Int64 timeoutInMachineTicks)
        //{
        //    bool success = false;

        //    // obtain an exclusive handle to the reserved socket
        //    int socketHandle = _ipv4Layer.CreateSocket(IPv4Layer.ProtocolType.Udp, timeoutInMachineTicks, true);
        //    // instantiate the reserved socket
        //    UdpSocket socket = (UdpSocket)_ipv4Layer.GetSocket(socketHandle);

        //    try
        //    {
        //        // bind the reserved socket to the DHCPv4 client port
        //        socket.Bind(0 /* IP_ADDRESS_ANY */, DHCP_CLIENT_PORT);

        //        // we will retry the DHCP request up to four times.  first delay will be 4 +/-1 seconds; second delay will be 8 +/-1 seconds; third delay will be 16 +/-1 seconds; fourth delay will be 32 +/-1 seconds.
        //        // if our current timeoutInMachineTicks is longer than 64 seconds (the maximum wait for DHCP transmission) then reduce it to the maximum
        //        timeoutInMachineTicks = (Int64)System.Math.Max(timeoutInMachineTicks, Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks + (TimeSpan.TicksPerSecond * 64));
        //        byte nextRetrySeconds = 4;
        //        Int64 nextRetryInMachineTicks = (Int64)(Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks + (((double)nextRetrySeconds + GenerateRandomPlusMinusOne()) * TimeSpan.TicksPerSecond));

        //        // set our clientIdentifier
        //        byte[] clientIdentifier = new byte[1 + HARDWARE_ADDRESS_SIZE];
        //        clientIdentifier[0] = HARDWARE_TYPE_ETHERNET;
        //        clientIdentifier[1] = (byte)((_physicalAddress >> 40) & 0xFF);
        //        clientIdentifier[2] = (byte)((_physicalAddress >> 32) & 0xFF);
        //        clientIdentifier[3] = (byte)((_physicalAddress >> 24) & 0xFF);
        //        clientIdentifier[4] = (byte)((_physicalAddress >> 16) & 0xFF);
        //        clientIdentifier[5] = (byte)((_physicalAddress >> 8) & 0xFF);
        //        clientIdentifier[6] = (byte)(_physicalAddress & 0xFF);

        //        while (timeoutInMachineTicks > Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks)
        //        {
        //            // assemble options
        //            DhcpOption[] options = new DhcpOption[1];
        //            options[0] = new DhcpOption(DhcpOptionCode.ClientIdentifier, clientIdentifier);

        //            // send DHCP message
        //            SendDhcpMessage(socket, DhcpMessageType.DHCPINFORM, 0xFFFFFFFF, dhcpOffer.TransactionID, secondsElapsed, _ipConfigIPAddress, _physicalAddress, options, timeoutInMachineTicks);

        //            // wait for ACK/NAK
        //            bool responseIsAck;
        //            bool ackNakReceived = RetrieveAckNak(socket, dhcpOffer.TransactionID, _physicalAddress, ref dhcpOffer, out responseIsAck, nextRetryInMachineTicks);

        //            if (ackNakReceived)
        //            {
        //                success = responseIsAck;
        //                break;
        //            }

        //            secondsElapsed += nextRetrySeconds;
        //            nextRetrySeconds *= 2;
        //            nextRetryInMachineTicks = (Int64)(Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks + (((double)nextRetrySeconds + GenerateRandomPlusMinusOne()) * TimeSpan.TicksPerSecond));
        //        }
        //    }
        //    finally
        //    {
        //        // close the reserved socket
        //        _ipv4Layer.CloseSocket(socketHandle);
        //    }

        //    return success;
        //}

        bool RetrieveDhcpOffer(UdpSocket socket, UInt32 transactionID, UInt64 clientHardwareAddress, out DhcpOffer dhcpOffer, Int64 timeoutInMachineTicks)
        {
            UInt32 assignedIPAddress;
            DhcpOption[] options;
            bool success = RetrieveDhcpMessage(socket, new DhcpMessageType[] { DhcpMessageType.DHCPOFFER }, transactionID, clientHardwareAddress, out assignedIPAddress, out options, timeoutInMachineTicks);

            if (success == false)
            {
                dhcpOffer = new DhcpOffer();
                return false; /* timeout */
            }

            dhcpOffer = new DhcpOffer(transactionID, 0, assignedIPAddress, 0, 0, null);
            for (int iOption = 0; iOption < options.Length; iOption++)
            {
                switch (options[iOption].Code)
                {
                    case DhcpOptionCode.SubnetMask:
                        {
                            if (options[iOption].Value.Length >= 4)
                            {
                                dhcpOffer.SubnetMask = (
                                    (UInt32)(options[iOption].Value[0] << 24) + 
                                    (UInt32)(options[iOption].Value[1] << 16) + 
                                    (UInt32)(options[iOption].Value[2] << 8) + 
                                    (UInt32)(options[iOption].Value[3])
                                    );
                            }
                        }
                        break;
                    case DhcpOptionCode.Router:
                        {
                            /* NOTE: this option may include multiple router addresses; we select the first entry as our default gateway */
                            if (options[iOption].Value.Length >= 4)
                            {
                                dhcpOffer.GatewayAddress = (
                                    (UInt32)(options[iOption].Value[0] << 24) +
                                    (UInt32)(options[iOption].Value[1] << 16) +
                                    (UInt32)(options[iOption].Value[2] << 8) +
                                    (UInt32)(options[iOption].Value[3])
                                    );
                            }
                        }
                        break;
                    case DhcpOptionCode.DomainNameServer:
                        {
                            /* NOTE: this option may include multiple DNS servers; we will only select up to the first two servers as our default DNS servers */
                            System.Collections.ArrayList dnsServerArrayList = new System.Collections.ArrayList();
                            for (int iDnsServer = 0; iDnsServer < options[iOption].Value.Length; iDnsServer += 4)
                            {
                                if (options[iOption].Value.Length >= iDnsServer + 4)
                                {
                                    UInt32 dnsServerAddress = (
                                        (UInt32)(options[iOption].Value[iDnsServer + 0] << 24) +
                                        (UInt32)(options[iOption].Value[iDnsServer + 1] << 16) +
                                        (UInt32)(options[iOption].Value[iDnsServer + 2] << 8) +
                                        (UInt32)(options[iOption].Value[iDnsServer + 3])
                                        );
                                    dnsServerArrayList.Add(dnsServerAddress);
                                }
                            }
                            dhcpOffer.DnsAddresses = (UInt32[])dnsServerArrayList.ToArray(typeof(UInt32));
                        }
                        break;
                    case DhcpOptionCode.IPAddressLeaseTime:
                        {
                            if (options[iOption].Value.Length >= 4)
                            {
                                dhcpOffer.LeaseExpirationTimeInSeconds = (
                                    (UInt32)(options[iOption].Value[0] << 24) +
                                    (UInt32)(options[iOption].Value[1] << 16) +
                                    (UInt32)(options[iOption].Value[2] << 8) +
                                    (UInt32)(options[iOption].Value[3])
                                    );
                            }
                        }
                        break;
                    case DhcpOptionCode.ServerIdentifier:
                        {
                            if (options[iOption].Value.Length >= 4)
                            {
                                dhcpOffer.ServerIdentifier = (
                                    (UInt32)(options[iOption].Value[0] << 24) +
                                    (UInt32)(options[iOption].Value[1] << 16) +
                                    (UInt32)(options[iOption].Value[2] << 8) +
                                    (UInt32)(options[iOption].Value[3])
                                    );
                            }
                        }
                        break;
                    case DhcpOptionCode.RenewalTimeValue:
                        {
                            if (options[iOption].Value.Length >= 4)
                            {
                                dhcpOffer.LeaseRenewalTimeInSeconds = (
                                    (UInt32)(options[iOption].Value[0] << 24) +
                                    (UInt32)(options[iOption].Value[1] << 16) +
                                    (UInt32)(options[iOption].Value[2] << 8) +
                                    (UInt32)(options[iOption].Value[3])
                                    );
                            }
                        }
                        break;
                    case DhcpOptionCode.RebindingTimeValue:
                        {
                            if (options[iOption].Value.Length >= 4)
                            {
                                dhcpOffer.LeaseRebindingTimeInSeconds = (
                                    (UInt32)(options[iOption].Value[0] << 24) +
                                    (UInt32)(options[iOption].Value[1] << 16) +
                                    (UInt32)(options[iOption].Value[2] << 8) +
                                    (UInt32)(options[iOption].Value[3])
                                    );
                            }
                        }
                        break;
                    case DhcpOptionCode.ClientIdentifier:
                        /* NOTE: we ignore this since our hardware address matches, although if we ever need to verify it then we can do so here */
                        break;
                }
            }

            return true; /* success */
        }

        bool RetrieveAckNak(UdpSocket socket, UInt32 transactionID, UInt64 clientHardwareAddress, ref DhcpOffer dhcpOffer, out bool responseIsAck, Int64 timeoutInMachineTicks)
        {
            // set default ack/nak response
            responseIsAck = false;

            UInt32 assignedIPAddress;
            DhcpOption[] options;
            bool success = RetrieveDhcpMessage(socket, new DhcpMessageType[] { DhcpMessageType.DHCPACK, DhcpMessageType.DHCPNAK }, transactionID, clientHardwareAddress, out assignedIPAddress, out options, timeoutInMachineTicks);
            if (success == false)
            {
                return false; /* timeout */
            }

            // analyze return value and update offer values if necessary
            for (int iOption = 0; iOption < options.Length; iOption++)
            {
                switch (options[iOption].Code)
                {
                    case DhcpOptionCode.DhcpMessageType:
                        {
                            if (options[iOption].Value.Length >= 1)
                            {
                                responseIsAck = ((DhcpMessageType)options[iOption].Value[0] == DhcpMessageType.DHCPACK);
                            }
                        }
                        break;
                    case DhcpOptionCode.SubnetMask:
                        {
                            if (options[iOption].Value.Length >= 4)
                            {
                                dhcpOffer.SubnetMask = (
                                    (UInt32)(options[iOption].Value[0] << 24) +
                                    (UInt32)(options[iOption].Value[1] << 16) +
                                    (UInt32)(options[iOption].Value[2] << 8) +
                                    (UInt32)(options[iOption].Value[3])
                                    );
                            }
                        }
                        break;
                    case DhcpOptionCode.Router:
                        {
                            /* NOTE: this option may include multiple router addresses; we select the first entry as our default gateway */
                            if (options[iOption].Value.Length >= 4)
                            {
                                dhcpOffer.GatewayAddress = (
                                    (UInt32)(options[iOption].Value[0] << 24) +
                                    (UInt32)(options[iOption].Value[1] << 16) +
                                    (UInt32)(options[iOption].Value[2] << 8) +
                                    (UInt32)(options[iOption].Value[3])
                                    );
                            }
                        }
                        break;
                    case DhcpOptionCode.DomainNameServer:
                        {
                            /* NOTE: this option may include multiple DNS servers; we will only select up to the first two servers as our default DNS servers */
                            System.Collections.ArrayList dnsServerArrayList = new System.Collections.ArrayList();
                            for (int iDnsServer = 0; iDnsServer < options[iOption].Value.Length; iDnsServer += 4)
                            {
                                if (options[iOption].Value.Length >= iDnsServer + 4)
                                {
                                    UInt32 dnsServerAddress = (
                                        (UInt32)(options[iOption].Value[iDnsServer + 0] << 24) +
                                        (UInt32)(options[iOption].Value[iDnsServer + 1] << 16) +
                                        (UInt32)(options[iOption].Value[iDnsServer + 2] << 8) +
                                        (UInt32)(options[iOption].Value[iDnsServer + 3])
                                        );
                                    dnsServerArrayList.Add(dnsServerAddress);
                                }
                            }
                            dhcpOffer.DnsAddresses = (UInt32[])dnsServerArrayList.ToArray(typeof(UInt32));
                        }
                        break;
                    case DhcpOptionCode.IPAddressLeaseTime:
                        {
                            if (options[iOption].Value.Length >= 4)
                            {
                                dhcpOffer.LeaseExpirationTimeInSeconds = (
                                    (UInt32)(options[iOption].Value[0] << 24) +
                                    (UInt32)(options[iOption].Value[1] << 16) +
                                    (UInt32)(options[iOption].Value[2] << 8) +
                                    (UInt32)(options[iOption].Value[3])
                                    );
                            }
                        }
                        break;
                    case DhcpOptionCode.ServerIdentifier:
                        {
                            if (options[iOption].Value.Length >= 4)
                            {
                                dhcpOffer.ServerIdentifier = (
                                    (UInt32)(options[iOption].Value[0] << 24) +
                                    (UInt32)(options[iOption].Value[1] << 16) +
                                    (UInt32)(options[iOption].Value[2] << 8) +
                                    (UInt32)(options[iOption].Value[3])
                                    );
                            }
                        }
                        break;
                    case DhcpOptionCode.RenewalTimeValue:
                        {
                            if (options[iOption].Value.Length >= 4)
                            {
                                dhcpOffer.LeaseRenewalTimeInSeconds = (
                                    (UInt32)(options[iOption].Value[0] << 24) +
                                    (UInt32)(options[iOption].Value[1] << 16) +
                                    (UInt32)(options[iOption].Value[2] << 8) +
                                    (UInt32)(options[iOption].Value[3])
                                    );
                            }
                        }
                        break;
                    case DhcpOptionCode.RebindingTimeValue:
                        {
                            if (options[iOption].Value.Length >= 4)
                            {
                                dhcpOffer.LeaseRebindingTimeInSeconds = (
                                    (UInt32)(options[iOption].Value[0] << 24) +
                                    (UInt32)(options[iOption].Value[1] << 16) +
                                    (UInt32)(options[iOption].Value[2] << 8) +
                                    (UInt32)(options[iOption].Value[3])
                                    );
                            }
                        }
                        break;
                    case DhcpOptionCode.ClientIdentifier:
                        /* NOTE: we ignore this since our hardware address matches, although if we ever need to verify it then we can do so here */
                        break;
                }
            }

            return true; /* success */
        }

        bool RetrieveDhcpMessage(UdpSocket socket, DhcpMessageType[] messageTypes, UInt32 transactionID, UInt64 clientHardwareAddress, out UInt32 assignedIPAddress, out DhcpOption[] options, Int64 timeoutInMachineTicks)
        {
            byte[] dhcpFrameBuffer = new byte[DHCP_FRAME_BUFFER_LENGTH];
            while (timeoutInMachineTicks > Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks)
            {
                Int32 bytesReceived = socket.Receive(dhcpFrameBuffer, 0, dhcpFrameBuffer.Length, 0, timeoutInMachineTicks);

                if (bytesReceived == 0) // timeout
                    break;

                /* parse our DHCP frame */
                // validate the operation
                if ((BootpOperation)dhcpFrameBuffer[0] != BootpOperation.BOOTREPLY)
                    continue; /* filter out this BOOTP/DHCP frame */
                /* verify magic cookie {99, 130, 83, 99} is the first 4-byte entry, as per RFC 1497 */
                if ((dhcpFrameBuffer[236] != 99) ||
                    (dhcpFrameBuffer[237] != 130) ||
                    (dhcpFrameBuffer[238] != 83) ||
                    (dhcpFrameBuffer[239] != 99))
                    continue; /* filter out this BOOTP non-DHCP frame */
                // verify that the transaction ID matches
                UInt32 verifyTransactionID = (
                    (UInt32)(dhcpFrameBuffer[4] << 24) +
                    (UInt32)(dhcpFrameBuffer[5] << 16) +
                    (UInt32)(dhcpFrameBuffer[6] << 8) +
                    (UInt32)(dhcpFrameBuffer[7])
                    );
                if (transactionID != verifyTransactionID)
                    continue; /* filter out this DHCP frame */
                // verify the the physical hardware type matches
                if (dhcpFrameBuffer[1] != (byte)HARDWARE_TYPE_ETHERNET)
                    continue; /* filter out this DHCP frame */
                if (dhcpFrameBuffer[2] != (byte)HARDWARE_ADDRESS_SIZE)
                    continue; /* filter out this DHCP frame */
                // verify that the physical address matches
                UInt64 verifyClientHardwareAddress = (
                    ((UInt64)(dhcpFrameBuffer[28]) << 40) +
                    ((UInt64)(dhcpFrameBuffer[29]) << 32) +
                    ((UInt64)(dhcpFrameBuffer[30]) << 24) +
                    ((UInt64)(dhcpFrameBuffer[31]) << 16) +
                    ((UInt64)(dhcpFrameBuffer[32]) << 8) +
                    (UInt64)(dhcpFrameBuffer[33])
                    );
                if (clientHardwareAddress != verifyClientHardwareAddress)
                    continue; /* filter out this DHCP frame */

                // retrieve allocated ip address
                /* yiaddr (ip address, populated by server */
                assignedIPAddress = (
                    (UInt32)(dhcpFrameBuffer[16] << 24) +
                    (UInt32)(dhcpFrameBuffer[17] << 16) +
                    (UInt32)(dhcpFrameBuffer[18] << 8) +
                    (UInt32)(dhcpFrameBuffer[19])
                    );
                // retrieve options
                System.Collections.ArrayList optionsArrayList = new System.Collections.ArrayList();
                bool optionOverloadReceived = false;
                DhcpOptionsBlockRange[] optionsBlocks = new DhcpOptionsBlockRange[]
                {
                    new DhcpOptionsBlockRange(240, DHCP_FRAME_BUFFER_LENGTH - 240 - 1),
                };
                int optionsBlocksIndex = 0;

                DhcpMessageType verifyMessageType = 0;
                while (optionsBlocksIndex < optionsBlocks.Length)
                {
                    Int32 index = optionsBlocks[optionsBlocksIndex].BeginOffset;
                    bool endOpcodeReceived = false;
                    while (!endOpcodeReceived && index <= optionsBlocks[optionsBlocksIndex].EndOffset)
                    {
                        DhcpOptionCode optionCode = (DhcpOptionCode)dhcpFrameBuffer[index];
                        switch ((DhcpOptionCode)optionCode)
                        {
                            case DhcpOptionCode.Pad:
                                {
                                    index++;
                                }
                                break;
                            case DhcpOptionCode.End:
                                {
                                    index++;
                                    endOpcodeReceived = true;
                                }
                                break;
                            case DhcpOptionCode.OptionOverload:
                                {
                                    if (optionsBlocksIndex == 0)
                                    {
                                        index++;
                                        if (dhcpFrameBuffer[index] != 1)
                                            break;
                                        index++;
                                        byte value = dhcpFrameBuffer[index];
                                        index++;
                                        int numBlocks = 1 + (((value & 0x01) == 0x01) ? 1 : 0) + (((value & 0x02) == 0x02) ? 1 : 0);
                                        optionsBlocks = new DhcpOptionsBlockRange[numBlocks];
                                        int iOptionBlock = 0;
                                        optionsBlocks[iOptionBlock++] = new DhcpOptionsBlockRange(240, DHCP_FRAME_BUFFER_LENGTH - 240 - 1);
                                        if ((value & 0x01) == 0x01)
                                        {
                                            /* use file field for extended options */
                                            optionsBlocks[iOptionBlock++] = new DhcpOptionsBlockRange(108, 235);
                                        }
                                        if ((value & 0x02) == 0x02)
                                        {
                                            /* use sname field for extended options */
                                            optionsBlocks[iOptionBlock++] = new DhcpOptionsBlockRange(44, 107);
                                        }
                                    }
                                }
                                break;
                            default:
                                {
                                    index++;
                                    byte[] value = new byte[Math.Min(dhcpFrameBuffer[index], DHCP_FRAME_BUFFER_LENGTH - index)];
                                    index++;
                                    Array.Copy(dhcpFrameBuffer, index, value, 0, value.Length);
                                    index += value.Length;

                                    // if the option already exists, append to it
                                    bool foundOption = false;
                                    for (int iExistingOption = 0; iExistingOption < optionsArrayList.Count; iExistingOption++)
                                    {
                                        if (((DhcpOption)optionsArrayList[iExistingOption]).Code == optionCode)
                                        {
                                            byte[] newValue = new byte[((DhcpOption)optionsArrayList[iExistingOption]).Value.Length + value.Length];
                                            Array.Copy(((DhcpOption)optionsArrayList[iExistingOption]).Value, 0, newValue, 0, ((DhcpOption)optionsArrayList[iExistingOption]).Value.Length);
                                            Array.Copy(value, 0, newValue, ((DhcpOption)optionsArrayList[iExistingOption]).Value.Length, value.Length);
                                            optionsArrayList.RemoveAt(iExistingOption);
                                            optionsArrayList.Add(new DhcpOption(optionCode, newValue));

                                            foundOption = true;
                                            break;
                                        }
                                    }
                                    if (!foundOption)
                                    {
                                        optionsArrayList.Add(new DhcpOption(optionCode, value));
                                    }

                                    if (optionCode == DhcpOptionCode.DhcpMessageType)
                                    {
                                        verifyMessageType = (DhcpMessageType)value[0];
                                    }
                                }
                                break;
                        }
                    }

                    optionsBlocksIndex++;
                }
                options = (DhcpOption[])optionsArrayList.ToArray(typeof(DhcpOption));

                // verify that the DHCP message type matches
                bool messageTypeMatches = false;
                for (int iMessageType = 0; iMessageType < messageTypes.Length; iMessageType++)
                {
                    if (messageTypes[iMessageType] == verifyMessageType)
                    {
                        messageTypeMatches = true;
                        break;
                    }
                }

                if (messageTypeMatches)
                    return true; /* message matches the messageTypes filter, with a valid frame; return all data to the caller  */
            }

            // if we did not receive a message before timeout, return false.
            // set default return values
            assignedIPAddress = 0;
            options = null;
            return false;
        }

        void SendDhcpMessage(UdpSocket socket, DhcpMessageType messageType, UInt32 dhcpServerIPAddress, UInt32 transactionID, UInt16 secondsElapsed, UInt32 clientIPAddress, UInt64 clientHardwareAddress, DhcpOption[] options, Int64 timeoutInMachineTicks)
        {
            if (_isDisposed) return;

            byte[] dhcpFrameBuffer = new byte[DHCP_FRAME_BUFFER_LENGTH];

            // configure DHCP frame
            /* op (bootp operation) */
            dhcpFrameBuffer[0] = (byte)BootpOperation.BOOTREQUEST;
            /* htype (hardware type) */
            dhcpFrameBuffer[1] = (byte)HARDWARE_TYPE_ETHERNET;
            /* hlen (hardware address length) */
            dhcpFrameBuffer[2] = (byte)HARDWARE_ADDRESS_SIZE;
            /* hops (bootp relay hops; we should always set this to zero) */
            dhcpFrameBuffer[3] = 0;
            /* xid (transaction id; this should be a randomly-generated number) */
            dhcpFrameBuffer[4] = (byte)((transactionID >> 24) & 0xFF);
            dhcpFrameBuffer[5] = (byte)((transactionID >> 16) & 0xFF);
            dhcpFrameBuffer[6] = (byte)((transactionID >> 8) & 0xFF);
            dhcpFrameBuffer[7] = (byte)(transactionID & 0xFF);
            /* secs (seconds elasped since start of DHCP config acquisition process) */
            dhcpFrameBuffer[8] = (byte)((secondsElapsed >> 8) & 0xFF);
            dhcpFrameBuffer[9] = (byte)(secondsElapsed & 0xFF);
            /* flags (most significant bit is broadcast flags; all others are zeroes) */
            /* some DHCP servers do not process the broadcast flag properly, so we allow all broadcast and unicast packets with our hardwareAddress to pass through to the UDP layer instead */
            /* see https://support.microsoft.com/en-us/kb/928233 for more details */
            dhcpFrameBuffer[10] = 0x00; // 0x80; 
            dhcpFrameBuffer[11] = 0x00;
            /* ciaddr (client ip address; only filled in if client can respond to ARP requests and is in BOUND, RENEW or REBINDING state) */
            dhcpFrameBuffer[12] = (byte)((clientIPAddress >> 24) & 0xFF);
            dhcpFrameBuffer[13] = (byte)((clientIPAddress >> 16) & 0xFF);
            dhcpFrameBuffer[14] = (byte)((clientIPAddress >> 8) & 0xFF);
            dhcpFrameBuffer[15] = (byte)(clientIPAddress & 0xFF);
            /* yiaddr (ip address, populated by server; this should always be zero in client requests */
            dhcpFrameBuffer[16] = 0;
            dhcpFrameBuffer[17] = 0;
            dhcpFrameBuffer[18] = 0;
            dhcpFrameBuffer[19] = 0;
            /* siaddr (ip address of next address to use in bootp boot process, populated by server; this should always be zero in client requests */
            dhcpFrameBuffer[20] = 0;
            dhcpFrameBuffer[21] = 0;
            dhcpFrameBuffer[22] = 0;
            dhcpFrameBuffer[23] = 0;
            /* giaddr (ip address of relay agent, populated by relay agents; this should always be zero in client requests */
            dhcpFrameBuffer[24] = 0;
            dhcpFrameBuffer[25] = 0;
            dhcpFrameBuffer[26] = 0;
            dhcpFrameBuffer[27] = 0;
            /* chaddr (client hardware address; we fill in the first 6 bytes with our MAC address) */
            dhcpFrameBuffer[28] = (byte)((clientHardwareAddress >> 40) & 0xFF);
            dhcpFrameBuffer[29] = (byte)((clientHardwareAddress >> 32) & 0xFF);
            dhcpFrameBuffer[30] = (byte)((clientHardwareAddress >> 24) & 0xFF);
            dhcpFrameBuffer[31] = (byte)((clientHardwareAddress >> 16) & 0xFF);
            dhcpFrameBuffer[32] = (byte)((clientHardwareAddress >> 8) & 0xFF);
            dhcpFrameBuffer[33] = (byte)(clientHardwareAddress & 0xFF);
            Array.Clear(dhcpFrameBuffer, 34, 10);
            /* sname (null-terminated server hostname, populated by server; always set to zero in client requests */
            Array.Clear(dhcpFrameBuffer, 44, 64);
            /* file (null-terminated boot file name, populaetd by server; always set to zero in client requests */
            Array.Clear(dhcpFrameBuffer, 108, 128);
            /* options; NOTE: we do support overflowing options into the sname and file fields in this implementation of DHCP. */
            /* magic cookie {99, 130, 83, 99} is the first 4-byte entry, as per RFC 1497 */
            dhcpFrameBuffer[236] = 99;
            dhcpFrameBuffer[237] = 130;
            dhcpFrameBuffer[238] = 83;
            dhcpFrameBuffer[239] = 99;
            /* now we fill in the options (starting with the DhcpMessageType, then all of the passed-in options, and then the END option */
            dhcpFrameBuffer[240] = (byte)DhcpOptionCode.DhcpMessageType;
            dhcpFrameBuffer[241] = 1; /* Length */
            dhcpFrameBuffer[242] = (byte)messageType;
            int currentOptionPos = 243;
            if (options != null)
            {
                for (int iOption = 0; iOption < options.Length; iOption++)
                {
                    /* do not include missing/empty options (or pad) */
                    if (options[iOption].Code != 0)
                    {
                        // if this option will not fit in the options section, stop processing options.
                        if (currentOptionPos + options.Length + 2 /* size of code + length bytes */ + 1 /* size of END option */ > DHCP_FRAME_BUFFER_LENGTH)
                            break;

                        dhcpFrameBuffer[currentOptionPos++] = (byte)options[iOption].Code;
                        dhcpFrameBuffer[currentOptionPos++] = (byte)options[iOption].Value.Length;
                        Array.Copy(options[iOption].Value, 0, dhcpFrameBuffer, currentOptionPos, options[iOption].Value.Length);
                        currentOptionPos += options[iOption].Value.Length;
                    }
                }
            }
            /* finish with "END" option */
            dhcpFrameBuffer[currentOptionPos++] = (byte)DhcpOptionCode.End;
            Array.Clear(dhcpFrameBuffer, currentOptionPos, DHCP_FRAME_BUFFER_LENGTH - currentOptionPos);

            // expand frame to word boundary, just in case DHCP servers have troubles processing non-aligned frames.
            Int32 lengthOfFrame = Math.Min(currentOptionPos + (currentOptionPos % 4 != 0 ? (4 - (currentOptionPos % 4)) : 0), DHCP_FRAME_BUFFER_LENGTH);

            socket.SendTo(dhcpFrameBuffer, 0, lengthOfFrame, 0, timeoutInMachineTicks, dhcpServerIPAddress, DHCP_SERVER_PORT);
        }
        
    }
}