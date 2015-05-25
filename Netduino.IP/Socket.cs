using System;

namespace Netduino.IP
{
    internal class Socket : IDisposable 
    {
        protected IPv4Layer _ipv4Layer;
        protected int _handle;

        protected bool _isDisposed = false;

        ////const int MAX_TX_DATAGRAM_LENGTH = 1500; /* 1500 bytes */
        ////byte[] _txDatagramBuffer;

        protected byte[][] _checksumBufferArray = new byte[3][];
        protected int[] _checksumOffsetArray = new int[3];
        protected int[] _checksumCountArray = new int[3];

        ///* NOTE: TEMPORARY! */
        //protected byte[][] _checksumBufferArray2 = new byte[2][];
        //protected int[] _checksumOffsetArray2 = new int[2];
        //protected int[] _checksumCountArray2 = new int[2];

        protected byte[][] _bufferArray = new byte[2][];
        protected int[] _indexArray = new int[2];
        protected int[] _countArray = new int[2];

        protected const UInt32 IP_ADDRESS_ANY = 0x0000000000000000;
        protected const UInt16 IP_PORT_ANY = 0x0000;

        protected UInt32 _srcIPAddress = IP_ADDRESS_ANY;
        protected UInt16 _srcIPPort = IP_PORT_ANY;

        protected UInt32 _destIPAddress = IP_ADDRESS_ANY;
        protected UInt16 _destIPPort = IP_PORT_ANY;

        //protected const int SELECT_MODE_READ = 0;
        //protected const int SELECT_MODE_WRITE = 1;
        //protected const int SELECT_MODE_ERROR = 2;

        protected IPv4Layer.ProtocolType _protocolType;

        public Socket(IPv4Layer ipv4Layer, Int32 handle)
        {
        //    //// create a buffer for TX datagrams; our stack will reuse this same buffer as we push data towards the MAC layer.
        //    //_txDatagramBuffer = new byte[MAX_TX_DATAGRAM_LENGTH];

        //    // save a reference to our ILinkLayer; we'll use this to send IPv4 frames
            _ipv4Layer = ipv4Layer;
            _handle = handle;
        }

        public virtual void Dispose()
        {
            _isDisposed = true;

            _ipv4Layer = null;
            _handle = -1;

        //    _checksumBufferArray = null;
        //    _checksumOffsetArray = null;
        //    _checksumCountArray = null;

        //    _bufferArray = null;
        //    _indexArray = null;
        //    _countArray = null;

            _srcIPAddress = IP_ADDRESS_ANY;
            _srcIPPort = IP_PORT_ANY;
        }

        public int Handle
        {
            get
            {
                return _handle;
            }
        }

        //public virtual void Bind(UInt32 ipAddress, UInt16 ipPort)
        //{
        //    // virtual method not actually implemented in base Socket class
        //}

        public virtual void Connect(UInt32 ipAddress, UInt16 ipPort)
        {
            // virtual method not actually implemented in base Socket class
        }

        //public virtual bool Poll(Int32 mode, Int32 microSeconds)
        //{
        //    // virtual method not actually implemented in base Socket class
        //    return false;
        //}

        public virtual Int32 Send(byte[] buffer, Int32 offset, Int32 count, Int32 flags, Int64 timeoutInMachineTicks)
        {
            // virtual method not actually implemented in base Socket class
            return 0;
        }

        public virtual Int32 SendTo(byte[] buffer, int offset, int count, int flags, Int64 timeoutInMachineTicks, UInt32 ipAddress, UInt16 ipPort)
        {
            // virtual method not actually implemented in base Socket class
            return 0;
        }

        internal virtual void OnPacketReceived(byte[] buffer, Int32 index, Int32 count)
        {
            // virtual method not actually implemented in base Socket class
        }

        //public virtual UInt32 GetBytesToRead()
        //{
        //    // virtual method not actually implemented in base Socket class
        //    return 0;
        //}

        //public virtual Int32 Receive(byte[] buf, Int32 offset, Int32 count, Int32 flags, Int64 timeoutInMachineTicks)
        //{
        //    // virtual method not actually implemented in base Socket class
        //    return 0;
        //}

        //public virtual Int32 ReceiveFrom(byte[] buf, Int32 offset, Int32 count, Int32 flags, Int64 timeoutInMachineTicks, out UInt32 ipAddress, out UInt16 ipPort)
        //{
        //    // virtual method not actually implemented in base Socket class
        //    ipAddress = 0;
        //    ipPort = 0;
        //    return 0;
        //}

        internal UInt32 SourceIPAddress
        {
            get
            {
                return _srcIPAddress;
            }
        }

        internal UInt16 SourceIPPort
        {
            get
            {
                return _srcIPPort;
            }
        }

        internal UInt32 DestinationIPAddress
        {
            get
            {
                return _destIPAddress;
            }
        }

        internal UInt16 DestinationIPPort
        {
            get
            {
                return _destIPPort;
            }
        }

        internal IPv4Layer.ProtocolType ProtocolType
        {
            get
            {
                return _protocolType;
            }
        }
    }
}
