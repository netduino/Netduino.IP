using System;
using System.Threading;

namespace Netduino.IP
{
    internal class ArpResolver : IDisposable 
    {
        // fixed buffer for ARP request/reply frames
        const int ARP_FRAME_BUFFER_LENGTH = 28;
        byte[] _arpFrameBuffer = new byte[ARP_FRAME_BUFFER_LENGTH];
        object _arpFrameBufferLock = new object();

        UInt32 _currentArpRequestProtocolAddress = 0x00000000;
        UInt64 _currentArpRequestPhysicalAddress = 0x000000000000;
        object _simultaneousArpRequestLock = new object(); // this lock is used to make sure we only have one simultaneous outgoing ARP request at a time; we could modify this to allow multiple parallel requests in the future
        AutoResetEvent _currentArpRequestAnsweredEvent = new AutoResetEvent(false);

        byte[][] _bufferArray = new byte[1][];
        int[] _indexArray = new int[1];
        int[] _countArray = new int[1];

        const UInt16 HARDWARE_TYPE_ETHERNET = 0x0001;
        const UInt16 DATA_TYPE_ARP = 0x0806;
        const UInt16 PROTOCOL_TYPE_IPV4 = 0x0800;
        const byte HARDWARE_ADDRESS_SIZE = 6;
        const byte PROTOCOL_ADDRESS_SIZE = 4;

        const UInt32 MAX_ARP_TRANSLATE_ATTEMPTS = 3; /* the maximum number of times we will attempt to translate an IP Address into a Physical Address, per packet */

        const UInt16 DEFAULT_ARP_CACHE_TIMEOUT_IN_SECONDS = 1200; /* the default timeout for ARP cache items */

        enum ArpOperation : ushort 
        {
            ARP_OPERATION_REQUEST = 0x01,
            ARP_OPERATION_REPLY = 0x02,
        }
        const UInt64 ETHERNET_BROADCAST_ADDRESS = 0x00FFFFFFFFFFFF;

        UInt32 _ipv4ProtocolAddress = 0x00000000;

        // NOTE: in a future version of NETMF (with nullable type support), we would ideally change this to a struct--and use nullable struct types instead of classes.
        class ArpCacheEntry
        {
            public ArpCacheEntry(UInt64 physicalAddress, Int64 timeoutTicks, Int64 lastUsedTicks = 0)
            {
                this.PhysicalAddress = physicalAddress;
                this.TimeoutTicks = timeoutTicks;
                this.LastUsedTicks = lastUsedTicks;
            }

            public UInt64 PhysicalAddress;
            public Int64 TimeoutTicks;
            /* LastUsedTicks is set whenever the entry is used to send a packet; 
             * it is set to zero (unused) when this entry is a reply of an incoming ARP request; 
             * when the ARP Cache is full, we clean out the oldest ARP entry based on the value of LastUsedTicks */
            public Int64 LastUsedTicks;
        }
        const byte ARP_CACHE_MAXIMUM_ENTRIES = 254; /* we will store a maximum of 254 entires in our ARP cache */ /* TODO: we should consider making this a configurable option...and default it to 4-8 */
        System.Collections.Hashtable _arpCache;
        object _arpCacheLock = new object();

        struct ArpGenericData
        {
            public ArpGenericData(UInt64 destinationEthernetAddress, ArpOperation arpOperation, UInt64 targetPhysicalAddress, UInt32 targetProtocolAddress)
            {
                this.DestinationEthernetAddress = destinationEthernetAddress;
                this.ArpOperaton = arpOperation;
                this.TargetPhysicalAddress = targetPhysicalAddress;
                this.TargetProtocolAddress = targetProtocolAddress;
            }

            public UInt64 DestinationEthernetAddress;
            public ArpOperation ArpOperaton;
            public UInt32 TargetProtocolAddress;
            public UInt64 TargetPhysicalAddress;
        }
        System.Threading.Thread _sendArpGenericInBackgroundThread;
        AutoResetEvent _sendArpGenericInBackgroundEvent = new AutoResetEvent(false);
        System.Collections.Queue _sendArpGenericInBackgroundQueue;

        System.Threading.Timer _cleanupArpCacheTimer;

        EthernetInterface _ethernetInterface;
        bool _isDisposed = false;

        public ArpResolver(EthernetInterface ethernetInterface)
        {
            // save a reference to our ethernet interface; we will use this to send ARP fraems
            _ethernetInterface = ethernetInterface;

            // create our ARP cache
            _arpCache = new System.Collections.Hashtable();

            /* write fixed ARP frame parameters (which do not change) to our ARP frame buffer */
            /* Hardware Type: 0x0001 (Ethernet) */
            _arpFrameBuffer[0] = (byte)((HARDWARE_TYPE_ETHERNET >> 8) & 0xFF);
            _arpFrameBuffer[1] = (byte)(HARDWARE_TYPE_ETHERNET & 0xFF);
            /* Protocol Type: 0x0800 (IPv4) */
            _arpFrameBuffer[2] = (byte)((PROTOCOL_TYPE_IPV4 >> 8) & 0xFF);
            _arpFrameBuffer[3] = (byte)(PROTOCOL_TYPE_IPV4 & 0xFF);
            /* Hardware Address Size: 6 bytes */
            _arpFrameBuffer[4] = HARDWARE_ADDRESS_SIZE;
            /* Protocol Address Size: 4 bytes */
            _arpFrameBuffer[5] = PROTOCOL_ADDRESS_SIZE;

            /* fixed values for index and count (passed to EthernetInterface.Send(...) with _arpFrameBuffer */
            _indexArray[0] = 0;
            _countArray[0] = ARP_FRAME_BUFFER_LENGTH;

            // start our "send ARP replies" thread
            _sendArpGenericInBackgroundQueue = new System.Collections.Queue();
            _sendArpGenericInBackgroundThread = new Thread(SendArpGenericThread);
            _sendArpGenericInBackgroundThread.Start();

            // enable our "cleanup ARP cache" timer (fired every 60 seconds)
            _cleanupArpCacheTimer = new Timer(CleanupArpCache, null, 60000, 60000); 

            // wire up the incoming ARP frame handler
            _ethernetInterface.ARPFrameReceived += _ethernetInterface_ARPFrameReceived;
        }

        public void Dispose()
        {
            if (_isDisposed) return;

            _isDisposed = true;

            // shut down our ARP response thread
            if (_sendArpGenericInBackgroundEvent != null)
            {
                _sendArpGenericInBackgroundEvent.Set();
                _sendArpGenericInBackgroundEvent = null;
            }

            // timeout any current ARP requests
            if (_currentArpRequestAnsweredEvent != null)
            {
                _currentArpRequestAnsweredEvent.Set();
                _currentArpRequestAnsweredEvent = null;
            }

            if (_arpCache != null)
                _arpCache.Clear();
            _cleanupArpCacheTimer.Dispose();

            _ethernetInterface = null;
            _arpFrameBuffer = null;
            _arpFrameBufferLock = null;

            _bufferArray = null;
            _indexArray = null;
            _countArray = null;
        }

        internal void SetIpv4Address(UInt32 ipv4ProtocolAddress)
        {
            _ipv4ProtocolAddress = ipv4ProtocolAddress;
        }

        void _ethernetInterface_ARPFrameReceived(object sender, byte[] buffer, int index, int count)
        {
            // verify that our ARP frame is long enough
            if (count < ARP_FRAME_BUFFER_LENGTH)
                return; // discard packet

            /* parse our ARP frame */
            // first validate our frame's fixed header
            UInt16 hardwareType = (UInt16)((buffer[index] << 8) + buffer[index + 1]);
            if (hardwareType != HARDWARE_TYPE_ETHERNET)
                return; // only Ethernet hardware is supported; discard frame
            UInt16 protocolType = (UInt16)((buffer[index + 2] << 8) + buffer[index + 3]);
            if (protocolType != PROTOCOL_TYPE_IPV4)
                return; // only IPv4 protocol is supported; discard frame
            byte hardwareAddressSize = buffer[index + 4];
            if (hardwareAddressSize != HARDWARE_ADDRESS_SIZE)
                return; // invalid hardware address size
            byte protocolAddressSize = buffer[index + 5];
            if (protocolAddressSize != PROTOCOL_ADDRESS_SIZE)
                return; // invalid protocol address size
            ArpOperation operation = (ArpOperation)((buffer[index + 6] << 8) + buffer[index + 7]);
            // NOTE: we will validate the operation a bit later after we compare the incoming targetProtocolAddress.
            // retrieve the sender and target addresses
            UInt64 senderPhysicalAddress = (UInt64)(((UInt64)buffer[index + 8] << 40) + ((UInt64)buffer[index + 9] << 32) + ((UInt64)buffer[index + 10] << 24) + ((UInt64)buffer[index + 11] << 16) + ((UInt64)buffer[index + 12] << 8) + (UInt64)buffer[index + 13]);
            UInt32 senderProtocolAddress = (UInt32)(((UInt32)buffer[index + 14] << 24) + ((UInt32)buffer[index + 15] << 16) + ((UInt32)buffer[index + 16] << 8) + (UInt32)buffer[index + 17]);
            //UInt64 targetHardwareAddress = (UInt64)(((UInt64)buffer[index + 18] << 40) + ((UInt64)buffer[index + 19] << 32) + ((UInt64)buffer[index + 20] << 24) + ((UInt64)buffer[index + 21] << 16) + ((UInt64)buffer[index + 22] << 8) + (UInt64)buffer[index + 23]);
            UInt32 targetProtocolAddress = (UInt32)(((UInt32)buffer[index + 24] << 24) + ((UInt32)buffer[index + 25] << 16) + ((UInt32)buffer[index + 26] << 8) + (UInt32)buffer[index + 27]);

            // if the sender IP is already listed in our ARP cache then update the entry
            lock (_arpCacheLock)
            {
                ArpCacheEntry arpEntry = (ArpCacheEntry)_arpCache[senderProtocolAddress];
                if (arpEntry != null)
                {
                    arpEntry.PhysicalAddress = senderPhysicalAddress;
                    Int64 nowTicks = Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks;
                    arpEntry.TimeoutTicks = nowTicks + (TimeSpan.TicksPerSecond * DEFAULT_ARP_CACHE_TIMEOUT_IN_SECONDS);
                    // NOTE: we do not update the LastUsedTicks property since this is not the result of a request for a non-existent cache entry
                }
                else
                {
                    // do nothing.  some implementation of ARP would create a cache entry for any incoming ARP packets...but we only cache a limited number of entries which we requested.
                }
            }

            if (operation == ArpOperation.ARP_OPERATION_REQUEST)
            {
                // if the request is asking for our IP protocol address, then send an ARP reply
                if (targetProtocolAddress == _ipv4ProtocolAddress)
                {
                    // we do not want to block our RX thread, so queue a response on a worker thread
                    SendArpGenericInBackground(senderPhysicalAddress, ArpOperation.ARP_OPERATION_REPLY, senderPhysicalAddress, senderProtocolAddress);
                }
            }
            else if (operation == ArpOperation.ARP_OPERATION_REPLY)
            {
                if (senderProtocolAddress == _currentArpRequestProtocolAddress)
                {
                    _currentArpRequestPhysicalAddress = senderPhysicalAddress;
                    _currentArpRequestAnsweredEvent.Set();
                }
            }
            else
            {
                // invalid operation; discard frame
            }
        }

        /* this function translates a target IP address into a destination physical address
         * NOTE: if the address could not be translated, the function returns zero. */
        internal UInt64 TranslateIPAddressToPhysicalAddress(UInt32 ipAddress, Int64 timeoutInMachineTicks)
        {
            // if the ipAdderss is the broadcast address, return a broadcast MAC address
            if (ipAddress == 0xFFFFFFFF)
                return ETHERNET_BROADCAST_ADDRESS;

            ArpCacheEntry arpEntry = null;

            // retrieve our existing ARP entry, if it exists
            lock (_arpCacheLock)
            {
                arpEntry = (ArpCacheEntry)_arpCache[ipAddress];

                // if we retrieved an entry, make sure it has not timed out.
                if (arpEntry != null)
                {
                    Int64 nowTicks = Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks;
                    if (arpEntry.TimeoutTicks < nowTicks)
                    {
                        // if the entry has timed out, dispose of it; we will re-query the target
                        _arpCache.Remove(ipAddress);
                        arpEntry = null;
                    }
                }
            }

            // if we were caching a valid ARP entry, return its PhysicalAddress now.
            if (arpEntry != null)
                return arpEntry.PhysicalAddress;

            // if we did not obtain a valid ARP entry, query for one now.
            lock (_simultaneousArpRequestLock) /* lock our current ARP request...we can only have one request at a time. */
            {
                Int32 waitTimeout;
                for (int iAttempt = 0; iAttempt < MAX_ARP_TRANSLATE_ATTEMPTS; iAttempt++)
                {
                    // set the IP Address of our current ARP request
                    _currentArpRequestProtocolAddress = ipAddress;
                    _currentArpRequestPhysicalAddress = 0; // this will be set to a non-zero value if we get a response
                    _currentArpRequestAnsweredEvent.Reset();
                    // send the ARP request
                    SendArpRequest(ipAddress, timeoutInMachineTicks);
                    // wait on a reply (for up to our timeout time or 1 second...whichever is less)
                    waitTimeout = System.Math.Min((Int32)((timeoutInMachineTicks != Int64.MaxValue) ? Math.Max((timeoutInMachineTicks - Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks) / System.TimeSpan.TicksPerMillisecond, 0) : 1000), 1000);
                    _currentArpRequestAnsweredEvent.WaitOne(waitTimeout, false);

                    // if we received an ARP reply, add it to our cache table
                    try
                    {
                        if (_currentArpRequestPhysicalAddress != 0)
                        {
                            lock (_arpCacheLock)
                            {
                                // first make sure that our cache table is not full; if it is full then remove the oldest entry (based on LastUsedTime)
                                if (_arpCache.Count >= ARP_CACHE_MAXIMUM_ENTRIES)
                                {
                                    Int64 oldestLastUsedTicks = Int64.MaxValue;
                                    UInt32 oldestKey = 0;
                                    foreach (UInt32 key in _arpCache.Keys)
                                    {
                                        if (((ArpCacheEntry)_arpCache[key]).LastUsedTicks < oldestLastUsedTicks)
                                        {
                                            oldestKey = key;
                                            oldestLastUsedTicks = ((ArpCacheEntry)_arpCache[key]).LastUsedTicks;
                                        }
                                    }
                                    _arpCache.Remove(oldestKey);
                                }

                                // then add our new cache entry and return the ARP reply's address.
                                Int64 nowTicks = Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks;
                                arpEntry = new ArpCacheEntry(_currentArpRequestPhysicalAddress, nowTicks + (TimeSpan.TicksPerSecond * DEFAULT_ARP_CACHE_TIMEOUT_IN_SECONDS), nowTicks);
                                _arpCache.Add(_currentArpRequestProtocolAddress, arpEntry);
                            }
                            return _currentArpRequestPhysicalAddress;
                        }

                        // if we're out of (user-specified) time without a reply, return zero (no address).
                        if (Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks > timeoutInMachineTicks)
                            return 0;
                    }
                    finally
                    {
                        _currentArpRequestProtocolAddress = 0; // no ARP request is in process now.
                    }
                }
            }

            // if we could not get the address of the target, return zero.
            return 0;
        }

        void SendArpGenericThread()
        {
            while (true)
            {
                _sendArpGenericInBackgroundEvent.WaitOne();

                // if we have been disposed, shut down our thread now.
                if (_isDisposed)
                    return;

                while ((_sendArpGenericInBackgroundQueue != null) && (_sendArpGenericInBackgroundQueue.Count > 0))
                {
                    try
                    {
                        ArpGenericData arpGenericData = (ArpGenericData)_sendArpGenericInBackgroundQueue.Dequeue();
                        SendArpGeneric(arpGenericData.DestinationEthernetAddress, arpGenericData.ArpOperaton, arpGenericData.TargetPhysicalAddress, arpGenericData.TargetProtocolAddress, Int64.MaxValue);
                    }
                    catch (InvalidOperationException)
                    {
                        // reply queue was empty
                    }

                    // if we have been disposed, shut down our thread now.
                    if (_isDisposed)
                        return;
                }
            }
        }

        ///* TODO: verify that this content is correct for an ARP announcement */
        //public void SendArpAnnouncement()
        //{
        //    SendArpGeneric(ETHERNET_BROADCAST_ADDRESS, ArpOperation.ARP_OPERATION_REQUEST, _ethernetInterface.PhysicalAddress, _ipv4ProtocolAddress, 0x000000000000, _ipv4ProtocolAddress, Int64.MaxValue);
        //}

        public void SendArpGratuitousInBackground()
        {
            SendArpGenericInBackground(ETHERNET_BROADCAST_ADDRESS, ArpOperation.ARP_OPERATION_REQUEST, 0x000000000000, _ipv4ProtocolAddress);
        }

        public void SendArpGratuitous()
        {
            SendArpGeneric(ETHERNET_BROADCAST_ADDRESS, ArpOperation.ARP_OPERATION_REQUEST, 0x000000000000, _ipv4ProtocolAddress, Int64.MaxValue);
        }

        ///* TODO: verify that this content is correct for an ARP probe */
        //public void SendArpProbe(UInt32 targetProtocolAddress)
        //{
        //    /* TODO: should the two "0" entries be our NIC's IP address and the broadcast MAC respectively? */
        //    SendArpGeneric(ETHERNET_BROADCAST_ADDRESS, _ethernetInterface.PhysicalAddress, ArpOperation.ARP_OPERATION_REQUEST, _ethernetInterface.PhysicalAddress, 0x00000000, 0x000000000000, targetProtocolAddress, Int64.MaxValue);
        //}

        public void SendArpRequest(UInt32 targetProtocolAddress, Int64 timeoutInMachineTicks)
        {
            SendArpGeneric(ETHERNET_BROADCAST_ADDRESS, ArpOperation.ARP_OPERATION_REQUEST, 0x000000000000, targetProtocolAddress, timeoutInMachineTicks);
        }

        void SendArpReply(UInt64 targetPhysicalAddress, UInt32 targetProtocolAddress)
        {
            SendArpGeneric(targetPhysicalAddress, ArpOperation.ARP_OPERATION_REPLY, targetPhysicalAddress, targetProtocolAddress, Int64.MaxValue);
        }

        void SendArpGenericInBackground(UInt64 destinationEthernetAddress, ArpOperation arpOperation, UInt64 targetPhysicalAddress, UInt32 targetProtocolAddress)
        {
            ArpGenericData arpGenericData = new ArpGenericData(destinationEthernetAddress, arpOperation, targetPhysicalAddress, targetProtocolAddress);
            _sendArpGenericInBackgroundQueue.Enqueue(arpGenericData);
            _sendArpGenericInBackgroundEvent.Set();
        }

        void SendArpGeneric(UInt64 destinationEthernetAddress, ArpOperation arpOperation, UInt64 targetPhysicalAddress, UInt32 targetProtocolAddress, Int64 timeoutInMachineTicks)
        {
            if (_isDisposed) return;

            lock (_arpFrameBufferLock)
            {
                UInt64 physicalAddress = _ethernetInterface.PhysicalAddressAsUInt64;

                // configure ARP packet
                /* Op: request (1) or reply (2) */
                _arpFrameBuffer[6] = (byte)(((UInt16)arpOperation >> 8) & 0xFF);
                _arpFrameBuffer[7] = (byte)((UInt16)arpOperation & 0xFF);
                /* Sender Harwdare Address */
                _arpFrameBuffer[8] = (byte)((physicalAddress >> 40) & 0xFF);
                _arpFrameBuffer[9] = (byte)((physicalAddress >> 32) & 0xFF);
                _arpFrameBuffer[10] = (byte)((physicalAddress >> 24) & 0xFF);
                _arpFrameBuffer[11] = (byte)((physicalAddress >> 16) & 0xFF);
                _arpFrameBuffer[12] = (byte)((physicalAddress >> 8) & 0xFF);
                _arpFrameBuffer[13] = (byte)(physicalAddress & 0xFF);
                /* Sender Protocol Address */
                _arpFrameBuffer[14] = (byte)((_ipv4ProtocolAddress >> 24) & 0xFF);
                _arpFrameBuffer[15] = (byte)((_ipv4ProtocolAddress >> 16) & 0xFF);
                _arpFrameBuffer[16] = (byte)((_ipv4ProtocolAddress >> 8) & 0xFF);
                _arpFrameBuffer[17] = (byte)(_ipv4ProtocolAddress & 0xFF);
                /* Target Harwdare Address (if known) */
                _arpFrameBuffer[18] = (byte)((targetPhysicalAddress >> 40) & 0xFF);
                _arpFrameBuffer[19] = (byte)((targetPhysicalAddress >> 32) & 0xFF);
                _arpFrameBuffer[20] = (byte)((targetPhysicalAddress >> 24) & 0xFF);
                _arpFrameBuffer[21] = (byte)((targetPhysicalAddress >> 16) & 0xFF);
                _arpFrameBuffer[22] = (byte)((targetPhysicalAddress >> 8) & 0xFF);
                _arpFrameBuffer[23] = (byte)(targetPhysicalAddress & 0xFF);
                /* Target Protocol Address */
                _arpFrameBuffer[24] = (byte)((targetProtocolAddress >> 24) & 0xFF);
                _arpFrameBuffer[25] = (byte)((targetProtocolAddress >> 16) & 0xFF);
                _arpFrameBuffer[26] = (byte)((targetProtocolAddress >> 8) & 0xFF);
                _arpFrameBuffer[27] = (byte)(targetProtocolAddress & 0xFF);

                _bufferArray[0] = _arpFrameBuffer;
                _ethernetInterface.Send(destinationEthernetAddress, DATA_TYPE_ARP /* dataType: ARP */, _ipv4ProtocolAddress, targetProtocolAddress, 1 /* one buffer in bufferArray */, _bufferArray, _indexArray, _countArray, timeoutInMachineTicks);
            }
        }

        // this function is called ocassionally to clean up timed-out entries in the ARP cache
        void CleanupArpCache(object state)
        {
            Int64 nowTicks = Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks;
            bool keyWasRemoved = true;

            // NOTE: this loop isn't very efficient since it starts over after every key removal: we may consider more efficient removal processes in the future.
            while (keyWasRemoved == true)
            {
                keyWasRemoved = false; // default to "no keys removed"
                lock (_arpCacheLock)
                {
                    foreach (UInt32 key in _arpCache.Keys)
                    {
                        if (((ArpCacheEntry)_arpCache[key]).TimeoutTicks < nowTicks)
                        {
                            _arpCache.Remove(key);
                            keyWasRemoved = true;
                            break; // exit the loop so we can parse the set of keys again; since we just removed a key the collection is not in a valid enumerable state.
                        }
                    }
                }
            }
        }
    }
}
