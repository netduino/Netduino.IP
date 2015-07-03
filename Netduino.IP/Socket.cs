using System;

namespace Netduino.IP
{
    internal class Socket : IDisposable 
    {
        protected int _handle;

        protected bool _isDisposed = false;

        ////const int MAX_TX_DATAGRAM_LENGTH = 1500; /* 1500 bytes */
        ////byte[] _txDatagramBuffer;

        protected const UInt32 IP_ADDRESS_ANY = 0x0000000000000000;
        protected const UInt16 IP_PORT_ANY = 0x0000;

        protected UInt32 _srcIPAddress = IP_ADDRESS_ANY;
        protected UInt16 _srcIPPort = IP_PORT_ANY;

        protected UInt32 _destIPAddress = IP_ADDRESS_ANY;
        protected UInt16 _destIPPort = IP_PORT_ANY;

        protected const Int32 SELECT_MODE_READ = 0;
        protected const Int32 SELECT_MODE_WRITE = 1;
        protected const Int32 SELECT_MODE_ERROR = 2;

        /* send and receive timeout values in milliseconds; default is 0 (infinite) */
        protected int _transmitTimeoutInMilliseconds = 0;
        protected int _receiveTimeoutInMilliseconds = 0;

        protected IPv4Layer.ProtocolType _protocolType;

        public Socket(Int32 handle)
        {
        //    //// create a buffer for TX datagrams; our stack will reuse this same buffer as we push data towards the MAC layer.
        //    //_txDatagramBuffer = new byte[MAX_TX_DATAGRAM_LENGTH];

            _handle = handle;
        }

        public virtual void Dispose()
        {
            _isDisposed = true;

            _handle = -1;

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

        public virtual Socket Accept()
        {
            // virtual method not actually implemented in base Socket class
            return null;
        }

        public virtual void Bind(UInt32 ipAddress, UInt16 ipPort)
        {
            // virtual method not actually implemented in base Socket class
        }

        public virtual void Close()
        {
            // virtual method not actually implemented in base Socket class
        }

        public virtual void Connect(UInt32 ipAddress, UInt16 ipPort)
        {
            // virtual method not actually implemented in base Socket class
        }

        public virtual void Listen(int backlog)
        {
            // virtual method not actually impelmented in base Socket class
        }

        public virtual bool Poll(Int32 mode, Int32 microSeconds)
        {
            // virtual method not actually implemented in base Socket class
            return false;
        }

        internal virtual UInt16 ReceiveBufferSize
        {
            get 
            {   
                return 0;
            }
            set
            {
            }
        }

        internal virtual UInt16 TransmitBufferSize
        {
            get
            {
                return 0;
            }
            set
            {
            }
        }

        public virtual Int32 ReceiveTimeoutInMilliseconds
        {
            get
            {
                return _receiveTimeoutInMilliseconds;
            }
            set
            {
                if (value == 0 || value == -1)
                {
                    _receiveTimeoutInMilliseconds = 0;
                }
                else if (value < -1)
                {
                    throw new ArgumentOutOfRangeException();
                }
                else
                {
                    _receiveTimeoutInMilliseconds = value;
                }
            }
        }

        public virtual Int32 TransmitTimeoutInMilliseconds
        {
            get
            {
                return _transmitTimeoutInMilliseconds;
            }
            set
            {
                if (value == 0 || value == -1)
                {
                    _transmitTimeoutInMilliseconds = 0;
                }
                else if (value < -1)
                {
                    throw new ArgumentOutOfRangeException();
                }
                else if (value >= 500)
                {
                    _transmitTimeoutInMilliseconds = value;
                }
                else // if (value > 0 && value < 500)
                {
                    _transmitTimeoutInMilliseconds = 500;
                }
            }
        }

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

        internal virtual void OnPacketReceived(UInt32 sourceIPAddress, UInt32 destinationIPAddress, byte[] buffer, Int32 index, Int32 count)
        {
            // virtual method not actually implemented in base Socket class
        }

        public virtual Int32 GetBytesToRead()
        {
            // virtual method not actually implemented in base Socket class
            return 0;
        }

        public virtual Int32 Receive(byte[] buf, Int32 offset, Int32 count, Int32 flags, Int64 timeoutInMachineTicks)
        {
            // virtual method not actually implemented in base Socket class
            return 0;
        }

        public virtual Int32 ReceiveFrom(byte[] buf, Int32 offset, Int32 count, Int32 flags, Int64 timeoutInMachineTicks, out UInt32 ipAddress, out UInt16 ipPort)
        {
            // virtual method not actually implemented in base Socket class
            ipAddress = 0;
            ipPort = 0;
            return 0;
        }

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
