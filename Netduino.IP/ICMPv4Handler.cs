using System;
using System.Threading;

namespace Netduino.IP
{
    internal class ICMPv4Handler : IDisposable 
    {
        // fixed buffer for ICMP frames
        const int ICMP_HEADER_LENGTH = 8;
        const int ICMP_FRAME_BUFFER_LENGTH = 8;
        byte[] _icmpFrameBuffer = new byte[ICMP_FRAME_BUFFER_LENGTH];
        object _icmpFrameBufferLock = new object();

        byte[][] _bufferArray = new byte[2][];
        int[] _indexArray = new int[2];
        int[] _countArray = new int[2];

        protected byte[][] _checksumBufferArray = new byte[2][];
        protected int[] _checksumOffsetArray = new int[2];
        protected int[] _checksumCountArray = new int[2];
        object _checksumLock = new object();

        IPv4Layer _ipv4Layer;

        bool _isDisposed = false;

        enum IcmpMessageType : byte
        {
            EchoReply = 0,
            //DestinationUnreachable = 3,
            Echo = 8, /* EchoRequest */
            //TimeExceeded = 11,
        }

        enum IcmpMessageCode : byte
        {
            None = 0,
            //TimeExceeded_TtlExpiredInTransit = 0,
            //TimeExceeded_FragmentReassemblyTimeExceeded = 1,
        }

        struct IcmpMessage
        {
            public UInt32 DestinationIPAddress;
            public IcmpMessageType IcmpMessageType;
            public IcmpMessageCode IcmpMessageCode;
            public byte[] RestOfHeader;
            public byte[] Data;

            public IcmpMessage(UInt32 destinationIPAddress, IcmpMessageType icmpMessageType, IcmpMessageCode icmpMessageCode, byte[] restOfHeader, byte[] data)
            {
                this.DestinationIPAddress = destinationIPAddress;
                this.IcmpMessageType = icmpMessageType;
                this.IcmpMessageCode = icmpMessageCode;
                this.RestOfHeader = new byte[4];
                if (restOfHeader != null)
                {
                    Array.Copy(restOfHeader, this.RestOfHeader, System.Math.Min(this.RestOfHeader.Length, restOfHeader.Length));
                }
                if (data != null)
                {
                    this.Data = data;
                }
                else
                {
                    this.Data = new byte[0];
                }
            }
        }
        System.Threading.Thread _sendIcmpMessagesInBackgroundThread;
        AutoResetEvent _sendIcmpMessagesInBackgroundEvent = new AutoResetEvent(false);
        System.Collections.Queue _sendIcmpMessagesInBackgroundQueue;

        //struct EchoRequest
        //{
        //    public UInt32 DestinationIPAddress;
        //    public UInt16 Identifier;
        //    public UInt16 SequenceNumber;
        //    public byte[] Data;

        //    AutoResetEvent _responseReceivedEvent;

        //    public EchoRequest(UInt32 destinationIPAddress, UInt16 identifier, UInt16 sequenceNumber, byte[] data)
        //    {
        //        this.DestinationIPAddress = destinationIPAddress;
        //        this.Identifier = identifier;
        //        this.SequenceNumber = sequenceNumber;
        //        this.Data = data;

        //        _responseReceivedEvent = new AutoResetEvent(false);
        //    }

        //    public bool WaitForResponse(Int32 millisecondsTimeout)
        //    {
        //        return _responseReceivedEvent.WaitOne(millisecondsTimeout, false);
        //    }

        //    internal void SetResponseReceived()
        //    {
        //        _responseReceivedEvent.Set();
        //    }
        //}
        //System.Collections.ArrayList _outstandingEchoRequests = new System.Collections.ArrayList();
        //object _outstandingEchoRequestsLock = new object();
        //UInt16 _nextEchoRequestSequenceNumber = 0;

        internal ICMPv4Handler(IPv4Layer ipv4Layer)
        {
            _ipv4Layer = ipv4Layer;

            // start our "send ICMP messages transmission" thread
            _sendIcmpMessagesInBackgroundQueue = new System.Collections.Queue();
            _sendIcmpMessagesInBackgroundThread = new Thread(SendIcmpMessagesThread);
            _sendIcmpMessagesInBackgroundThread.Start();
        }

        public void Dispose()
        {
            if (_isDisposed) return;

            _isDisposed = true;

            // shut down our icmp messages transmission thread
            if (_sendIcmpMessagesInBackgroundEvent != null)
            {
                _sendIcmpMessagesInBackgroundEvent.Set();
                _sendIcmpMessagesInBackgroundEvent = null;
            }

            _ipv4Layer = null;

            _icmpFrameBuffer = null;
            _icmpFrameBufferLock = null;

            //if (_outstandingEchoRequests != null)
            //{
            //    for (int i = 0; i < _outstandingEchoRequests.Count; i++)
            //    {
            //        if (_outstandingEchoRequests[i] != null)
            //        {
            //            ((IDisposable)_outstandingEchoRequests[i]).Dispose();
            //        }
            //    }
            //}
            //_outstandingEchoRequests = null;
            //_outstandingEchoRequestsLock = null;

            _bufferArray = null;
            _indexArray = null;
            _countArray = null;

            _checksumBufferArray = null;
            _checksumOffsetArray = null;
            _checksumCountArray = null;
            _checksumLock = null;
        }

        internal void OnPacketReceived(UInt32 sourceIPAddress, byte[] buffer, Int32 index, Int32 count)
        {
            if (count < ICMP_HEADER_LENGTH)
                return;

            UInt16 verifyChecksum;
            lock (_checksumLock)
            {
                // calculate checksum over entire ICMP frame including data
                _checksumBufferArray[0] = buffer;
                _checksumOffsetArray[0] = index;
                _checksumCountArray[0] = count;
                verifyChecksum = Utility.CalculateInternetChecksum(_checksumBufferArray, _checksumOffsetArray, _checksumCountArray, 1);
            }

            if (verifyChecksum != 0x0000)
                return;

            /* parse ICMP message */
            /* Type */
            IcmpMessageType icmpMessageType = (IcmpMessageType)buffer[index + 0];
            /* Code (Subtype) */
            IcmpMessageCode icmpMessageCode = (IcmpMessageCode)buffer[index + 1];
            /* Rest of header */
            byte[] restOfHeader = new byte[4];
            Array.Copy(buffer, index + 4, restOfHeader, 0, restOfHeader.Length);
            /* Data */
            byte[] data = new byte[count - 8];
            Array.Copy(buffer, index + 8, data, 0, data.Length);

            /* process ICMP message */
            switch (icmpMessageType)
            {
                case IcmpMessageType.Echo:
                    {
                        SendIcmpMessageInBackground(sourceIPAddress, IcmpMessageType.EchoReply, IcmpMessageCode.None, restOfHeader, data);
                    }
                    break;
                //case IcmpMessageType.EchoReply:
                //    {
                //        lock (_outstandingEchoRequestsLock)
                //        {
                //            for (int i = 0; i < _outstandingEchoRequests.Count; i++)
                //            {
                //                if (_outstandingEchoRequests[i] != null)
                //                {
                //                    // check to see if this request matches
                //                    EchoRequest echoRequest = (EchoRequest)_outstandingEchoRequests[i];
                //                    if (echoRequest.DestinationIPAddress == sourceIPAddress)
                //                    {
                //                        /* parse restOfHeader */
                //                        UInt16 identifier = (UInt16)((restOfHeader[0] << 8) + restOfHeader[1]);
                //                        UInt16 sequenceNumber = (UInt16)((restOfHeader[2] << 8) + restOfHeader[3]);
                //                        if ((echoRequest.Identifier == identifier) && (echoRequest.SequenceNumber == sequenceNumber) && (echoRequest.Data.Length == data.Length))
                //                        {
                //                            bool dataMatches = true;
                //                            for (int iData = 0; iData < data.Length; iData++)
                //                            {
                //                                if (data[iData] != echoRequest.Data[iData])
                //                                {
                //                                    dataMatches = false;
                //                                    break;
                //                                }
                //                            }
                //                            if (dataMatches)
                //                                echoRequest.SetResponseReceived();
                //                        }
                //                    }
                //                }
                //            }
                //        }
                //    }
                //    break;
                default:
                    break;
            }
        }

        ///* this function returns true if ping was success, and false if ping was not successful */
        //bool PingDestinationIPAddress(UInt32 destinationIPAddress)
        //{
        //    byte[] restOfHeader = new byte[4];
        //    UInt16 identifier = 0x0000;
        //    UInt16 sequenceNumber = _nextEchoRequestSequenceNumber++;
        //    restOfHeader[0] = (byte)((identifier << 8) & 0xFF);
        //    restOfHeader[1] = (byte)(identifier & 0xFF);
        //    restOfHeader[2] = (byte)((sequenceNumber << 8) & 0xFF);
        //    restOfHeader[3] = (byte)(sequenceNumber & 0xFF);
        //    /* for data, we will include a simple 8-character array.  we could alternatively send a timestamp, etc. */
        //    byte[] data = System.Text.Encoding.UTF8.GetBytes("abcdefgh");

        //    EchoRequest echoRequest = new EchoRequest(destinationIPAddress, identifier, sequenceNumber, data);
        //    lock (_outstandingEchoRequests)
        //    {
        //        _outstandingEchoRequests.Add(echoRequest);
        //    }

        //    bool echoReplyReceived = false;
        //    try
        //    {
        //        Int64 timeoutInMachineTicks = 1 * TimeSpan.TicksPerSecond; /* wait up to one second for ping to be sent */

        //        /* send ICMP echo request */
        //        SendIcmpMessage(destinationIPAddress, IcmpMessageType.Echo, IcmpMessageCode.None, restOfHeader, data, timeoutInMachineTicks);

        //        /* wait for ICMP echo reply for up to one second */
        //        echoReplyReceived = echoRequest.WaitForResponse(1000);

        //    }
        //    finally
        //    {
        //        /* remove ICMP request from collection */
        //        lock (_outstandingEchoRequests)
        //        {
        //            _outstandingEchoRequests.Remove(echoRequest);
        //        }
        //    }

        //    /* if we did not get a match, return false */
        //    return echoReplyReceived;
        //}

        void SendIcmpMessagesThread()
        {
            while (true)
            {
                _sendIcmpMessagesInBackgroundEvent.WaitOne();

                // if we have been disposed, shut down our thread now.
                if (_isDisposed)
                    return;

                while ((_sendIcmpMessagesInBackgroundQueue != null) && (_sendIcmpMessagesInBackgroundQueue.Count > 0))
                {
                    try
                    {
                        IcmpMessage icmpMessage = (IcmpMessage)_sendIcmpMessagesInBackgroundQueue.Dequeue();
                        SendIcmpMessage(icmpMessage.DestinationIPAddress, icmpMessage.IcmpMessageType, icmpMessage.IcmpMessageCode, icmpMessage.RestOfHeader, icmpMessage.Data, Int64.MaxValue);
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

        void SendIcmpMessageInBackground(UInt32 destinationIPAddress, IcmpMessageType icmpMessageType, IcmpMessageCode icmpMessageCode, byte[] restOfHeader, byte[] data)
        {
            IcmpMessage icmpMessage = new IcmpMessage(destinationIPAddress, icmpMessageType, icmpMessageCode, restOfHeader, data);
            _sendIcmpMessagesInBackgroundQueue.Enqueue(icmpMessage);
            _sendIcmpMessagesInBackgroundEvent.Set();
        }

        void SendIcmpMessage(UInt32 destinationIPAddress, IcmpMessageType icmpMessageType, IcmpMessageCode icmpMessageCode, byte[] restOfHeader, byte[] data, Int64 timeoutInMachineTicks)
        {
            if (_isDisposed) return;

            lock (_icmpFrameBufferLock)
            {
                // configure ICMPv4 frame
                /* Type */
                _icmpFrameBuffer[0] = (byte)icmpMessageType;
                /* Code (Subtype) */
                _icmpFrameBuffer[1] = (byte)icmpMessageCode;
                /* checksum (clear this for now; we will calculate it shortly) */
                Array.Clear(_icmpFrameBuffer, 2, 2);
                /* restOfHeader */
                if (restOfHeader.Length < 4) Array.Clear(_icmpFrameBuffer, 4, 4);
                Array.Copy(restOfHeader, 0, _icmpFrameBuffer, 4, System.Math.Min(4, restOfHeader.Length));

                UInt16 checksum;
                lock (_checksumLock)
                {
                    // calculate checksum over entire ICMP frame including data
                    _checksumBufferArray[0] = _icmpFrameBuffer;
                    _checksumOffsetArray[0] = 0;
                    _checksumCountArray[0] = ICMP_FRAME_BUFFER_LENGTH;
                    _checksumBufferArray[1] = data;
                    _checksumOffsetArray[1] = 0;
                    _checksumCountArray[1] = data.Length;
                    checksum = Utility.CalculateInternetChecksum(_checksumBufferArray, _checksumOffsetArray, _checksumCountArray, 2);
                }
                _icmpFrameBuffer[2] = (byte)((checksum >> 8) & 0xFF);
                _icmpFrameBuffer[3] = (byte)(checksum & 0xFF);

                _bufferArray[0] = _icmpFrameBuffer;
                _indexArray[0] = 0;
                _countArray[0] = ICMP_FRAME_BUFFER_LENGTH;
                _bufferArray[1] = data;
                _indexArray[1] = 0;
                _countArray[1] = data.Length;
                _ipv4Layer.Send(1 /* PROTOCOL for ICMP */, _ipv4Layer.IPAddress, destinationIPAddress, _bufferArray, _indexArray, _countArray, timeoutInMachineTicks);
            }
        }

    }
}
