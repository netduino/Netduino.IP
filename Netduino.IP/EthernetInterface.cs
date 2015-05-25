using System;

namespace Netduino.IP
{
    internal class EthernetInterface : IDisposable 
    {
        public delegate void PacketReceivedEventHandler(object sender, byte[] buffer, int index, int count);
        public event PacketReceivedEventHandler IPv4PacketReceived;
        public event PacketReceivedEventHandler ARPFrameReceived;

        // fixed buffer for ARP request/reply frames
        const int ETHERNET_HEADER_LENGTH = 14;
        const int ETHERNET_FCS_LENGTH = 4;
        const int PHYSICAL_ADDRESS_LENGTH = 6;
        byte[] _ethernetHeaderBuffer = new byte[ETHERNET_HEADER_LENGTH];
        byte[] _cachedPhysicalAddress = null;
        System.Threading.AutoResetEvent _ethernetHeaderBufferWaitHandle = new System.Threading.AutoResetEvent(true);

        const int MAX_BUFFER_SEGMENT_COUNT = 4;
        byte[][] _bufferArray = new byte[MAX_BUFFER_SEGMENT_COUNT][];
        int[] _indexArray = new int[MAX_BUFFER_SEGMENT_COUNT];
        int[] _countArray = new int[MAX_BUFFER_SEGMENT_COUNT];

        ILinkLayer _linkLayer;
        bool _isDisposed = false;

        public event LinkStateChangedEventHandler LinkStateChanged;

        public EthernetInterface(ILinkLayer linkLayer)
        {
            _linkLayer = linkLayer;
            _cachedPhysicalAddress = _linkLayer.GetMacAddress();

            _linkLayer.LinkStateChanged += _linkLayer_LinkStateChanged;
            _linkLayer.PacketReceived += _linkLayer_PacketReceived;
        }

        void _linkLayer_LinkStateChanged(object sender, bool state)
        {
            if (_isDisposed) throw new ObjectDisposedException();

            // first, raise our LinkStateChanged event; this will let our IPv4Layer send a gratuitous ARP immediately.
            if (LinkStateChanged != null)
                LinkStateChanged(this, state);

            // then raise the NetworkAvailabilityChanged event handler for user applications
            Type networkChangeListenerType = Type.GetType("Microsoft.SPOT.Net.NetworkInformation.NetworkChange+NetworkChangeListener, Microsoft.SPOT.Net");
            if (networkChangeListenerType != null)
            {
                // create instance of NetworkChangeListener
                System.Reflection.ConstructorInfo networkChangeListenerConstructor = networkChangeListenerType.GetConstructor(new Type[] { });
                object networkChangeListener = networkChangeListenerConstructor.Invoke(new object[] { });

                // now call the ProcessEvent function to create a NetworkEvent class.
                System.Reflection.MethodInfo processEventMethodType = networkChangeListenerType.GetMethod("ProcessEvent");
                object networkEvent = processEventMethodType.Invoke(networkChangeListener, new object[] { (UInt32)(((UInt32)(state ? 1 : 0) << 16) + ((UInt32)1 /* AvailabilityChanged*/)), (UInt32)0, DateTime.Now }); /* TODO: should this be DateTime.Now or DateTime.UtcNow? */

                // and finally call the static NetworkChange.OnNetworkChangeCallback function to raise the event.
                Type networkChangeType = Type.GetType("Microsoft.SPOT.Net.NetworkInformation.NetworkChange, Microsoft.SPOT.Net");
                if (networkChangeType != null)
                {
                    System.Reflection.MethodInfo onNetworkChangeCallbackMethod = networkChangeType.GetMethod("OnNetworkChangeCallback", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                    onNetworkChangeCallbackMethod.Invoke(networkChangeType, new object[] { networkEvent });
                }
            }
        }

        void _linkLayer_PacketReceived(object sender, byte[] buffer, int index, int count)
        {
            if (_isDisposed) throw new ObjectDisposedException();

            /* NOTE: the FCS must be verified by the ILinkLayer implementer (or hardware link layer) before passing us the frame; the FCS should not be omitted from the buffer */

            // ignore frames smaller than the standard Ethernet header size
            if (count < ETHERNET_HEADER_LENGTH) return;

            // make sure that this packet is addressed to our NIC
            bool unicastMacAddressMatches = true;
            bool broadcastMacAddressMatches = true;
            for (int i = 0; i < 5; i++)
            {
                if (buffer[index + i] != _cachedPhysicalAddress[i])
                    unicastMacAddressMatches = false;
                if (buffer[index + i] != 0xFF)
                    broadcastMacAddressMatches = false;
            }
            // if the destination MAC address is not a match, drop the packet
            if (!unicastMacAddressMatches && !broadcastMacAddressMatches) return;

            // forward the frame based on its data type
            UInt16 dataType = (UInt16)((buffer[index + 12] << 8) + buffer[index + 13]);
            switch (dataType)
            {
                case 0x0800: /* IPv4 */
                    {
                        if (IPv4PacketReceived != null)
                            IPv4PacketReceived(this, buffer, index + ETHERNET_HEADER_LENGTH, count - ETHERNET_HEADER_LENGTH);
                    }
                    break;
                case 0x0806: /* ARP */
                    {
                        if (ARPFrameReceived != null)
                            ARPFrameReceived(this, buffer, index + ETHERNET_HEADER_LENGTH, count - ETHERNET_HEADER_LENGTH);
                    }
                    break;
                default: /* unsupported data type: drop the frame */
                    return;
            }
        }

        public void Send(UInt64 dstPhysicalAddress, UInt16 dataType, UInt32 srcIPAddress, UInt32 dstIPAddress, int numBufferSegments, byte[][] buffer, int[] offset, int[] count, Int64 timeoutInMachineTicks)
        {
            if (_isDisposed) throw new ObjectDisposedException();

            /* NOTE: Ethernet frames must be at least 64 bytes in length (48 bytes of data + 14 byte header + 2 byte FCS); the ILinkLayer implementer must add padding in its ILinkLayer.SendFrame implementation. */

            /* if we are receiving more than (MAX_BUFFER_SEGMENT_COUNT - 1) buffer segments, abort; if we need more, we'll have to change our array sizes at top */
            if (numBufferSegments > MAX_BUFFER_SEGMENT_COUNT - 1)
                throw new ArgumentException();

            /* wait until the header buffer is free so that we can send our frame--but do not wait beyond the specified expiration */
            Int32 millisecondsUntilTimeout = (Int32)((timeoutInMachineTicks != Int64.MaxValue) ? (timeoutInMachineTicks - Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks) / System.TimeSpan.TicksPerMillisecond : Int32.MaxValue);
            if (millisecondsUntilTimeout < 0) millisecondsUntilTimeout = 0;
            if (!_ethernetHeaderBufferWaitHandle.WaitOne(millisecondsUntilTimeout, false)) return;
            try
            {
                // destination MAC address
                _ethernetHeaderBuffer[0] = (byte)((dstPhysicalAddress >> 40) & 0xFF);
                _ethernetHeaderBuffer[1] = (byte)((dstPhysicalAddress >> 32) & 0xFF);
                _ethernetHeaderBuffer[2] = (byte)((dstPhysicalAddress >> 24) & 0xFF);
                _ethernetHeaderBuffer[3] = (byte)((dstPhysicalAddress >> 16) & 0xFF);
                _ethernetHeaderBuffer[4] = (byte)((dstPhysicalAddress >> 8) & 0xFF);
                _ethernetHeaderBuffer[5] = (byte)(dstPhysicalAddress & 0xFF);
                // source MAC address
                Array.Copy(_cachedPhysicalAddress, 0, _ethernetHeaderBuffer, 6, PHYSICAL_ADDRESS_LENGTH);
                // data type
                _ethernetHeaderBuffer[12] = (byte)((dataType >> 8) & 0xFF);
                _ethernetHeaderBuffer[13] = (byte)(dataType & 0xFF);

                // queue up our buffer arrays
                _bufferArray[0] = _ethernetHeaderBuffer;
                _indexArray[0] = 0;
                _countArray[0] = ETHERNET_HEADER_LENGTH;
                for (int i = 0; i < numBufferSegments; i++)
                {
                    _bufferArray[i + 1] = buffer[i];
                    _indexArray[i + 1] = offset[i];
                    _countArray[i + 1] = count[i];
                }

                // send the datagram (or datagram fragment)
                _linkLayer.SendFrame(numBufferSegments + 1, _bufferArray, _indexArray, _countArray, timeoutInMachineTicks);
            }
            finally
            {
                // let the next transmission proceed.
                _ethernetHeaderBufferWaitHandle.Set();
            }
        }

        public UInt64 PhysicalAddressAsUInt64
        {
            get
            {
                if (_cachedPhysicalAddress == null) return 0;

                return
                    (((UInt64)_cachedPhysicalAddress[0]) << 40) |
                    (((UInt64)_cachedPhysicalAddress[1]) << 32) |
                    (((UInt64)_cachedPhysicalAddress[2]) << 24) |
                    (((UInt64)_cachedPhysicalAddress[3]) << 16) |
                    (((UInt64)_cachedPhysicalAddress[4]) << 8) |
                    (((UInt64)_cachedPhysicalAddress[5]) << 0);
            }
        }

        public bool GetLinkState()
        {
            return _linkLayer.GetLinkState();
        }

        public void Dispose()
        {
            if (_isDisposed) return;

            _isDisposed = true;

            _linkLayer = null;
            _ethernetHeaderBuffer = null;
            _ethernetHeaderBufferWaitHandle = null;

            _bufferArray = null;
            _indexArray = null;
            _countArray = null;
        }
    }
}
