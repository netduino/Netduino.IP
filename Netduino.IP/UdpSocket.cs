using System;
using System.Threading;

namespace Netduino.IP
{
    internal class UdpSocket : Socket  
    {
        // fixed buffer for UDP header
        const int UDP_HEADER_LENGTH = 8;
        const int UDP_PSEUDO_HEADER_LENGTH = 12;
        byte[] _udpHeaderBuffer = new byte[UDP_HEADER_LENGTH];
        object _udpHeaderBufferLockObject = new object();
        // fixed buffer for UDP pseudo header
        byte[] _udpPseudoHeaderBuffer = new byte[UDP_PSEUDO_HEADER_LENGTH];
        object _udpPseudoHeaderBufferLockObject = new object();

        /* TODO: consider using a pool of global ReceivedPacketBuffers instead of creating a single buffer per socket */
        internal class ReceivedPacketBuffer
        {
            public UInt32 SourceIPAddress;
            public UInt16 SourceIPPort;
            public byte[] Buffer = new byte[1500];
            public Int32 ActualBufferLength;
            public bool IsEmpty;
            public object LockObject;
        }
        ReceivedPacketBuffer _receivedPacketBuffer = new ReceivedPacketBuffer();
        AutoResetEvent _receivedPacketBufferFilledEvent = new AutoResetEvent(false);

        public UdpSocket(IPv4Layer ipv4Layer, int handle)
            : base(ipv4Layer, handle)
        {
            base._protocolType = IPv4Layer.ProtocolType.Udp;

            InitializeReceivedPacketBuffer(_receivedPacketBuffer);
        }

        public override void Dispose()
        {
            base.Dispose();

            _udpHeaderBuffer = null;
            _udpHeaderBufferLockObject = null;

            _udpPseudoHeaderBuffer = null;
            _udpPseudoHeaderBufferLockObject = null;
        }

        public override void Bind(UInt32 ipAddress, UInt16 ipPort)
        {
            // if ipAddress is IP_ADDRESS_ANY, then change it to to our actual ipAddress.
            if (ipAddress == IP_ADDRESS_ANY)
                ipAddress = _ipv4Layer.IPAddress;

            // verify that this source IP address is correct
            if (ipAddress != _ipv4Layer.IPAddress)
                throw new Exception(); // throw a better exception (address invalid)

            /* ensure that no other UdpSockets are bound to this address/port */
            for (int iSocketHandle = 0; iSocketHandle < IPv4Layer.MAX_SIMULTANEOUS_SOCKETS; iSocketHandle++)
            {
                Socket socket = _ipv4Layer.GetSocket(iSocketHandle);
                if (socket.ProtocolType == IPv4Layer.ProtocolType.Udp &&
                    socket.SourceIPAddress == ipAddress && 
                    socket.SourceIPPort == ipPort)
                {
                    throw new Exception(); /* find a better exception for "cannot bind to already-bound port" */
                }
            }

            _srcIPAddress = ipAddress;
            _srcIPPort = ipPort;
        }

        public override void Connect(UInt32 ipAddress, UInt16 ipPort)
        {
            if (_srcIPAddress == IP_ADDRESS_ANY)
            {
                _srcIPAddress = _ipv4Layer.IPAddress;
            }
            if (_srcIPPort != IP_PORT_ANY)
            {
                _srcIPPort = _ipv4Layer.GetNextEphemeralPortNumber(IPv4Layer.ProtocolType.Udp); // next available ephemeral port #
            }

            // UDP is connectionless, so the Connect function sets the default destination IP address and port values.
            _destIPAddress = ipAddress;
            _destIPPort = ipPort;
        }

        public override bool Poll(int mode, int microSeconds)
        {
            switch (mode)
            {
                case SELECT_MODE_READ:
                    /* [source: MSDN documentation]
                     * return true when:
                     *   - if Listen has been called and a connection is pending
                     *   - if data is available for reading
                     *   - if the connection has been closed, reset, or terminated
                     * otherwise return false */
                    {
                        /* TODO: check if listen has been called and a connection is pending */
                        //return true;

                        if (_receivedPacketBuffer.ActualBufferLength > 0)
                            return true;

                        /* TODO: check if connection has been closed, reset, or terminated */
                        //return true;

                        /* TODO: only check _isDisposed if the connection hasn't been closed/reset/terminated; those other cases should return TRUE */
                        if (_isDisposed) return false;

                        // in all other circumstances, return false.
                        return false;
                    }
                case SELECT_MODE_WRITE:
                    /* [source: MSDN documentation]
                     * return true when:
                     *   - processing a Connect and the connection has succeeded
                     *   - if data can be sent
                     * otherwise return false */
                    {
                        if (_isDisposed) return false;

                        if ((_destIPAddress != 0) && (_destIPPort != 0))
                            return true;
                        else
                            return false;
                    }
                case SELECT_MODE_ERROR:
                    /* [source: MSDN documentation]
                     * return true when:
                     *   - if processing a Connect that does not block--and the connection has failed
                     *   - if OutOfBandInline is not set and out-of-band data is available 
                     * otherwise return false */
                    {
                        if (_isDisposed) return false;

                        return false;
                    }
                default:
                    {
                        // the following line should never be executed
                        return false;
                    }
            }
        }

        public override int Send(byte[] buffer, Int32 offset, Int32 count, Int32 flags, Int64 timeoutInMachineTicks)
        {
            // make sure that a default destination IPEndpoint has been configured.
            if ((_destIPAddress == IP_ADDRESS_ANY) || (_destIPPort == IP_PORT_ANY))
                throw new Exception(); /* TODO: find better exception for "must specify destination ipaddr/port" */

            // send to the default destination ipAddress/ipPort (as specified when Connect was called)
            return SendTo(buffer, offset, count, flags, timeoutInMachineTicks, _destIPAddress, _destIPPort);
        }

        /* TODO: we can't lock permanently while waiting for another UDP packet to be sent; get rid of the _udpHeaderBufferLock "lock that could stay locked forever" */
        public override Int32 SendTo(byte[] buffer, Int32 offset, Int32 count, Int32 flags, Int64 timeoutInMachineTicks, UInt32 ipAddress, UInt16 ipPort)
        {
            lock (_udpHeaderBufferLockObject)
            {
                // UDP header: 8 bytes
                _udpHeaderBuffer[0] = (byte)((_srcIPPort >> 8) & 0xFF);
                _udpHeaderBuffer[1] = (byte)(_srcIPPort & 0xFF);
                _udpHeaderBuffer[2] = (byte)((_destIPPort >> 8) & 0xFF);
                _udpHeaderBuffer[3] = (byte)(_destIPPort & 0xFF);
                UInt16 udpLength = (UInt16)(UDP_HEADER_LENGTH + count);
                _udpHeaderBuffer[4] = (byte)((udpLength >> 8) & 0xFF);
                _udpHeaderBuffer[5] = (byte)(udpLength & 0xFF);
                _udpHeaderBuffer[6] = 0; // for now, no checksum; we'll populate this when we calculate checksum over the pseudoheader + header + data + 16-bit-alignment-if-necessary-zero-pad-byte
                _udpHeaderBuffer[7] = 0; // checksum (continued)

                UInt16 checksum;
                lock (_udpPseudoHeaderBufferLockObject)
                {
                    // create temporary pseudo header
                    _udpPseudoHeaderBuffer[0] = (byte)((_srcIPAddress >> 24) & 0xFF);
                    _udpPseudoHeaderBuffer[1] = (byte)((_srcIPAddress >> 16) & 0xFF);
                    _udpPseudoHeaderBuffer[2] = (byte)((_srcIPAddress >> 8) & 0xFF);
                    _udpPseudoHeaderBuffer[3] = (byte)(_srcIPAddress & 0xFF);
                    _udpPseudoHeaderBuffer[4] = (byte)((_destIPAddress >> 24) & 0xFF);
                    _udpPseudoHeaderBuffer[5] = (byte)((_destIPAddress >> 16) & 0xFF);
                    _udpPseudoHeaderBuffer[6] = (byte)((_destIPAddress >> 8) & 0xFF);
                    _udpPseudoHeaderBuffer[7] = (byte)(_destIPAddress & 0xFF);
                    _udpPseudoHeaderBuffer[8] = 0; // ZERO
                    _udpPseudoHeaderBuffer[9] = (byte)IPv4Layer.ProtocolType.Udp; // Protocol Number
                    _udpPseudoHeaderBuffer[10] = (byte)((udpLength >> 8) & 0xFF);
                    _udpPseudoHeaderBuffer[11] = (byte)(udpLength & 0xFF);

                    // calculate checksum over entire pseudo-header, UDP header and data
                    _checksumBufferArray[0] = _udpPseudoHeaderBuffer;
                    _checksumOffsetArray[0] = 0;
                    _checksumCountArray[0] = UDP_PSEUDO_HEADER_LENGTH;
                    _checksumBufferArray[1] = _udpHeaderBuffer;
                    _checksumOffsetArray[1] = 0;
                    _checksumCountArray[1] = UDP_HEADER_LENGTH;
                    _checksumBufferArray[2] = buffer;
                    _checksumOffsetArray[2] = offset;
                    _checksumCountArray[2] = count;
                    checksum = Utility.CalculateInternetChecksum(_checksumBufferArray, _checksumOffsetArray, _checksumCountArray); /* NOTE: this function will append a pad byte if necessary for 16-bit alignment before calculation */
                }

                // insert checksujm into UDP header
                _udpHeaderBuffer[6] = (byte)((checksum >> 8) & 0xFF);
                _udpHeaderBuffer[7] = (byte)(checksum & 0xFF);

                // queue up our buffer arrays
                _bufferArray[0] = _udpHeaderBuffer;
                _indexArray[0] = 0;
                _countArray[0] = UDP_HEADER_LENGTH;
                _bufferArray[1] = buffer;
                _indexArray[1] = offset;
                _countArray[1] = count;

                /* TODO: deal with our flags and timeout_ms; we shouldn't just be sending while blocking -- and ignoring the flags */
                _ipv4Layer.Send((byte)IPv4Layer.ProtocolType.Udp, _srcIPAddress, _destIPAddress, _bufferArray, _indexArray, _countArray, timeoutInMachineTicks);
            }

            /* TODO: return actual # of bytes sent, if different; this may be caused by a limit on UDP datagram size, timeout while sending data, etc. */
            return count;
        }

        public override Int32 Receive(byte[] buf, Int32 offset, Int32 count, Int32 flags, Int64 timeoutInMachineTicks)
        {
            UInt32 ipAddress;
            UInt16 ipPort;
            return ReceiveFrom(buf, offset, count, flags, timeoutInMachineTicks, out ipAddress, out ipPort);
        }

        public override Int32 ReceiveFrom(byte[] buf, Int32 offset, Int32 count, Int32 flags, Int64 timeoutInMachineTicks, out UInt32 ipAddress, out UInt16 ipPort)
        {
            if (_receivedPacketBuffer.IsEmpty)
            {
                Int32 waitTimeout = System.Math.Min((Int32)((timeoutInMachineTicks != Int64.MaxValue) ? (timeoutInMachineTicks - Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks) / System.TimeSpan.TicksPerMillisecond : 1000), 1000);
                if (waitTimeout < 0) waitTimeout = 0;
                if (!_receivedPacketBufferFilledEvent.WaitOne(waitTimeout, false))
                {
                    /* TODO: do we need to throw a timeout exception here, or just return zero bytes? */
                    ipAddress = 0;
                    ipPort = 0;
                    return 0;
                }
            }

            int bytesRead;
            lock (_receivedPacketBuffer.LockObject)
            {
                bytesRead = Math.Min(count, _receivedPacketBuffer.ActualBufferLength);
                Array.Copy(_receivedPacketBuffer.Buffer, 0, buf, offset, bytesRead);
                ipAddress = _receivedPacketBuffer.SourceIPAddress;
                ipPort = _receivedPacketBuffer.SourceIPPort;

                // now empty our datagram buffer
                InitializeReceivedPacketBuffer(_receivedPacketBuffer);
            }

            return bytesRead;
        }

        internal override void OnPacketReceived(UInt32 sourceIPAddress, UInt32 destinationIPAddress, byte[] buffer, Int32 index, Int32 count)
        {
            lock (_receivedPacketBuffer.LockObject)
            {
                if (count < UDP_HEADER_LENGTH)
                    return;

                /* if we do not have enough room for the incoming frame, discard it */
                if (_receivedPacketBuffer.IsEmpty == false)
                    return;

                UInt16 packetHeaderChecksum = (UInt16)((((UInt16)buffer[index + 6]) << 8) + buffer[index + 7]);

                // if the incoming packet includes a checksum then calculate a checksum to verify packet integrity.
                // NOTE: UDP checksums are mandatory for inclusion on our outgoing datagrams, but are technically optional for incoming IPv4 UDP datagrams
                if (packetHeaderChecksum != 0x0000)
                {
                    UInt16 calculateChecksum;
                    lock (_udpPseudoHeaderBufferLockObject)
                    {
                        // create temporary pseudo header
                        _udpPseudoHeaderBuffer[0] = (byte)((sourceIPAddress >> 24) & 0xFF);
                        _udpPseudoHeaderBuffer[1] = (byte)((sourceIPAddress >> 16) & 0xFF);
                        _udpPseudoHeaderBuffer[2] = (byte)((sourceIPAddress >> 8) & 0xFF);
                        _udpPseudoHeaderBuffer[3] = (byte)(sourceIPAddress & 0xFF);
                        _udpPseudoHeaderBuffer[4] = (byte)((destinationIPAddress >> 24) & 0xFF);
                        _udpPseudoHeaderBuffer[5] = (byte)((destinationIPAddress >> 16) & 0xFF);
                        _udpPseudoHeaderBuffer[6] = (byte)((destinationIPAddress >> 8) & 0xFF);
                        _udpPseudoHeaderBuffer[7] = (byte)(destinationIPAddress & 0xFF);
                        _udpPseudoHeaderBuffer[8] = 0; // ZERO
                        _udpPseudoHeaderBuffer[9] = (byte)IPv4Layer.ProtocolType.Udp; // Protocol Number
                        _udpPseudoHeaderBuffer[10] = (byte)((count >> 8) & 0xFF);
                        _udpPseudoHeaderBuffer[11] = (byte)(count & 0xFF);

                        // calculate checksum over entire pseudo-header, UDP header and data
                        _checksumBufferArray[0] = _udpPseudoHeaderBuffer;
                        _checksumOffsetArray[0] = 0;
                        _checksumCountArray[0] = UDP_PSEUDO_HEADER_LENGTH;
                        _checksumBufferArray[1] = buffer;
                        _checksumOffsetArray[1] = index;
                        _checksumCountArray[1] = count;
                        calculateChecksum = Utility.CalculateInternetChecksum(_checksumBufferArray, _checksumOffsetArray, _checksumCountArray, 2);
                    }
                    if (calculateChecksum != 0x0000)
                        return; // drop packet
                }

                UInt16 sourceIPPort = (UInt16)((((UInt16)buffer[index + 0]) << 8) + buffer[index + 1]);

                _receivedPacketBuffer.IsEmpty = false;
                Array.Copy(buffer, index + UDP_HEADER_LENGTH, _receivedPacketBuffer.Buffer, 0, count - UDP_HEADER_LENGTH);
                _receivedPacketBuffer.ActualBufferLength = count - UDP_HEADER_LENGTH;

                _receivedPacketBuffer.SourceIPAddress = sourceIPAddress;
                _receivedPacketBuffer.SourceIPPort = sourceIPPort;

                _receivedPacketBufferFilledEvent.Set();
            }
        }

        internal void InitializeReceivedPacketBuffer(ReceivedPacketBuffer buffer)
        {
            buffer.SourceIPAddress = 0;
            buffer.SourceIPPort = 0;

            if (buffer.Buffer == null)
                buffer.Buffer = new byte[1500]; /* TODO: determine correct maximum size and make this a const */
            buffer.ActualBufferLength = 0;

            buffer.IsEmpty = true;
            if (buffer.LockObject == null)
                buffer.LockObject = new object();
        }

        public override Int32 GetBytesToRead()
        {
            lock (_receivedPacketBuffer.LockObject)
            {
                if (_receivedPacketBuffer.IsEmpty)
                    return 0;

                return _receivedPacketBuffer.ActualBufferLength;
            }
        }
    }
}
