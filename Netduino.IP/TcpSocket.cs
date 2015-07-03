using Microsoft.SPOT;
using System;
using System.Threading;

namespace Netduino.IP
{
    class TcpSocket : Socket  
    {
        TcpHandler _tcpHandler;

        bool _sourceIpAddressAndPortAssigned = false;

        System.Random _randomGenerator = new Random();

        UInt32 _receiveWindowLeftEdge = 0; /* this tracks the next sequence # we expect to receive from the destination host */
        UInt32 _receiveWindowRightEdge = 0; /* this tracks the last sequence # we expect to receive from the destnation host (plus 1) */
        //UInt32 _receiveWindowScale = 0; /* 2^0; this is our default scale.  we bit-shift the window size to the left (i.e. _receiveWindowSize = windowSize * 2^_receiveWindowScale) */
        UInt32 _receiveBufferSize = 1461; /* the actual size of our internal receive buffer; it should match the _receiveWindowSize */
        /* NOTE: in IPv6 or hybrid IPv4+IPv6 mode, we should set our receive window maximum segment size to 1440 (since IPv6 requires 20 extra bytes for its IPv6 header; otherwise use 1460 for IPv4-only.
         *       in any cirumstance, 536 bytes is the minimum segment data size requried by the TCP protocol */
        const UInt32 _receiveWindowMaximumSegmentSize = 1440 /* min: 536 */; /* this is the maximum segment size (for our receive processing) which we have advertised to the destination host */
        //const Int32 RECEIVE_BUFFER_MIN_SIZE = 536; /* 536 bytes is the minimum receive buffer size allowed by TCP spec */

        UInt32 _transmitWindowLeftEdge = 0; /* this equals the first byte in our outgoing sliding window */
        UInt32 _transmitWindowRightEdge = 0; /* this equals the last byte (plus 1) in our outgoing sliding window */
        UInt32 _transmitWindowMaximumSegmentSize = 536; /* this is the maximum segment size sent by the destination host during connection establishment; default is 536 bytes (the minimum in TCP spec) */
        UInt32 _transmitWindowSentButNotAcknowledgedMarker = 0; /* this equals the last byte (plus 1) which has been sent but not yet acknowledged */
        //UInt32 _transmitWindowScale = 0; /* 2^0; this is our default scale.  we bit-shift the window size to the left (i.e. _transmitWindowSize = windowSize * 2^_transmitWindowScale) */
        UInt32 _transmitBufferSize = 1461; /* the actual size of our internal transmit buffer */

        /* NOTES on receive buffer:
         * 1. the buffer wraps around; data does not necessarily start at position 0.
         * 2. if (_receiveBufferNextBytePos == _receiveBufferFirstBytePos) then the buffer is empty.
         * 3. if (_receiveBufferNextBytePos == _receiveBufferFirstBytePos - 1) then the buffer is full. [note: we must track wrap-around as well; firstbytepos could be zero and nextbytepos could be UInt32.MaxValue] */
        byte[] _receiveBuffer;
        UInt32 _receiveBufferFirstBytePos = 0; /* the position where the first occupied byte is stored */
        UInt32 _receiveBufferNextBytePos = 0; /* the position where we should store the next byte */
        object _receiveBufferLockObject = new object();
        AutoResetEvent _receiveBufferSpaceFilledEvent = new AutoResetEvent(false);

        /* NOTES on transmit buffer:
         * 1. the buffer wraps around; data does not necessarily start at position 0.
         * 2. if (_transmitBufferNextBytePos == _transmitBufferFirstBytePos) then the buffer is empty.
         * 3. if (_transmitBufferNextBytePos == _transmitBufferFirstBytePos - 1) then the buffer is full. [note: we must track wrap-around as well; firstbytepos could be zero and nextbytepos could be UInt32.MaxValue] */
        byte[] _transmitBuffer;
        UInt32 _transmitBufferFirstBytePos = 0; /* the position where the first occupied byte is stored */
        UInt32 _transmitBufferNextBytePos = 0; /* the position where we should store the next byte */
        object _transmitBufferLockObject = new object();
        AutoResetEvent _transmitBufferSpaceFreedEvent = new AutoResetEvent(false);

        enum TcpStateMachineState : byte
        {
            CLOSED, /* connection closed */
            OPENING, /* sending SYN, initiating connection (in response to .Connect() or in response to an incoming SYN on a LISTENING socket) */
            LISTEN, /* listening for incoming SYN */
            SYN_SENT, /* SYN sent, waiting for incoming SYN for other direction */
            ESTABLISHED, /* SYN sent and acknowledged; incoming SYN received */
            CLOSING, /* FIN received or sent, shutting down connection */
            /* NOTE: currently, TIME_WAIT state is automatically moved to CLOSED state once the receive buffer has been completely read by the client;
             *       in the future we should consider keeping the endpoint open (although not necessarily occupying a valuable sockets slot) until 2MSL has expired */
            TIME_WAIT, /* after the connection is closed, wait 2MSL before enabling the socket endpoint pair to be reused */
            //FIN_SENT, /* FIN Sent, waiting for incoming FIN for other direction */
        }
        Thread _tcpStateMachineThread;
        AutoResetEvent _tcpStateMachine_ActionRequiredEvent = new AutoResetEvent(false);
        TcpStateMachineState _tcpStateMachine_CurrentState = TcpStateMachineState.CLOSED;
        // state machine data, stored by the OnPacketReceived function and processed by the state machine
        bool _tcpStateMachine_SendAckRequired = false; /* NOTE: this flag will force the sending of an ACK--even if there is no data to send.  All packets other than the initial SYN packet contain ACKs. */

        enum TransmissionType
        {
            None,
            SYN,
            FIN,
            Data
        }
        struct TransmissionDetails
        {
            public TransmissionType TransmissionType;
            public Int32 TransmissionAttemptsCounter;
            public Int32 CurrentRetryTimeoutInMilliseconds;
            public Int64 CurrentRetryTimeoutInMachineTicks;
            public Int64 MaximumTimeoutInMachineTicks;

            public TransmissionDetails(TransmissionType transmissionType)
            {
                this.TransmissionType = transmissionType;
                this.TransmissionAttemptsCounter = 0;
                this.CurrentRetryTimeoutInMilliseconds = 0;
                this.CurrentRetryTimeoutInMachineTicks = Int64.MaxValue;
                this.MaximumTimeoutInMachineTicks = Int64.MaxValue;
            }
        }
        TransmissionDetails _currentOutgoingTransmission = new TransmissionDetails(TransmissionType.None);
        object _currentOutgoingTransmissionLock = new object();
        Int32 _tcpStateMachine_CalculatedRetryTimeoutInMilliseconds = 210;

        const Int32 SYN_INITIAL_RETRY_TIMEOUT_IN_MS = 3000;
        const Int32 SYN_MAX_RETRY_TIMEOUT_IN_MS = 180000;

        const Int32 DATA_MAX_RETRY_TIMEOUT_IN_MS = 64000;

        System.Collections.ArrayList _incomingConnectionSockets = new System.Collections.ArrayList();
        Int32 _incomingConnectionBacklog = 0;

        [Flags] /* NOTE: these flags are or'd to indicate a live bi-directional connection */
        enum ConnectionStateFlags
        {
            None = 0,
            ConnectedToDestination = 1,
            ConnectedFromDestination = 2,
        }
        ConnectionStateFlags _connectionState = ConnectionStateFlags.None;
        AutoResetEvent _outgoingConnectionCompleteEvent = new AutoResetEvent(false);
        AutoResetEvent _outgoingConnectionClosedEvent = new AutoResetEvent(false);

        AutoResetEvent _acceptSocketWaitHandle = new AutoResetEvent(false);

        internal delegate void ConnectedEventHandler(object sender);
        internal event ConnectedEventHandler ConnectionComplete;
        internal event ConnectedEventHandler ConnectionAborted;

        public TcpSocket(TcpHandler tcpHandler, int handle)
            : base(handle)
        {
            // save a reference to our TcpHandler; we'll use this to send TCP segments
            _tcpHandler = tcpHandler;

            base._protocolType = IPv4Layer.ProtocolType.Tcp;

            // create our receive buffer
            _receiveBuffer = new byte[_receiveBufferSize];

            // create our transmit buffer
            _transmitBuffer = new byte[_transmitBufferSize];

            // create TCP state machine thread
            _tcpStateMachineThread = new Thread(TcpStateMachine);
            _tcpStateMachineThread.Start();
        }

        public override void Dispose()
        {
            base.Dispose();

            // shut down our state machine thread
            if (_tcpStateMachine_ActionRequiredEvent != null)
            {
                _tcpStateMachine_ActionRequiredEvent.Set();
                _tcpStateMachine_ActionRequiredEvent = null;
            }

            if (_outgoingConnectionCompleteEvent != null)
            {
                _outgoingConnectionCompleteEvent.Set();
                _outgoingConnectionCompleteEvent = null;
            }

            _incomingConnectionSockets = null;

            _currentOutgoingTransmission = new TransmissionDetails(TransmissionType.None);
            _currentOutgoingTransmissionLock = null;

            _receiveBuffer = null;
            _receiveBufferLockObject = null;
            if (_receiveBufferSpaceFilledEvent != null) _receiveBufferSpaceFilledEvent.Set();
            _receiveBufferSpaceFilledEvent = null;

            _transmitBuffer = null;
            _transmitBufferLockObject = null;
            if (_transmitBufferSpaceFreedEvent != null) _transmitBufferSpaceFreedEvent.Set();
            _transmitBufferSpaceFreedEvent = null;

            _randomGenerator = null;

            _tcpHandler = null;
        }

        public bool IsListening
        {
            get
            {
                return (_tcpStateMachine_CurrentState == TcpStateMachineState.LISTEN);
            }
        }

        public bool IsConnected
        {
            get
            {
                return (((_connectionState & ConnectionStateFlags.ConnectedFromDestination) != 0) &&
                    ((_connectionState & ConnectionStateFlags.ConnectedToDestination) != 0));
            }
        }

        internal void JoinIncomingConnection(UInt32 localIPAddress, UInt16 localIPPort, UInt32 destinationIPAddress, UInt16 destinationIPPort, UInt32 sequenceNumber, UInt16 windowSize)
        {
            if (_sourceIpAddressAndPortAssigned)
                throw Utility.NewSocketException(SocketError.SocketError); /* is this correct; should we throw an exception if we already have an IP address/port assigned? */

            _sourceIpAddressAndPortAssigned = true;

            _srcIPAddress = localIPAddress;
            _srcIPPort = localIPPort;
            _destIPAddress = destinationIPAddress;
            _destIPPort = destinationIPPort;

            _receiveWindowLeftEdge = sequenceNumber + 1;
            _receiveWindowRightEdge = (UInt32)(_receiveWindowLeftEdge + _receiveBuffer.Length);

            //_transmitWindowLeftEdge = acknowledgementNumber;
            //_transmitWindowRightEdge = _transmitWindowLeftEdge + windowSize;

            _tcpStateMachine_CurrentState = TcpStateMachineState.OPENING;
            _connectionState = ConnectionStateFlags.ConnectedFromDestination;
            
            _tcpStateMachine_SendAckRequired = true;

            _tcpStateMachine_ActionRequiredEvent.Set();
        }

        public override Socket Accept()
        {
            if (_tcpStateMachine_CurrentState != TcpStateMachineState.LISTEN)
                return null;

            while (true)
            {
                _acceptSocketWaitHandle.WaitOne();

                if (_isDisposed) return null;

                for (int iSocket = 0; iSocket < _incomingConnectionSockets.Count; iSocket++)
                {
                    if ((_incomingConnectionSockets[iSocket] != null) && ((TcpSocket)_incomingConnectionSockets[iSocket]).IsConnected)
                    {
                        Socket acceptedSocket = (Socket)_incomingConnectionSockets[iSocket];
                        _incomingConnectionSockets.Remove(acceptedSocket);
                        return acceptedSocket;
                    }
                }
            }
        }

        public override void Bind(UInt32 ipAddress, UInt16 ipPort)
        {
            if (_connectionState != 0)
                throw Utility.NewSocketException(SocketError.IsConnected);

            if (_sourceIpAddressAndPortAssigned)
                throw Utility.NewSocketException(SocketError.SocketError); /* is this correct; should we throw an exception if we already have an IP address/port assigned? */

            _sourceIpAddressAndPortAssigned = true;

            // if ipAddress is IP_ADDRESS_ANY, then change it to to our actual ipAddress.
            if (ipAddress == IP_ADDRESS_ANY)
                ipAddress = _tcpHandler.IPv4Layer.IPAddress;

            if (ipPort == IP_PORT_ANY)
                ipPort = _tcpHandler.IPv4Layer.GetNextEphemeralPortNumber(IPv4Layer.ProtocolType.Tcp) /* next available ephemeral port # */;

            // verify that this source IP address is correct
            if (ipAddress != _tcpHandler.IPv4Layer.IPAddress)
                throw Utility.NewSocketException(SocketError.AddressNotAvailable); /* address invalid */

            /* ensure that no other TcpSockets are bound to this address/port */
            for (int iSocketHandle = 0; iSocketHandle < IPv4Layer.MAX_SIMULTANEOUS_SOCKETS; iSocketHandle++)
            {
                Socket socket = _tcpHandler.IPv4Layer.GetSocket(iSocketHandle);
                if (socket != null &&
                    socket.ProtocolType == IPv4Layer.ProtocolType.Tcp &&
                    socket.SourceIPAddress == ipAddress &&
                    socket.SourceIPPort == ipPort)
                {
                    throw Utility.NewSocketException(SocketError.AddressAlreadyInUse); /* find a better exception for "cannot bind to already-bound port" */
                }
            }

            _srcIPAddress = ipAddress;
            _srcIPPort = ipPort;
        }

        public override void Connect(UInt32 ipAddress, UInt16 ipPort)
        {
            /* TODO: if the client wishes to reuse a socket object to connect a different server than before while TIME_WAIT's 2MSL have not yet expired,
             *       we may want to support that scenario here--possibly by caching the TIME_WAIT state and endpoint pairs in our TcpHandler so it can handle the TIME_WAIT state. */
            if (_tcpStateMachine_CurrentState != TcpStateMachineState.CLOSED)
                throw Utility.NewSocketException(SocketError.IsConnected);

            if (!_sourceIpAddressAndPortAssigned)
                Bind(IP_ADDRESS_ANY, IP_PORT_ANY);

            _destIPAddress = ipAddress;
            _destIPPort = ipPort;

            try
            {
                _tcpStateMachine_CurrentState = TcpStateMachineState.OPENING;
                _tcpStateMachine_ActionRequiredEvent.Set();
                _outgoingConnectionCompleteEvent.Reset();
                // wait for our connection to be established
                _outgoingConnectionCompleteEvent.WaitOne(); /* TODO: implement timeout here */

                if (((_connectionState & ConnectionStateFlags.ConnectedFromDestination) != 0) &&
                    ((_connectionState & ConnectionStateFlags.ConnectedToDestination) != 0))
                {
                    return; // success!
                }
                else
                {
                    throw Utility.NewSocketException(SocketError.TimedOut);
                }
            }
            catch
            {
                _tcpStateMachine_CurrentState = TcpStateMachineState.CLOSED;
            }
        }

        public override void Close()
        {
            // if we were waiting for the client code to read our last-received data, go ahead and move to closed state now.
            if (_tcpStateMachine_CurrentState == TcpStateMachineState.TIME_WAIT)
            {
                _tcpStateMachine_CurrentState = TcpStateMachineState.CLOSED;
                if (_outgoingConnectionClosedEvent != null) _outgoingConnectionClosedEvent.Set();
                _tcpStateMachine_ActionRequiredEvent.Set();
            }
            
            if (_tcpStateMachine_CurrentState == TcpStateMachineState.CLOSED)
                return;

            if ((_outgoingConnectionClosedEvent != null) && (_tcpStateMachine_CurrentState != TcpStateMachineState.CLOSING))
                _outgoingConnectionClosedEvent.Reset();

            _tcpStateMachine_CurrentState = TcpStateMachineState.CLOSING;
            _tcpStateMachine_ActionRequiredEvent.Set();

            if (_outgoingConnectionClosedEvent != null)
                _outgoingConnectionClosedEvent.WaitOne();
        }

        public override void Listen(int backlog)
        {
            if (_tcpStateMachine_CurrentState != TcpStateMachineState.CLOSED)
                throw Utility.NewSocketException(SocketError.IsConnected); 

            if (!_sourceIpAddressAndPortAssigned)
                throw Utility.NewSocketException(SocketError.SocketError); /* "must be bound to address/port before .listen() can be called" */

            // move to the listening  state
            _tcpStateMachine_CurrentState = TcpStateMachineState.LISTEN;
            _tcpStateMachine_ActionRequiredEvent.Set();
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

                        /* return true if connection has been closed, reset, or terminated */
                        if (_connectionState == 0)
                            return true;

                        /* TODO: only check _isDisposed if the connection hasn't been closed/reset/terminated; those other cases should return TRUE */
                        if (_isDisposed) return false;

                        // if there is no data available and our connection is not being shut down, wait until data is available
                        if (GetReceiveBufferBytesFilled() == 0)
                        {
                            _receiveBufferSpaceFilledEvent.Reset(); // ensure that the "buffer filled" event is not triggered before checking our buffer fill state (so that waiting on the event doesn't falsely trigger
                            _receiveBufferSpaceFilledEvent.WaitOne(microSeconds / 1000, false);
                        }

                        if (_receiveBufferNextBytePos != _receiveBufferFirstBytePos)
                            return true;

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

                        return ((_connectionState & ConnectionStateFlags.ConnectedToDestination) > 0);
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

        object _sendFunctionSynchronizationLockObject = new object();

        public override int Send(byte[] buffer, Int32 offset, Int32 count, Int32 flags, Int64 timeoutInMachineTicks)
        {
            if (_tcpStateMachine_CurrentState != TcpStateMachineState.ESTABLISHED)
                return 0;

            Int32 bytesSent = 0;
            lock (_sendFunctionSynchronizationLockObject)
            {
                Int32 subOffset = 0;
                while (bytesSent < count)
                {
                    Int32 bytesAvailable = GetTransmitBufferBytesFree();
                    Int32 bytesToSend = System.Math.Min(count, bytesAvailable);

                    lock (_transmitBufferLockObject)
                    {
                        Int32 bytesToWriteBeforeWrap = (Int32)System.Math.Min(bytesToSend, _transmitBuffer.Length - _transmitBufferNextBytePos);
                        Array.Copy(buffer, offset + subOffset, _transmitBuffer, (Int32)_transmitBufferNextBytePos, bytesToWriteBeforeWrap);
                        _transmitBufferNextBytePos = (UInt32)((_transmitBufferNextBytePos + bytesToWriteBeforeWrap) % _transmitBuffer.Length);
                        subOffset += bytesToWriteBeforeWrap;

                        // if we wrapped all the way around, write the remaining data.
                        if (bytesToWriteBeforeWrap < bytesToSend)
                        {
                            Array.Copy(buffer, offset + subOffset, _transmitBuffer, (Int32)_transmitBufferNextBytePos, bytesToSend - bytesToWriteBeforeWrap);
                            _transmitBufferNextBytePos += (UInt32)(bytesToSend - bytesToWriteBeforeWrap);
                        }
                    }

                    bytesSent += bytesToSend;
                    _tcpStateMachine_ActionRequiredEvent.Set();

                    // if we could nto send all of our data to the buffer, block until there is more space available.
                    Int32 timeoutMilliseconds = (timeoutInMachineTicks == Int64.MaxValue ? Timeout.Infinite : (Int32)((timeoutInMachineTicks - Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks) / TimeSpan.TicksPerMillisecond));
                    if (bytesSent < count)
                    {
                        bool spaceFreed = _transmitBufferSpaceFreedEvent.WaitOne(timeoutMilliseconds, false);

                        /* if we ran out of time, abort. */
                        if (!spaceFreed)
                            throw Utility.NewSocketException(SocketError.SocketError); /* NOTE: should we throw a TimeoutException? */
                    }
                }
            }

            return bytesSent;
        }

        /* NOTE: for connection-based protocols like TCP, this method ignores the ipAddress and ipPort parameters */
        public override Int32 SendTo(byte[] buffer, Int32 offset, Int32 count, Int32 flags, Int64 timeoutInMachineTicks, UInt32 ipAddress, UInt16 ipPort)
        {
            return Send(buffer, offset, count, flags, timeoutInMachineTicks);
        }

        public override Int32 Receive(byte[] buffer, Int32 offset, Int32 count, Int32 flags, Int64 timeoutInMachineTicks)
        {
            switch (_tcpStateMachine_CurrentState)
            {
                case TcpStateMachineState.ESTABLISHED:
                    {
                        // if there is no data available and our connection is not being shut down, wait until data is available
                        _receiveBufferSpaceFilledEvent.Reset(); // ensure that the "buffer filled" event is not triggered before checking our buffer fill state (so that waiting on the event doesn't falsely trigger
                        if (GetReceiveBufferBytesFilled() == 0)
                        {
                            Int32 timeoutMilliseconds = (timeoutInMachineTicks == Int64.MaxValue ? Timeout.Infinite : (Int32)((timeoutInMachineTicks - Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks) / TimeSpan.TicksPerMillisecond));
                            _receiveBufferSpaceFilledEvent.WaitOne(timeoutMilliseconds, false);
                        }
                    }
                    break;
                case TcpStateMachineState.CLOSING:
                case TcpStateMachineState.TIME_WAIT:
                    break;
                default:
                    return 0; /* if we are not in a valid state to receive data, return 0 bytes as the available data length. */
            }

            bool receiveWindowWasFull;
            bool receiveWindowRemainingWasLessThanButIsNowGreaterThanSegmentSize = false;

            Int32 bytesRead = 0;
            lock (_receiveBufferLockObject)
            {
                UInt16 receiveBufferBytesFilled = GetReceiveBufferBytesFilled();
                Int32 bytesToRead = System.Math.Min(receiveBufferBytesFilled, count);
                Int32 receiveBufferSize = _receiveBuffer.Length - 1; /* NOTE: one byte of teh actual buffer is unused, so the usable size is one less than the length */
                Int32 receiveBufferBytesRemaining = receiveBufferSize - receiveBufferBytesFilled;

                receiveWindowWasFull = (bytesToRead == receiveBufferSize);
                receiveWindowRemainingWasLessThanButIsNowGreaterThanSegmentSize = (
                    (receiveBufferBytesRemaining < _receiveWindowMaximumSegmentSize) &&
                    ((receiveBufferBytesRemaining + bytesToRead) >= _receiveWindowMaximumSegmentSize)
                    );

                Int32 bytesToReadBeforeWrap = (Int32)System.Math.Min(bytesToRead, _receiveBuffer.Length - _receiveBufferFirstBytePos);
                Array.Copy(_receiveBuffer, (Int32)_receiveBufferFirstBytePos, buffer, offset, bytesToReadBeforeWrap);
                _receiveBufferFirstBytePos = (UInt32)((_receiveBufferFirstBytePos + bytesToReadBeforeWrap) % _receiveBuffer.Length);
                bytesRead += bytesToReadBeforeWrap;

                if ((_receiveBufferFirstBytePos == 0) && (bytesToReadBeforeWrap < bytesToRead))
                {
                    Array.Copy(_receiveBuffer, (Int32)_receiveBufferFirstBytePos, buffer, offset + bytesToReadBeforeWrap, bytesToRead - bytesToReadBeforeWrap);
                    _receiveBufferFirstBytePos = (UInt32)((_receiveBufferFirstBytePos + (bytesToRead - bytesToReadBeforeWrap)) % _receiveBuffer.Length);
                    bytesRead += (bytesToRead - bytesToReadBeforeWrap);
                }
            }

            /* if window size was zero, but is no longer zero, then send a window update to the destination host (via an ACK) */
            if (receiveWindowWasFull)
            {
                _tcpStateMachine_SendAckRequired = true;
                _tcpStateMachine_ActionRequiredEvent.Set();
            }

            /* if we window size was smaller than our minimum segemnt size, and is now larger than our minimum segment size, then send a window update to the destination host (via an ACK) */
            if (receiveWindowRemainingWasLessThanButIsNowGreaterThanSegmentSize)
            {
                _tcpStateMachine_SendAckRequired = true;
                _tcpStateMachine_ActionRequiredEvent.Set();
            }

            /* if we were in TIME_WAIT state and the caller requested our last data, move to CLOSED state now. */
            if (_tcpStateMachine_CurrentState == TcpStateMachineState.TIME_WAIT && GetReceiveBufferBytesFilled() == 0)
            {
                _tcpStateMachine_CurrentState = TcpStateMachineState.CLOSED;
                if (_outgoingConnectionClosedEvent != null) _outgoingConnectionClosedEvent.Set();
                _tcpStateMachine_ActionRequiredEvent.Set();
            }

            return bytesRead;
        }

        public override Int32 ReceiveFrom(byte[] buffer, Int32 offset, Int32 count, Int32 flags, Int64 timeoutInMachineTicks, out UInt32 ipAddress, out UInt16 ipPort)
        {
            ipAddress = _destIPAddress;
            ipPort = _destIPPort;

            return Receive(buffer, offset, count, flags, timeoutInMachineTicks);
        }

        /* NOTE: this function processes incoming frames and stores flags/data; the TcpStateMachine function processes stored flags, advances the state machine as necessary, and sends out responses */
        internal override void OnPacketReceived(UInt32 sourceIPAddress, UInt32 destinationIPAddress, byte[] buffer, Int32 index, Int32 count)
        {
            if (_isDisposed)
                return;

            bool triggerStateMachine = false; /* we set this to true if the state machine needs to process our data */

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

            Int32 headerLength = (((Int32)buffer[index + 12]) >> 4) * 4;

            bool flagFin = ((buffer[index + 13] & (1 << 0)) > 0);
            bool flagSyn = ((buffer[index + 13] & (1 << 1)) > 0);
            bool flagRst = ((buffer[index + 13] & (1 << 2)) > 0);
            bool flagPsh = ((buffer[index + 13] & (1 << 3)) > 0);
            bool flagAck = ((buffer[index + 13] & (1 << 4)) > 0);
            bool flagUrg = ((buffer[index + 13] & (1 << 5)) > 0);

            UInt16 windowSize = (UInt16)(
                (((UInt16)buffer[index + 14]) << 8) +
                ((UInt16)buffer[index + 15]));

            /* TODO: process header options */
            if (headerLength > TcpHandler.TCP_HEADER_MIN_LENGTH)
            {
                bool endOfListProcessed = false;
                byte optionLength = 0;

                int headerBufferPos = TcpHandler.TCP_HEADER_MIN_LENGTH;
                while (!endOfListProcessed && (headerBufferPos < headerLength))
                {
                    byte optionKind;
                    optionKind = buffer[headerBufferPos];

                    if (optionKind == 0 /* EOL = end of option list */)
                    {
                        endOfListProcessed = true;
                        optionLength = 1;
                    }
                    else if (optionKind == 1 /* NOP = no operation; used for padding */)
                    {
                        optionLength = 1;
                    }
                    else
                    {
                        if (headerBufferPos < headerLength - 1)
                            optionLength = (byte)(buffer[headerBufferPos + 1]);
                        else
                            optionLength = 1; /* backup in case other option length/list is corrupted */

                        /* interpret known TCP options */
                        switch (buffer[headerBufferPos])
                        {
                            case 2: /* MSS = Maximum Segment Size */
                                {
                                    if (optionLength == 4)
                                    {
                                        _transmitWindowMaximumSegmentSize = (UInt16)(
                                            (((UInt16)buffer[headerBufferPos + 2]) << 8) +
                                            buffer[headerBufferPos + 3]
                                            );
                                    }
                                }
                                break;
                            default:
                                break;
                        }
                    }

                    headerBufferPos += optionLength;
                }
            }

            /* recalculate transmit window size */
            /* NOTE: in edge cases, the transmit window could actually shrink.  if that happens, we need to pull in our "sent but not acknowledged" marker as well */
            if (_transmitWindowSentButNotAcknowledgedMarker > _transmitWindowLeftEdge + windowSize)
                _transmitWindowSentButNotAcknowledgedMarker = _transmitWindowLeftEdge + windowSize;
            _transmitWindowRightEdge = _transmitWindowLeftEdge + windowSize;

            /* if we receive a reset frame, close down our socket */
            if (flagRst && (acknowledgmentNumber >= _transmitWindowLeftEdge) && (acknowledgmentNumber <= (_transmitWindowRightEdge + 1)))
            {
                // close down our socket
                _connectionState = ConnectionStateFlags.None;
                if (_tcpStateMachine_CurrentState != TcpStateMachineState.CLOSING)
                {
                    /* TODO: clear our transmit buffers, notify user, etc. */
                    if (_receiveBufferSpaceFilledEvent != null) _receiveBufferSpaceFilledEvent.Set();
                }
                if (_outgoingConnectionClosedEvent != null) _outgoingConnectionClosedEvent.Set();
                _tcpStateMachine_CurrentState = TcpStateMachineState.CLOSED;
                triggerStateMachine = true;
            }

            // if we received a SYN and are in a valid state, store it and the corresponding sequence number now.
            if (flagSyn)
            {
                switch (_tcpStateMachine_CurrentState)
                {
                    case TcpStateMachineState.OPENING: /* special case of simultaneous connection request from both sides */
                    case TcpStateMachineState.SYN_SENT:
                        {
                            _receiveWindowLeftEdge = sequenceNumber + 1;
                            _receiveWindowRightEdge = (UInt32)(_receiveWindowLeftEdge + _receiveBuffer.Length);
                            _connectionState |= ConnectionStateFlags.ConnectedFromDestination;
                            _tcpStateMachine_SendAckRequired = true;
                            triggerStateMachine = true;
                        }
                        break;
                    case TcpStateMachineState.LISTEN:
                        {
                            try
                            {
                                if (_incomingConnectionBacklog + 1 > _incomingConnectionSockets.Count)
                                {
                                    Int32 newIncomingSocketHandle = _tcpHandler.IPv4Layer.CreateSocket(IPv4Layer.ProtocolType.Tcp, Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks, false);
                                    TcpSocket newIncomingSocket = (TcpSocket)_tcpHandler.IPv4Layer.GetSocket(newIncomingSocketHandle);
                                    UInt16 sourceIPPort = (UInt16)((((UInt16)buffer[index + 0]) << 8) + buffer[index + 1]);
                                    newIncomingSocket.ConnectionComplete += _incomingConnectionSockets_ConnectionComplete;
                                    newIncomingSocket.ConnectionAborted += _incomingConnectionSockets_ConnectionAborted;
                                    newIncomingSocket.JoinIncomingConnection(_srcIPAddress, _srcIPPort, sourceIPAddress, sourceIPPort, sequenceNumber, windowSize);

                                    _incomingConnectionSockets.Add(newIncomingSocket);
                                    _incomingConnectionBacklog++;
                                }
                                else
                                {
                                    // if our backlog is already full, discard the frame
                                    return;
                                }
                            }
                            catch
                            {
                                // if we could not create the incoming socket, discard the frame.
                                return;
                            }
                        }
                        break;
                    case TcpStateMachineState.ESTABLISHED:
                        {
                            // we will drop this packet to avoid connection takeover attacks, but we will still ACK the message in case the destination host's original SYN packet was lost.
                            /* TODO: ideally, we would only send an ACK in the scenario that we have not received any TCP stream data yet */
                            _tcpStateMachine_SendAckRequired = true;
                            triggerStateMachine = true;
                        }
                        break;
                    default:
                        // we are not in a valid state for an incoming connection request: drop this packet.
                        return;
                }
            }

            Int32 payloadLength = count - headerLength;

            /* if some of our data has been acknowledged, process the ACK and move the transmit window forward */
            if (flagAck && (acknowledgmentNumber > _transmitWindowLeftEdge) && (acknowledgmentNumber <= _transmitWindowSentButNotAcknowledgedMarker))
            {
                if (_currentOutgoingTransmission.TransmissionType == TransmissionType.SYN)
                {
                    _transmitWindowLeftEdge++;
                    _currentOutgoingTransmission = new TransmissionDetails(TransmissionType.None);
                    triggerStateMachine = true;
                }
                else
                {
                    // move our transmit buffer pointer forward
                    UInt32 bytesAcknowledged = (UInt32)((((UInt64)(acknowledgmentNumber - _transmitWindowLeftEdge)) + UInt32.MaxValue) % UInt32.MaxValue);
                    _transmitBufferFirstBytePos = (UInt32)((((UInt64)_transmitBufferFirstBytePos) + bytesAcknowledged) % UInt32.MaxValue);

                    // move our transmit window left edge forward
                    _transmitWindowLeftEdge = acknowledgmentNumber;
                    if (_transmitWindowLeftEdge == _transmitWindowSentButNotAcknowledgedMarker)
                    {
                        // all transmitted has been ACK'd, so abort our retry timer and send more data
                        _currentOutgoingTransmission = new TransmissionDetails(TransmissionType.None);
                    }
                    _transmitBufferSpaceFreedEvent.Set();
                    triggerStateMachine = true;
                }
                _tcpStateMachine_SendAckRequired = true;
            }

            if (_currentOutgoingTransmission.TransmissionType == TransmissionType.FIN)
            {
                if (acknowledgmentNumber == _transmitWindowLeftEdge + 1)
                {
                    _transmitWindowLeftEdge++;
                    _currentOutgoingTransmission = new TransmissionDetails(TransmissionType.None);
                    _connectionState &= ~ConnectionStateFlags.ConnectedToDestination;
                    triggerStateMachine = true;
                }
            }
                 
            Int32 receiveBufferBytesAvailable = GetReceiveBufferBytesFree();
            if (
                ((_tcpStateMachine_CurrentState == TcpStateMachineState.ESTABLISHED) || (_tcpStateMachine_CurrentState == TcpStateMachineState.CLOSING) || (_tcpStateMachine_CurrentState == TcpStateMachineState.TIME_WAIT))
                && (payloadLength > 0) && (receiveBufferBytesAvailable >= payloadLength))
            {
                if ((_connectionState & ConnectionStateFlags.ConnectedFromDestination) != 0)
                {
                    if (_receiveWindowLeftEdge == sequenceNumber)
                    {
                        // we have received data; store it as long as we have room for it.
                        lock (_receiveBufferLockObject)
                        {
                            Int32 numBytesStoredBeforeWrap = System.Math.Min((Int32)(_receiveBufferSize - _receiveBufferNextBytePos), (Int32)payloadLength);
                            Array.Copy(buffer, index + headerLength, _receiveBuffer, (Int32)_receiveBufferNextBytePos, numBytesStoredBeforeWrap);
                            _receiveBufferNextBytePos += (UInt32)numBytesStoredBeforeWrap;
                            if (_receiveBufferNextBytePos == _receiveBufferSize) _receiveBufferNextBytePos = 0; // handle wrap-around

                            // if we wrapped around, store the remaining bytes
                            if (numBytesStoredBeforeWrap < payloadLength)
                            {
                                Array.Copy(buffer, index + headerLength + numBytesStoredBeforeWrap, _receiveBuffer, (Int32)_receiveBufferNextBytePos, payloadLength - numBytesStoredBeforeWrap);
                                _receiveBufferNextBytePos += (UInt32)(payloadLength - numBytesStoredBeforeWrap);
                            }
                        }
                        _receiveWindowLeftEdge = (UInt32)(((UInt64)_receiveWindowLeftEdge + (UInt64)payloadLength) % UInt32.MaxValue); /* TODO: handle _receiveWindowScale here */
                        _receiveBufferSpaceFilledEvent.Set();
                        _tcpStateMachine_SendAckRequired = true;
                        triggerStateMachine = true;
                    }
                    else
                    {
                        // out of order packet; send an ACK so that the destination host knows what segment data we are expecting
                        /* TODO: we should really have an "out of order/duplicate packet counter" here, and only send a dummy ACK when that counter meets its upper threshold */
                        _tcpStateMachine_SendAckRequired = true;
                        triggerStateMachine = true;
                    }
                }
            }

            // if we received a FIN and are in a valid state, store it and the corresponding sequence number now.
            if (flagFin && (acknowledgmentNumber >= _transmitWindowLeftEdge) && (acknowledgmentNumber <= (_transmitWindowRightEdge + 1)))
            {
                switch (_tcpStateMachine_CurrentState)
                {
                    case TcpStateMachineState.ESTABLISHED:
                    case TcpStateMachineState.CLOSING:
                        {
                            _receiveWindowLeftEdge = (UInt32)(sequenceNumber + payloadLength + 1);
                            _receiveWindowRightEdge = _receiveWindowLeftEdge;
                            _connectionState &= ~ConnectionStateFlags.ConnectedFromDestination;
                            _tcpStateMachine_SendAckRequired = true;
                            // if we weren't already closing the connection from our end, make sure that our TCP socket is now in closing state.
                            _tcpStateMachine_CurrentState = TcpStateMachineState.CLOSING;

                            triggerStateMachine = true;
                        }
                        break;
                    default:
                        // we are not in a valid state for an incoming close request: drop this packet.
                        break;
                }
            }

            if (triggerStateMachine)
                _tcpStateMachine_ActionRequiredEvent.Set();
        }

        void _incomingConnectionSockets_ConnectionComplete(object sender)
        {
            if (_acceptSocketWaitHandle != null)
                _acceptSocketWaitHandle.Set();
        }

        void _incomingConnectionSockets_ConnectionAborted(object sender)
        {
            _tcpHandler.IPv4Layer.CloseSocket(((TcpSocket)sender).Handle);
        }

        /* NOTE: this function advances the state machine as necessary and sends out responses as necessary; the OnPacketReceived function processes incoming frames and stores the flags/data */
        void TcpStateMachine()
        {
            while (true)
            {
                // wait for a change which requires the TCP state machine to change state or process data
                Int32 millisecondsTimeout = System.Math.Max(0, (Int32)(((_currentOutgoingTransmission.TransmissionType == TransmissionType.None) || (_currentOutgoingTransmission.CurrentRetryTimeoutInMachineTicks == Int64.MaxValue)) ? Timeout.Infinite : (_currentOutgoingTransmission.CurrentRetryTimeoutInMachineTicks - Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks) / TimeSpan.TicksPerMillisecond));
                _tcpStateMachine_ActionRequiredEvent.WaitOne(millisecondsTimeout, false);

                if (_isDisposed)
                    return;

                switch (_tcpStateMachine_CurrentState)
                {
                    case TcpStateMachineState.CLOSED:
                        {
                            /* there is nothing to be done if our socket is CLOSED */
                        }
                        break;
                    case TcpStateMachineState.OPENING:
                        {
                            // if our app has requested an outgoing connection, try connecting now.
                            // set the initial sequence number
                            UInt32 initialSequenceNumber = (UInt32)(_randomGenerator.Next() + _randomGenerator.Next()); /* initial sequence number */
                            _transmitWindowLeftEdge = initialSequenceNumber;
                            _transmitWindowSentButNotAcknowledgedMarker = _transmitWindowLeftEdge + 1;
                            _transmitWindowRightEdge = _transmitWindowLeftEdge + _transmitWindowMaximumSegmentSize;

                            Int64 currentTimeInMachineTicks = Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks;
                            // move the state machine forward to "SYN_SENT" so that it can respond to an incoming SYN + ACK message
                            _tcpStateMachine_CurrentState = TcpStateMachineState.SYN_SENT;

                            lock (_currentOutgoingTransmissionLock)
                            {
                                // set our outstanding transmission type to SYN (which flag occupies a sequence #)
                                _currentOutgoingTransmission = new TransmissionDetails(TransmissionType.SYN);
                                _currentOutgoingTransmission.TransmissionAttemptsCounter = 1;
                                _currentOutgoingTransmission.CurrentRetryTimeoutInMilliseconds = SYN_INITIAL_RETRY_TIMEOUT_IN_MS;
                                _currentOutgoingTransmission.CurrentRetryTimeoutInMachineTicks = currentTimeInMachineTicks + (_currentOutgoingTransmission.CurrentRetryTimeoutInMilliseconds * TimeSpan.TicksPerMillisecond);
                                _currentOutgoingTransmission.MaximumTimeoutInMachineTicks = currentTimeInMachineTicks + (SYN_MAX_RETRY_TIMEOUT_IN_MS * TimeSpan.TicksPerMillisecond);
                            }

                            _tcpHandler.SendTcpSegment(_srcIPAddress, _destIPAddress, _srcIPPort, _destIPPort, _transmitWindowLeftEdge, _receiveWindowLeftEdge, GetReceiveBufferBytesFree(), 
                                ((_connectionState & ConnectionStateFlags.ConnectedFromDestination) > 0),
                                false, false, true, false,
                                new TcpHandler.TcpOption[] { new TcpHandler.TcpOption(2 /* Maximum Segment Size */, new byte[] { (byte)((_receiveWindowMaximumSegmentSize >> 8) & 0xFF), (byte)(_receiveWindowMaximumSegmentSize & 0xFF) }) }, 
                                new byte[] { }, 0, 0, Int64.MaxValue); /* TODO: use timeout on sending segment */
                            _tcpStateMachine_SendAckRequired = false;
                        }
                        break;
                    case TcpStateMachineState.SYN_SENT:
                        {
                            if (_currentOutgoingTransmission.TransmissionType == TransmissionType.SYN)
                            {
                                // if we have not yet received an ACK for our SYN and our timeout has expired, resend it now.
                                Int64 currentTimeInMachineTicks = Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks;
                                if (currentTimeInMachineTicks > _currentOutgoingTransmission.CurrentRetryTimeoutInMachineTicks /* timeout occurred */)
                                {
                                    if (currentTimeInMachineTicks > _currentOutgoingTransmission.MaximumTimeoutInMachineTicks)
                                    {
                                        /* TODO: abort connection attempt */
                                        /* TODO: should we send a RESET segment? */
                                        _connectionState = ConnectionStateFlags.None;
                                        _outgoingConnectionCompleteEvent.Set();
                                        /* raise our connection aborted event; this is used to notify a Listener socket that our connection has been successfully established */
                                        if (ConnectionAborted != null) ConnectionAborted(this);
                                    }
                                    else
                                    {
                                        _currentOutgoingTransmission.TransmissionAttemptsCounter += 1;
                                        _currentOutgoingTransmission.CurrentRetryTimeoutInMilliseconds *= 2;
                                        _currentOutgoingTransmission.CurrentRetryTimeoutInMachineTicks = currentTimeInMachineTicks + (_currentOutgoingTransmission.CurrentRetryTimeoutInMilliseconds * TimeSpan.TicksPerMillisecond);
                                        _tcpHandler.SendTcpSegment(_srcIPAddress, _destIPAddress, _srcIPPort, _destIPPort, _transmitWindowLeftEdge, _receiveWindowLeftEdge, GetReceiveBufferBytesFree(),
                                            ((_connectionState & ConnectionStateFlags.ConnectedFromDestination) > 0), 
                                            false, false, true, false,
                                            new TcpHandler.TcpOption[] { new TcpHandler.TcpOption(2 /* Maximum Segment Size */, new byte[] { (byte)((_receiveWindowMaximumSegmentSize >> 8) & 0xFF), (byte)(_receiveWindowMaximumSegmentSize & 0xFF) }) },
                                            new byte[] { }, 0, 0, Int64.MaxValue); /* TODO: enable timeout */
                                    }
                                }

                            }
                            else
                            {
                                // our SYN has been ack'd; make sure we've marked our outgoing connection as valid
                                _connectionState |= ConnectionStateFlags.ConnectedToDestination;
                            }

                            if (((_connectionState & ConnectionStateFlags.ConnectedFromDestination) != 0) &&
                                ((_connectionState & ConnectionStateFlags.ConnectedToDestination) != 0))
                            {
                                _tcpStateMachine_CurrentState = TcpStateMachineState.ESTABLISHED;
                                _outgoingConnectionCompleteEvent.Set();
                                /* raise our connected event; this is used to notify a Listener socket that our connection has been successfully established */
                                if (ConnectionComplete != null) ConnectionComplete(this);
                            }
                        }
                        break;
                    case TcpStateMachineState.ESTABLISHED:
                        {
                            Int32 transmitBufferFilledLength = GetTransmitBufferBytesFilled();
                            if (transmitBufferFilledLength == 0)
                                break;

                            // if we have data to send, first make sure we send the initial attempt.
                            if (_currentOutgoingTransmission.TransmissionType == TransmissionType.None)
                            {
                                Int64 currentTimeInMachineTicks = Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks;

                                lock (_currentOutgoingTransmissionLock)
                                {
                                    // set our outstanding transmission type to data 
                                    _currentOutgoingTransmission = new TransmissionDetails(TransmissionType.Data);
                                    _currentOutgoingTransmission.TransmissionAttemptsCounter = 1;
                                    _currentOutgoingTransmission.CurrentRetryTimeoutInMilliseconds = _tcpStateMachine_CalculatedRetryTimeoutInMilliseconds;
                                    _currentOutgoingTransmission.CurrentRetryTimeoutInMachineTicks = currentTimeInMachineTicks + (_currentOutgoingTransmission.CurrentRetryTimeoutInMilliseconds * TimeSpan.TicksPerMillisecond);
                                    _currentOutgoingTransmission.MaximumTimeoutInMachineTicks = currentTimeInMachineTicks + (((_transmitTimeoutInMilliseconds > 0) ? _transmitTimeoutInMilliseconds : DATA_MAX_RETRY_TIMEOUT_IN_MS) * TimeSpan.TicksPerMillisecond);
                                }

                                // calculate the number of bytes to send
                                Int32 transmitWindowWidth = (Int32)((((UInt64)_transmitWindowRightEdge + UInt32.MaxValue) - _transmitWindowLeftEdge) % UInt32.MaxValue);
                                Int32 totalBytesToSend = System.Math.Min(transmitWindowWidth, transmitBufferFilledLength);

                                /* NOTE: this code is copy-and-paste synchronized with the "send data" code in the "re-attempt" section below */
                                // max bytes per segment (_transmitWindowMaximumSegmentSize)
                                UInt32 absoluteWindowOffset = _transmitWindowLeftEdge;
                                Int32 absoluteBufferOffset = (Int32)_transmitBufferFirstBytePos;
                                Int32 relativeOffset = 0;
                                while (relativeOffset < totalBytesToSend)
                                {
                                    Int32 bytesToSendBeforeWrap = (Int32)System.Math.Min(_transmitBuffer.Length - absoluteBufferOffset, totalBytesToSend);
                                    Int32 currentBytesToSend = (Int32)System.Math.Min(bytesToSendBeforeWrap, _transmitWindowMaximumSegmentSize);
                                    _tcpHandler.SendTcpSegment(_srcIPAddress, _destIPAddress, _srcIPPort, _destIPPort, 
                                        (UInt32)absoluteWindowOffset, _receiveWindowLeftEdge, GetReceiveBufferBytesFree(), true, 
                                        (totalBytesToSend - relativeOffset == currentBytesToSend), false, false, false, null, 
                                        _transmitBuffer, absoluteBufferOffset, currentBytesToSend, Int64.MaxValue); /* TODO: use timeout on sending segment */

                                    relativeOffset += currentBytesToSend;
                                    absoluteBufferOffset = (absoluteBufferOffset + currentBytesToSend) % _transmitBuffer.Length;
                                    absoluteWindowOffset = (UInt32)((absoluteWindowOffset + currentBytesToSend) % UInt32.MaxValue);
                                }
                                _transmitWindowSentButNotAcknowledgedMarker = absoluteWindowOffset;

                                _tcpStateMachine_SendAckRequired = false;
                            }
                            else if (_currentOutgoingTransmission.TransmissionType == TransmissionType.Data)
                            {
                                // if timeout has occured, resend all previously-sent segments
                                Int64 currentTimeInMachineTicks = Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks;
                                if (currentTimeInMachineTicks > _currentOutgoingTransmission.CurrentRetryTimeoutInMachineTicks /* timeout occurred */)
                                {
                                    if (currentTimeInMachineTicks > _currentOutgoingTransmission.MaximumTimeoutInMachineTicks)
                                    {
                                        /* TODO: send RESET to destination host */
                                        /* abort transmission attempt and close socket */
                                        _connectionState = ConnectionStateFlags.None;
                                        _tcpStateMachine_CurrentState = TcpStateMachineState.CLOSED;
                                        if (_receiveBufferSpaceFilledEvent != null) _receiveBufferSpaceFilledEvent.Set(); 
                                        if (_outgoingConnectionClosedEvent != null) _outgoingConnectionClosedEvent.Set();
                                    }
                                    else
                                    {
                                        /* retry transmission attempt */
                                        _currentOutgoingTransmission.TransmissionAttemptsCounter += 1;
                                        _currentOutgoingTransmission.CurrentRetryTimeoutInMilliseconds *= 2;
                                        _currentOutgoingTransmission.CurrentRetryTimeoutInMachineTicks = currentTimeInMachineTicks + (_currentOutgoingTransmission.CurrentRetryTimeoutInMilliseconds * TimeSpan.TicksPerMillisecond);

                                        // calculate the number of bytes to send
                                        Int32 transmitWindowUsedWidth = (Int32)((((UInt64)_transmitWindowSentButNotAcknowledgedMarker + UInt32.MaxValue) - _transmitWindowLeftEdge) % UInt32.MaxValue);
                                        Int32 totalBytesToSend = System.Math.Min(transmitWindowUsedWidth, transmitBufferFilledLength);

                                        /* NOTE: this code is copy-and-paste synchronized with the "send data" code in the "initial attempt" section above */
                                        // max bytes per segment (_transmitWindowMaximumSegmentSize)
                                        UInt32 absoluteWindowOffset = _transmitWindowLeftEdge;
                                        Int32 absoluteBufferOffset = (Int32)_transmitBufferFirstBytePos;
                                        Int32 relativeOffset = 0;
                                        while (relativeOffset < totalBytesToSend)
                                        {
                                            Int32 bytesToSendBeforeWrap = (Int32)System.Math.Min(_transmitBuffer.Length - absoluteBufferOffset, totalBytesToSend);
                                            Int32 currentBytesToSend = (Int32)System.Math.Min(bytesToSendBeforeWrap, _transmitWindowMaximumSegmentSize);
                                            _tcpHandler.SendTcpSegment(_srcIPAddress, _destIPAddress, _srcIPPort, _destIPPort,
                                                (UInt32)absoluteWindowOffset, _receiveWindowLeftEdge, GetReceiveBufferBytesFree(), true,
                                                (totalBytesToSend - relativeOffset == currentBytesToSend), false, false, false, null,
                                                _transmitBuffer, absoluteBufferOffset, currentBytesToSend, Int64.MaxValue); /* TODO: use timeout on sending segment */

                                            relativeOffset += currentBytesToSend;
                                            absoluteBufferOffset = (absoluteBufferOffset + currentBytesToSend) % _transmitBuffer.Length;
                                            absoluteWindowOffset = (UInt32)((absoluteWindowOffset + currentBytesToSend) % UInt32.MaxValue);
                                        }
                                        _transmitWindowSentButNotAcknowledgedMarker = absoluteWindowOffset;

                                        _tcpStateMachine_SendAckRequired = false;
                                    }
                                }
                            }
                        }
                        break;
                    case TcpStateMachineState.CLOSING:
                        {
                            /* if we are no longer connected to or from the destination host, move to the CLOSED state. */
                            if (((_connectionState & ConnectionStateFlags.ConnectedToDestination) == 0) &&
                                ((_connectionState & ConnectionStateFlags.ConnectedFromDestination) == 0))
                            {
                                /* TODO: we should move to 2MSL state (blocking this endpoint pair from being used for 2 * MSL) */
                                if (_receiveBufferSpaceFilledEvent != null) _receiveBufferSpaceFilledEvent.Set();
                                if (_outgoingConnectionClosedEvent != null) _outgoingConnectionClosedEvent.Set();
                                break;
                            }

                            /* if we are still connected to the destination, send a FIN packet (or retry sending, if it has already been sent */
                            if ((_connectionState & ConnectionStateFlags.ConnectedToDestination) > 0)
                            {
                                // if we have not yet sent the FIN request, do so now.
                                if (_currentOutgoingTransmission.TransmissionType != TransmissionType.FIN)
                                {
                                    /* purge outgoing buffers */
                                    _transmitWindowSentButNotAcknowledgedMarker = _transmitWindowLeftEdge;
                                    _transmitWindowRightEdge = _transmitWindowLeftEdge;
                                    /* TODO: notify any pending Send(...) calls that their transmission is being aborted. */

                                    Int64 currentTimeInMachineTicks = Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks;

                                    lock (_currentOutgoingTransmissionLock)
                                    {
                                        // set our outstanding transmission type to FIN (which flag occupies a sequence #)
                                        _currentOutgoingTransmission = new TransmissionDetails(TransmissionType.FIN);
                                        _currentOutgoingTransmission.TransmissionAttemptsCounter = 1;
                                        _currentOutgoingTransmission.CurrentRetryTimeoutInMilliseconds = SYN_INITIAL_RETRY_TIMEOUT_IN_MS;
                                        _currentOutgoingTransmission.CurrentRetryTimeoutInMachineTicks = currentTimeInMachineTicks + (_currentOutgoingTransmission.CurrentRetryTimeoutInMilliseconds * TimeSpan.TicksPerMillisecond);
                                        _currentOutgoingTransmission.MaximumTimeoutInMachineTicks = currentTimeInMachineTicks + (SYN_MAX_RETRY_TIMEOUT_IN_MS * TimeSpan.TicksPerMillisecond);
                                    }

                                    _tcpHandler.SendTcpSegment(_srcIPAddress, _destIPAddress, _srcIPPort, _destIPPort, _transmitWindowLeftEdge, _receiveWindowLeftEdge, GetReceiveBufferBytesFree(),
                                        true, false, false, false, true, null, new byte[] { }, 0, 0, Int64.MaxValue); /* TODO: use timeout on sending segment */
                                    _tcpStateMachine_SendAckRequired = false;
                                }
                                else
                                {
                                    // otherwise, if we have not received an ACK for our FIN and our timeout has expired, resend it now.
                                    Int64 currentTimeInMachineTicks = Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks;
                                    if (currentTimeInMachineTicks > _currentOutgoingTransmission.CurrentRetryTimeoutInMachineTicks /* timeout occurred */)
                                    {
                                        if (currentTimeInMachineTicks > _currentOutgoingTransmission.MaximumTimeoutInMachineTicks)
                                        {
                                            /* abort close attempt */
                                            _connectionState = ConnectionStateFlags.None;
                                            _tcpStateMachine_CurrentState = TcpStateMachineState.CLOSED;
                                            if (_receiveBufferSpaceFilledEvent != null) _receiveBufferSpaceFilledEvent.Set();
                                            if (_outgoingConnectionClosedEvent != null) _outgoingConnectionClosedEvent.Set();
                                        }
                                        else
                                        {
                                            /* retry close attempt */
                                            _currentOutgoingTransmission.TransmissionAttemptsCounter += 1;
                                            _currentOutgoingTransmission.CurrentRetryTimeoutInMilliseconds *= 2;
                                            _currentOutgoingTransmission.CurrentRetryTimeoutInMachineTicks = currentTimeInMachineTicks + (_currentOutgoingTransmission.CurrentRetryTimeoutInMilliseconds * TimeSpan.TicksPerMillisecond);
                                            _tcpHandler.SendTcpSegment(_srcIPAddress, _destIPAddress, _srcIPPort, _destIPPort, _transmitWindowLeftEdge, _receiveWindowLeftEdge, GetReceiveBufferBytesFree(),
                                                true, false, false, false, true, null, new byte[] { }, 0, 0, Int64.MaxValue); /* TODO: enable timeout */
                                        }
                                    }
                                }
                            }

                            /* we ignore the destination host's request to stay connected to us after we initiate a FIN; once our FIN transmission timeout occurs we move to CLOSED or 2MSL state */
                            //if ((_connectionState & ConnectionStateFlags.ConnectedFromDestination) > 0)
                            //{
                            //}
                        }
                        break;
                    default:
                        break;
                }

                /* send any outgoing data along with any required ACKs */
                if (_tcpStateMachine_SendAckRequired)
                {
                    _tcpStateMachine_SendAckRequired = false;
                    _tcpHandler.SendTcpSegment(_srcIPAddress, _destIPAddress, _srcIPPort, _destIPPort, _transmitWindowLeftEdge, _receiveWindowLeftEdge, GetReceiveBufferBytesFree(),
                        true, false, false, (_currentOutgoingTransmission.TransmissionType == TransmissionType.SYN), false, null, new byte[] { }, 0, 0, Int64.MaxValue); /* TODO: enable timeout */
                }
            }
        }

        UInt16 GetReceiveBufferBytesFree()
        {
            lock (_receiveBufferLockObject)
            {
                UInt32 tempReceiveBufferNextBytePos = _receiveBufferNextBytePos;
                if (tempReceiveBufferNextBytePos < _receiveBufferFirstBytePos)
                    tempReceiveBufferNextBytePos += _receiveBufferSize;

                return (UInt16)(_receiveBufferSize - (tempReceiveBufferNextBytePos - _receiveBufferFirstBytePos) - 1);
            }
        }

        UInt16 GetReceiveBufferBytesFilled()
        {
            lock (_receiveBufferLockObject)
            {
                UInt32 tempReceiveBufferNextBytePos = _receiveBufferNextBytePos;
                if (tempReceiveBufferNextBytePos < _receiveBufferFirstBytePos)
                    tempReceiveBufferNextBytePos += _receiveBufferSize;

                return (UInt16)(tempReceiveBufferNextBytePos - _receiveBufferFirstBytePos);
            }
        }

        internal override UInt16 ReceiveBufferSize
        {
            get
            {
                return (UInt16)(_receiveBuffer.Length - 1); /* 1 byte of our internal buffer is unused */
            }
            set
            {
                value = (UInt16)(value + 1); /* 1 byte of our internal buffer is unused */

                lock (_receiveBufferLockObject)
                {
                    Int32 receiveBufferBytesFilled = GetReceiveBufferBytesFilled();

                    // if our new receive buffer is smaller than our old receive buffer, we will truncate any remaining data
                    /* NOTE: an alternate potential behavior would be to block new data reception until enough data gets retrieved by the caller that we can shrink the buffer...
                     *       or to only allow this operation before sockets are opened...
                     *       or to choose the larger of the requested size and the currently-used size
                     *       or to temporarily choose the larger of the requested size and the currently-used size...and then reduce the buffer size ASAP */
                    if (receiveBufferBytesFilled > value)
                        receiveBufferBytesFilled = value;

                    // create new receive buffer
                    byte[] newReceiveBuffer = new byte[value];

                    // copy existing receive buffer to new receive buffer
                    Int32 bytesToCopyBeforeWrap = (Int32)(System.Math.Min(receiveBufferBytesFilled, _receiveBuffer.Length - _receiveBufferFirstBytePos));
                    Array.Copy(_receiveBuffer, (Int32)_receiveBufferFirstBytePos, newReceiveBuffer, 0, bytesToCopyBeforeWrap);
                    _receiveBufferFirstBytePos = (_receiveBufferFirstBytePos + (UInt32)bytesToCopyBeforeWrap) % _receiveBufferSize;

                    if (_receiveBufferFirstBytePos < _receiveBufferNextBytePos)
                    {
                        // data wrapped; copy remaining data to new receive buffer now.
                        Int32 remainingBytesToCopy = receiveBufferBytesFilled - bytesToCopyBeforeWrap;
                        Array.Copy(_receiveBuffer, (Int32)_receiveBufferFirstBytePos, newReceiveBuffer, bytesToCopyBeforeWrap, remainingBytesToCopy);
                    }

                    _receiveBuffer = newReceiveBuffer;
                    _receiveBufferSize = value;
                    _receiveBufferFirstBytePos = 0;
                    _receiveBufferNextBytePos = (UInt32)receiveBufferBytesFilled;
                }

                /* if we are connected, send an ACK to the destination device to let it know that our buffer has more space available 
                 * NOTE: technically we only need to do this if our receive buffer has gotten smaller or...if our receive buffer has gotten larger and freed up enough room for a full-size incoming segment */
                if (_tcpStateMachine_CurrentState == TcpStateMachineState.ESTABLISHED)
                {
                    _tcpStateMachine_SendAckRequired = true;
                    if (_tcpStateMachine_ActionRequiredEvent != null) _tcpStateMachine_ActionRequiredEvent.Set();
                }
            }
        }

        UInt16 GetTransmitBufferBytesFree()
        {
            lock (_transmitBufferLockObject)
            {
                UInt32 tempTransmitBufferNextBytePos = _transmitBufferNextBytePos;
                if (tempTransmitBufferNextBytePos < _transmitBufferFirstBytePos)
                    tempTransmitBufferNextBytePos += _transmitBufferSize;

                return (UInt16)(_transmitBufferSize - (tempTransmitBufferNextBytePos - _transmitBufferFirstBytePos) - 1);
            }
        }

        UInt16 GetTransmitBufferBytesFilled()
        {
            lock (_transmitBufferLockObject)
            {
                UInt32 tempTransmitBufferNextBytePos = _transmitBufferNextBytePos;
                if (tempTransmitBufferNextBytePos < _transmitBufferFirstBytePos)
                    tempTransmitBufferNextBytePos += _transmitBufferSize;

                return (UInt16)(tempTransmitBufferNextBytePos - _transmitBufferFirstBytePos);
            }
        }

        internal override UInt16 TransmitBufferSize
        {
            get
            {
                return (UInt16)(_transmitBuffer.Length - 1); /* 1 byte of our internal buffer is unused */
            }
            set
            {
                value = (UInt16)(value + 1); /* 1 byte of our internal buffer is unused */

                lock (_transmitBufferLockObject)
                {
                    Int32 transmitBufferBytesFilled = GetTransmitBufferBytesFilled();

                    // if our new transmit buffer is smaller than our old transmit buffer, throw an exception.
                    /* NOTE: an alternate potential behavior would be to block new data transmissions until enough data gets sent that we can shrink the buffer...
                     *       or to only allow this operation before sockets are opened...
                     *       or to choose the larger of the requested size and the currently-used size
                     *       or to temporarily choose the larger of the requested size and the currently-used size...and then reduce the buffer size ASAP */
                    if (value < transmitBufferBytesFilled)
                        throw new ArgumentException();

                    // create new transmit buffer
                    byte[] newTransmitBuffer = new byte[value];

                    // copy existing transmit buffer to new transmit buffer
                    Int32 bytesToCopyBeforeWrap = (Int32)(System.Math.Min(transmitBufferBytesFilled, _transmitBuffer.Length - _transmitBufferFirstBytePos));
                    Array.Copy(_transmitBuffer, (Int32)_transmitBufferFirstBytePos, newTransmitBuffer, 0, bytesToCopyBeforeWrap);
                    _transmitBufferFirstBytePos = (_transmitBufferFirstBytePos + (UInt32)bytesToCopyBeforeWrap) % _transmitBufferSize;

                    if (_transmitBufferFirstBytePos < _transmitBufferNextBytePos)
                    {
                        // data wrapped; copy remaining data to new transmit buffer now.
                        Int32 remainingBytesToCopy = transmitBufferBytesFilled - bytesToCopyBeforeWrap;
                        Array.Copy(_transmitBuffer, (Int32)_transmitBufferFirstBytePos, newTransmitBuffer, bytesToCopyBeforeWrap, remainingBytesToCopy);
                    }

                    _transmitBuffer = newTransmitBuffer;
                    _transmitBufferSize = value;
                    _transmitBufferFirstBytePos = 0;
                    _transmitBufferNextBytePos = (UInt32)transmitBufferBytesFilled;
                }

                if (_transmitBufferSpaceFreedEvent != null) _transmitBufferSpaceFreedEvent.Set();
            }
        }

        public override Int32 GetBytesToRead()
        {
            return GetReceiveBufferBytesFilled();
        }
    }
}
