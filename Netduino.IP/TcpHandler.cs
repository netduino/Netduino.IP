using Microsoft.SPOT;
using System;
using System.Threading;

namespace Netduino.IP
{
    internal class TcpHandler : IDisposable 
    {
        protected bool _isDisposed = false;

        IPv4Layer _ipv4Layer;

        // fixed buffer for TCP header
        internal const int TCP_HEADER_MIN_LENGTH = 20; /* no options */
        const int TCP_HEADER_MAX_LENGTH = 60; /* including options */
        const int TCP_PSEUDO_HEADER_LENGTH = 12;
        byte[] _tcpHeaderBuffer = new byte[TCP_HEADER_MAX_LENGTH];
        object _tcpHeaderBufferLockObject = new object();
        // fixed buffer for TCP pseudo header
        byte[] _tcpPseudoHeaderBuffer = new byte[TCP_PSEUDO_HEADER_LENGTH];
        object _tcpPseudoHeaderBufferLockObject = new object();

        protected byte[][] _checksumBufferArray = new byte[3][];
        protected int[] _checksumOffsetArray = new int[3];
        protected int[] _checksumCountArray = new int[3];
        /* NOTE: _checksum... objects are sync-locked by the inherited class if they are used in multiple functions */

        protected byte[][] _bufferArray = new byte[2][];
        protected int[] _indexArray = new int[2];
        protected int[] _countArray = new int[2];

        internal struct TcpOption
        {
            public byte Kind;
            public byte[] Data;

            public TcpOption(byte kind, byte[] data)
            {
                this.Kind = kind;
                this.Data = data;
            }
        }

        public TcpHandler(IPv4Layer ipv4Layer)
        {
            // save a reference to our IPv4Layer; we'll use this to send IPv4 frames
            _ipv4Layer = ipv4Layer;
        }

        public void Dispose()
        {
            _ipv4Layer = null;

            _tcpHeaderBuffer = null;
            _tcpHeaderBufferLockObject = null;

            _tcpPseudoHeaderBuffer = null;
            _tcpPseudoHeaderBufferLockObject = null;

            _checksumBufferArray = null;
            _checksumOffsetArray = null;
            _checksumCountArray = null;

            _bufferArray = null;
            _indexArray = null;
            _countArray = null;
        }

        public IPv4Layer IPv4Layer
        {
            get
            {
                return _ipv4Layer;
            }
        }

        /* NOTE: this function processes incoming frames and stores flags/data; the TcpStateMachine function processes stored flags, advances the state machine as necessary, and sends out responses */
        internal void OnPacketReceived(UInt32 sourceIPAddress, UInt32 destinationIPAddress, byte[] buffer, Int32 index, Int32 count)
        {
            if (_isDisposed)
                return;

            if (count < TCP_HEADER_MIN_LENGTH)
                return;

            UInt16 packetHeaderChecksum = (UInt16)((((UInt16)buffer[index + 16]) << 8) + buffer[index + 17]);

            // calculate checksum to verify packet integrity.
            UInt16 calculateChecksum;
            lock (_tcpPseudoHeaderBufferLockObject)
            {
                // create temporary pseudo header
                _tcpPseudoHeaderBuffer[0] = (byte)((sourceIPAddress >> 24) & 0xFF);
                _tcpPseudoHeaderBuffer[1] = (byte)((sourceIPAddress >> 16) & 0xFF);
                _tcpPseudoHeaderBuffer[2] = (byte)((sourceIPAddress >> 8) & 0xFF);
                _tcpPseudoHeaderBuffer[3] = (byte)(sourceIPAddress & 0xFF);
                _tcpPseudoHeaderBuffer[4] = (byte)((destinationIPAddress >> 24) & 0xFF);
                _tcpPseudoHeaderBuffer[5] = (byte)((destinationIPAddress >> 16) & 0xFF);
                _tcpPseudoHeaderBuffer[6] = (byte)((destinationIPAddress >> 8) & 0xFF);
                _tcpPseudoHeaderBuffer[7] = (byte)(destinationIPAddress & 0xFF);
                _tcpPseudoHeaderBuffer[8] = 0; // ZERO
                _tcpPseudoHeaderBuffer[9] = (byte)IPv4Layer.ProtocolType.Tcp; // Protocol Number
                _tcpPseudoHeaderBuffer[10] = (byte)((count >> 8) & 0xFF);
                _tcpPseudoHeaderBuffer[11] = (byte)(count & 0xFF);

                // calculate checksum over entire pseudo-header, TCP header and data
                _checksumBufferArray[0] = _tcpPseudoHeaderBuffer;
                _checksumOffsetArray[0] = 0;
                _checksumCountArray[0] = TCP_PSEUDO_HEADER_LENGTH;
                _checksumBufferArray[1] = buffer;
                _checksumOffsetArray[1] = index;
                _checksumCountArray[1] = count;
                calculateChecksum = Utility.CalculateInternetChecksum(_checksumBufferArray, _checksumOffsetArray, _checksumCountArray, 2);
            }
            if (calculateChecksum != 0x0000)
                return; // drop packet

            // find the source and destination port #s for our packet (looking into the packet) 
            UInt16 sourceIPPort = (UInt16)((((UInt16)buffer[index + 0]) << 8) + buffer[index + 1]);
            UInt16 destinationIPPort = (UInt16)((((UInt16)buffer[index + 2]) << 8) + buffer[index + 3]);

            TcpSocket socket = null;

            for (int i = 0; i < IPv4Layer.MAX_SIMULTANEOUS_SOCKETS; i++)
            {
                if ((IPv4Layer._sockets[i] != null) &&
                    (IPv4Layer._sockets[i].ProtocolType == IPv4Layer.ProtocolType.Tcp) &&
                    (IPv4Layer._sockets[i].SourceIPAddress == destinationIPAddress) &&
                    (IPv4Layer._sockets[i].SourceIPPort == destinationIPPort) &&
                    (IPv4Layer._sockets[i].DestinationIPAddress == sourceIPAddress) &&
                    (IPv4Layer._sockets[i].DestinationIPPort == sourceIPPort))
                {
                    socket = (TcpSocket)IPv4Layer._sockets[i];
                    socket.OnPacketReceived(sourceIPAddress, destinationIPAddress, buffer, index, count);
                    break;
                }
            }

            /* TODO: if this packet is a request to connect to a listening port, pass it along now */
            if (socket == null)
            {
                for (int i = 0; i < IPv4Layer.MAX_SIMULTANEOUS_SOCKETS; i++)
                {
                    if ((IPv4Layer._sockets[i] != null) &&
                        (IPv4Layer._sockets[i].ProtocolType == IPv4Layer.ProtocolType.Tcp) &&
                        (IPv4Layer._sockets[i].SourceIPAddress == destinationIPAddress) &&
                        (IPv4Layer._sockets[i].SourceIPPort == destinationIPPort) &&
                        (((TcpSocket)IPv4Layer._sockets[i]).IsListening == true)
                        )
                    {
                        socket = (TcpSocket)IPv4Layer._sockets[i];
                        socket.OnPacketReceived(sourceIPAddress, destinationIPAddress, buffer, index, count);
                        break;
                    }
                }
            }

            /* if this packet is otherwise invalid, respond a TCP "RESET" packet */
            if (socket == null)
            {
                /* send a TCP "RESET" packet */
                UInt32 sequenceNumber =
                    ((UInt32)buffer[index + 4] << 24) +
                    ((UInt32)buffer[index + 5] << 16) +
                    ((UInt32)buffer[index + 6] << 8) +
                    (UInt32)buffer[index + 7];

                UInt32 acknowledgmentNumber =
                    ((UInt32)buffer[index + 8] << 24) +
                    ((UInt32)buffer[index + 9] << 16) +
                    ((UInt32)buffer[index + 10] << 8) +
                    (UInt32)buffer[index + 11];

                SendTcpSegment(destinationIPAddress, sourceIPAddress, destinationIPPort, sourceIPPort,
                    acknowledgmentNumber, sequenceNumber, 0, false, false, true, false, false, null, new byte[] { }, 0, 0, Int64.MaxValue);
            }
        }

        internal void SendTcpSegment(UInt32 sourceIPAddress, UInt32 destinationIPAddress, UInt16 sourceIPPort, UInt16 destinationIPPort, UInt32 sequenceNumber, UInt32 acknowledgementNumber, UInt16 windowSize, bool sendAck, bool sendPsh, bool sendRst, bool sendSyn, bool sendFin, TcpOption[] tcpOptions, byte[] buffer, Int32 offset, Int32 count, Int64 timeoutInMachineTicks)
        {
            if (_isDisposed) return;

            lock (_tcpHeaderBufferLockObject)
            {
                Int32 tcpHeaderLength = TCP_HEADER_MIN_LENGTH + (sendSyn ? 4 : 0);

                // TCP basic header: 20 bytes
                _tcpHeaderBuffer[0] = (byte)((sourceIPPort >> 8) & 0xFF);
                _tcpHeaderBuffer[1] = (byte)(sourceIPPort & 0xFF);
                _tcpHeaderBuffer[2] = (byte)((destinationIPPort >> 8) & 0xFF);
                _tcpHeaderBuffer[3] = (byte)(destinationIPPort & 0xFF);
                // sequence number
                _tcpHeaderBuffer[4] = (byte)((sequenceNumber >> 24) & 0xFF);
                _tcpHeaderBuffer[5] = (byte)((sequenceNumber >> 16) & 0xFF);
                _tcpHeaderBuffer[6] = (byte)((sequenceNumber >> 8) & 0xFF);
                _tcpHeaderBuffer[7] = (byte)(sequenceNumber & 0xFF);
                // acknowledment number
                if (sendAck)
                {
                    _tcpHeaderBuffer[8] = (byte)((acknowledgementNumber >> 24) & 0xFF);
                    _tcpHeaderBuffer[9] = (byte)((acknowledgementNumber >> 16) & 0xFF);
                    _tcpHeaderBuffer[10] = (byte)((acknowledgementNumber >> 8) & 0xFF);
                    _tcpHeaderBuffer[11] = (byte)(acknowledgementNumber & 0xFF);
                }
                else
                {
                    Array.Clear(_tcpHeaderBuffer, 8, 4);
                }
                // header length and flags
                _tcpHeaderBuffer[12] = (byte)((tcpHeaderLength / 4) << 4);
                // more flags
                _tcpHeaderBuffer[13] = (byte)(
                    (sendFin ? 1 << 0 : 0) |
                    (sendSyn ? 1 << 1 : 0) |
                    (sendRst ? 1 << 2 : 0) |
                    (sendPsh ? 1 << 3 : 0) |
                    (sendAck ? 1 << 4 : 0)
                    );
                // window size (i.e. how much data we are willing to receive)
                _tcpHeaderBuffer[14] = (byte)((windowSize >> 8) & 0xFF);
                _tcpHeaderBuffer[15] = (byte)(windowSize & 0xFF);
                // tcp checksum
                Array.Clear(_tcpHeaderBuffer, 16, 2);
                // urgent pointer
                /* NOTE: bytes 18-19, never populated */

                // TCP options: empty by default policy -- but zero-initialized under any circumstance
                Array.Clear(_tcpHeaderBuffer, TCP_HEADER_MIN_LENGTH, TCP_HEADER_MAX_LENGTH - TCP_HEADER_MIN_LENGTH);

                /* TCP options */
                if (tcpOptions != null)
                {
                    int headerPos = TCP_HEADER_MIN_LENGTH;
                    for (int iOption = 0; iOption < tcpOptions.Length; iOption++)
                    {
                        _tcpHeaderBuffer[headerPos++] = tcpOptions[iOption].Kind;
                        switch (tcpOptions[iOption].Kind)
                        {
                            case 0: /* EOL = End of Option List */
                            case 1: /* NOP = No OPeration; used for padding */
                                break;
                            default:
                                {
                                    if (tcpOptions[iOption].Data != null)
                                    {
                                        _tcpHeaderBuffer[headerPos++] = (byte)(tcpOptions[iOption].Data.Length + 2);
                                        Array.Copy(tcpOptions[iOption].Data, 0, _tcpHeaderBuffer, headerPos, tcpOptions[iOption].Data.Length);
                                        headerPos += tcpOptions[iOption].Data.Length;
                                    }
                                    else
                                    {
                                        _tcpHeaderBuffer[headerPos++] = 2; /* only 2 bytes--the kind and the length--with no data */
                                    }
                                }
                                break;
                        }
                    }
                }                

                UInt16 checksum;
                lock (_tcpPseudoHeaderBufferLockObject)
                {
                    // create temporary pseudo header
                    _tcpPseudoHeaderBuffer[0] = (byte)((sourceIPAddress >> 24) & 0xFF);
                    _tcpPseudoHeaderBuffer[1] = (byte)((sourceIPAddress >> 16) & 0xFF);
                    _tcpPseudoHeaderBuffer[2] = (byte)((sourceIPAddress >> 8) & 0xFF);
                    _tcpPseudoHeaderBuffer[3] = (byte)(sourceIPAddress & 0xFF);
                    _tcpPseudoHeaderBuffer[4] = (byte)((destinationIPAddress >> 24) & 0xFF);
                    _tcpPseudoHeaderBuffer[5] = (byte)((destinationIPAddress >> 16) & 0xFF);
                    _tcpPseudoHeaderBuffer[6] = (byte)((destinationIPAddress >> 8) & 0xFF);
                    _tcpPseudoHeaderBuffer[7] = (byte)(destinationIPAddress & 0xFF);
                    _tcpPseudoHeaderBuffer[8] = 0; // ZERO
                    _tcpPseudoHeaderBuffer[9] = (byte)IPv4Layer.ProtocolType.Tcp; // Protocol Number
                    UInt16 tcpLength = (UInt16)(tcpHeaderLength + count);
                    _tcpPseudoHeaderBuffer[10] = (byte)((tcpLength >> 8) & 0xFF);
                    _tcpPseudoHeaderBuffer[11] = (byte)(tcpLength & 0xFF);

                    // calculate checksum over entire pseudo-header, UDP header and data
                    _checksumBufferArray[0] = _tcpPseudoHeaderBuffer;
                    _checksumOffsetArray[0] = 0;
                    _checksumCountArray[0] = TCP_PSEUDO_HEADER_LENGTH;
                    _checksumBufferArray[1] = _tcpHeaderBuffer;
                    _checksumOffsetArray[1] = 0;
                    _checksumCountArray[1] = tcpHeaderLength;
                    _checksumBufferArray[2] = buffer;
                    _checksumOffsetArray[2] = offset;
                    _checksumCountArray[2] = count;
                    checksum = Utility.CalculateInternetChecksum(_checksumBufferArray, _checksumOffsetArray, _checksumCountArray); /* NOTE: this function will append a pad byte if necessary for 16-bit alignment before calculation */
                }

                // insert checksujm into UDP header
                _tcpHeaderBuffer[16] = (byte)((checksum >> 8) & 0xFF);
                _tcpHeaderBuffer[17] = (byte)(checksum & 0xFF);

                // queue up our buffer arrays
                _bufferArray[0] = _tcpHeaderBuffer;
                _indexArray[0] = 0;
                _countArray[0] = tcpHeaderLength;
                _bufferArray[1] = buffer;
                _indexArray[1] = offset;
                _countArray[1] = count;

                /* TODO: deal with our flags and timeout_ms; we shouldn't just be sending while blocking -- and ignoring the flags */
                _ipv4Layer.Send((byte)IPv4Layer.ProtocolType.Tcp, sourceIPAddress, destinationIPAddress, _bufferArray, _indexArray, _countArray, timeoutInMachineTicks);
            }
        }
    }
}
