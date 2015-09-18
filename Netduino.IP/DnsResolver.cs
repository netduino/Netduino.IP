using System;
using System.Threading;

namespace Netduino.IP
{
    class DnsResolver : IDisposable 
    {
        const int DNS_HEADER_LENGTH = 12;
        const int DNS_FRAME_BUFFER_LENGTH = 512;

        const UInt16 DNS_SERVER_PORT = 53;

        /* TODO: we may want to make the DNS query timeout configurable in the future */
        // use a DNS query timeout of 5 seconds per server
        const int DNS_QUERY_TIMEOUT_MS = 5000;

        IPv4Layer _ipv4Layer;

        bool _isDisposed = false;

        UInt16 _nextTransactionID = 0;

        [Flags]
        enum DnsMessageFlagsFlags : ushort
        {
            QueryResponse       = (1 << 15),       /* Query = 0; Response = 1 */
            AuthoritativeAnswer = (1 << 10),
            TruncatedAnswer     = (1 << 9),
            RecursionDesired    = (1 << 8),
            RecursionAvailable  = (1 << 7),
            Zero                = (1 << 6),
            AuthenticData       = (1 << 5),
            CheckingDisabled    = (1 << 4),
        }

        enum DnsRecordType : ushort
        {
            A = 1,
            //NS = 2,
            //CNAME = 5,
            //SOA = 6,
            //PTR = 12,
            //MX = 15,
            //TXT = 16,
            //AAAA = 28,
            //SRV = 33,
            //NAPTR = 35,
            //OPT = 41,
            //IXFR = 251,
            //AXFR = 252,
            //ANY = 255,
        }

        const UInt16 DNS_RECORD_CLASS_INTERNET = 1;

        struct DnsResourceRecord
        {
            public string Name;
            public DnsRecordType RecordType;
            public UInt32 TimeToLive;
            public byte[] Data;

            public DnsResourceRecord(string name, DnsRecordType recordType, UInt32 timeToLive, byte[] data)
            {
                this.Name = name;
                this.RecordType = recordType;
                this.TimeToLive = timeToLive;
                this.Data = data;
            }
        }

        class DnsResponse
        {
            public DnsResponseCode ResponseCode;
            public DnsRecordType QueryType;
            public string QueryName;
            public DnsResourceRecord[] AnswerRecords;
            public DnsResourceRecord[] AuthorityRecords;
            public DnsResourceRecord[] AdditionalInformationRecords;

            public DnsResponse(DnsResponseCode responseCode, DnsRecordType queryType, string queryName, DnsResourceRecord[] answerRecords, DnsResourceRecord[] authorityRecords, DnsResourceRecord[] additionalInformationRecords)
            {
                this.ResponseCode = responseCode;
                this.QueryType = queryType;
                this.QueryName = queryName;
                this.AnswerRecords = answerRecords;
                this.AuthorityRecords = authorityRecords;
                this.AdditionalInformationRecords = additionalInformationRecords;
            }
        }

        enum DnsResponseCode : byte
        {
            NoError = 0,
            //FormErr = 1,
            ServFail = 2,
            NXDomain = 3,
            //NotImp = 4,
            Refused = 5,
            //YXDomain = 6,
            //YXRRSet = 7,
            //NXRRSet = 8,
            //NotAuth = 9,
            //NotZone = 10,
        }

        class DnsCacheEntry
        {
            public string Name;
            public string CanonicalName;
            public UInt32[] IpAddresses;
            public Int64 ExpirationTicks;
            /* LastUsedTicks is set whenever the entry is used to resolve a hostname; 
             * when the DNS cache is full, we clean out the oldest DNS entry based on the value of LastUsedTicks */
            public Int64 LastUsedTicks;

            public DnsCacheEntry(string name, string canonicalName, UInt32[] ipAddresses, Int64 expirationTicks, Int64 lastUsedTicks)
            {
                this.Name = name;
                this.CanonicalName = canonicalName;
                this.IpAddresses = ipAddresses;
                this.ExpirationTicks = expirationTicks;
                this.LastUsedTicks = lastUsedTicks;
            }
        }
        const byte DNS_CACHE_MAXIMUM_ENTRIES = 6; /* we will store a maximum of 6 entires in our ARP cache */ /* TODO: we should consider making this a configurable option...and default it to 4-8 */
        System.Collections.Hashtable _dnsCache;
        object _dnsCacheLock = new object();

        System.Threading.Timer _cleanupDnsCacheTimer;

        public DnsResolver(IPv4Layer ipv4Layer)
        {
            _ipv4Layer = ipv4Layer;

            // create our DNS cache
            _dnsCache = new System.Collections.Hashtable();

            // enable our "cleanup DNS cache" timer (fired every 600 seconds)
            _cleanupDnsCacheTimer = new Timer(CleanupDnsCache, null, 600000, 600000); 
        }

        public void Dispose()
        {
            if (_isDisposed) return;

            _isDisposed = true;

            if (_dnsCache != null)
                _dnsCache.Clear();
            _cleanupDnsCacheTimer.Dispose();

            _ipv4Layer = null;
        }

        public UInt32[] ResolveHostNameToIpAddresses(string name, out string canonicalName, Int64 timeoutInMachineTicks)
        {
            UInt32[] dnsServerAddresses = _ipv4Layer.DnsServerAddresses;
            if (dnsServerAddresses.Length < 1)
            {
                /* could not resolve DNS entry */
                throw Utility.NewSocketException(SocketError.NoRecovery); /* no DNS server is configured */
            }

            /* NOTE: this DNS resolver assumes that all names are Internet domains. */
            // ensure that the domain is rooted.
            if (name.Substring(name.Length - 1) != ".")
            {
                name += ".";
            }

            /* first, return a response from our dns cache if a valid response is already cached */
            lock (_dnsCacheLock)
            {
                DnsCacheEntry dnsEntry = (DnsCacheEntry)_dnsCache[name.ToLower()];

                // if we retrieved an entry, make sure it has not timed out.
                if (dnsEntry != null)
                {
                    Int64 nowTicks = Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks;
                    if (dnsEntry.ExpirationTicks < nowTicks)
                    {
                        // if the entry has timed out, dispose of it; we will re-query the target
                        _dnsCache.Remove(name.ToLower());
                        dnsEntry = null;
                    }
                    else
                    {
                        dnsEntry.LastUsedTicks = nowTicks; // update "last used" timestamp
                        canonicalName = dnsEntry.CanonicalName;
                        return dnsEntry.IpAddresses;
                    }
                }
            }

            // by default, set our canonicalName to the passed-in name
            canonicalName = name;

            Int64 expirationInMachineTicks = Int64.MaxValue;

            DnsResponse response = null;
            for (int iDnsServer = 0; iDnsServer < dnsServerAddresses.Length; iDnsServer++)
            {
                // set our query timeout to the maximum of DNS_QUERY_TIMEOUT_MS and the hard timeout passed into our function.
                Int64 queryTimeoutInMachineTicks = Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks + (DNS_QUERY_TIMEOUT_MS * TimeSpan.TicksPerMillisecond);
                if (queryTimeoutInMachineTicks > timeoutInMachineTicks)
                    queryTimeoutInMachineTicks = timeoutInMachineTicks;

                // query this DNS server and wait for a response (until a maximum time of queryTimeoutInMachineTicks)
                bool success = SendDnsQueryAndWaitForResponse(dnsServerAddresses[iDnsServer], DnsRecordType.A, name, out response, queryTimeoutInMachineTicks);
                if (success)
                    break;
            }

            if (response == null)
            {
                throw Utility.NewSocketException(SocketError.TryAgain); /* no response received from any dns server */
            }

            switch (response.ResponseCode)
            {
                case DnsResponseCode.NoError:
                    break; /* success */
                case DnsResponseCode.NXDomain:
                    throw Utility.NewSocketException(SocketError.HostNotFound);
                case DnsResponseCode.Refused:
                    throw Utility.NewSocketException(SocketError.NoRecovery);
                case DnsResponseCode.ServFail:
                    throw Utility.NewSocketException(SocketError.TryAgain);
                default:
                    break; /* in theory, some errors could actually have valid data, so ignore other errors for now. */
            }

            System.Collections.ArrayList ipAddressList = new System.Collections.ArrayList();
            for (int iRecord = 0; iRecord < response.AnswerRecords.Length; iRecord++)
            {
                if (response.AnswerRecords[iRecord].RecordType == DnsRecordType.A)
                {
                    canonicalName = response.AnswerRecords[iRecord].Name;
                    Int64 currentTimeoutTicks = Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks + (response.AnswerRecords[iRecord].TimeToLive * TimeSpan.TicksPerSecond);
                    if (currentTimeoutTicks < expirationInMachineTicks)
                    {
                        expirationInMachineTicks = Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks + (response.AnswerRecords[iRecord].TimeToLive * TimeSpan.TicksPerSecond);
                    }
                    ipAddressList.Add(
                    ((UInt32)response.AnswerRecords[iRecord].Data[0] << 24) +
                    ((UInt32)response.AnswerRecords[iRecord].Data[1] << 16) +
                    ((UInt32)response.AnswerRecords[iRecord].Data[2] << 8) +
                    ((UInt32)response.AnswerRecords[iRecord].Data[3])
                    );
                }
            }

            if (ipAddressList.Count > 0)
            {
                // add the host to our DNS cache
                lock (_dnsCacheLock)
                {
                    // first make sure that our cache table is not full; if it is full then remove the oldest entry (based on LastAccessedInMachineTicks)
                    if (_dnsCache.Count >= DNS_CACHE_MAXIMUM_ENTRIES)
                    {
                        Int64 oldestLastUsedTicks = Int64.MaxValue;
                        string oldestKey = string.Empty;
                        foreach (string key in _dnsCache.Keys)
                        {
                            if (((DnsCacheEntry)_dnsCache[key]).LastUsedTicks < oldestLastUsedTicks)
                            {
                                oldestKey = key;
                                oldestLastUsedTicks = ((DnsCacheEntry)_dnsCache[key]).LastUsedTicks;
                            }
                        }
                        _dnsCache.Remove(oldestKey);
                    }

                    DnsCacheEntry dnsEntry = new DnsCacheEntry(name, canonicalName, (UInt32[])ipAddressList.ToArray(typeof(UInt32)), expirationInMachineTicks, Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks);
                    _dnsCache.Add(name.ToLower(), dnsEntry);
                }
            }

            if (ipAddressList.Count == 0)
                throw Utility.NewSocketException(SocketError.NoData);

            UInt32[] addresses = (UInt32[])ipAddressList.ToArray(typeof(UInt32));
            return addresses;
        }

        bool SendDnsQueryAndWaitForResponse(UInt32 dnsServerIPAddress, DnsRecordType recordType, string name, out DnsResponse dnsResponse, Int64 timeoutInMachineTicks)
        {
            // obtain an exclusive handle to the reserved socket
            int socketHandle = _ipv4Layer.CreateSocket(IPv4Layer.ProtocolType.Udp, timeoutInMachineTicks, true);
            // instantiate the reserved socket
            UdpSocket socket = (UdpSocket)_ipv4Layer.GetSocket(socketHandle);

            try
            {
                // create DNS request header
                byte[] dnsRequestBuffer = new byte[DNS_FRAME_BUFFER_LENGTH];
                int bufferIndex = 0;
                /* Transaction ID */
                UInt16 transactionID = _nextTransactionID++;
                dnsRequestBuffer[bufferIndex++] = (byte)((transactionID >> 8) & 0xFF);
                dnsRequestBuffer[bufferIndex++] = (byte)(transactionID & 0xFF);
                /* Flags (including OpCode and RCODE) */
                DnsMessageFlagsFlags flags = 0;
                /* QR = 0 (query)
                 * OpCode = 0 (standard query/response)
                 * RD = 1 (recursion desired) */
                flags |= DnsMessageFlagsFlags.RecursionDesired;
                dnsRequestBuffer[bufferIndex++] |= (byte)(((UInt16)flags >> 8) & 0xFF);
                dnsRequestBuffer[bufferIndex++] |= (byte)((UInt16)flags & 0xFF);
                /* Query Count */
                UInt16 queryCount = 1;
                dnsRequestBuffer[bufferIndex++] = (byte)((queryCount >> 8) & 0xFF);
                dnsRequestBuffer[bufferIndex++] = (byte)(queryCount & 0xFF);
                /* Answer Count */
                // skip bytes 6-7
                bufferIndex += 2;
                /* Authority Record Count */
                // skip bytes 8-9
                bufferIndex += 2;
                /* Additional Information Count */
                // skip bytes 10-11
                bufferIndex += 2;

                // create dns question (query)
                // Query Name
                Int32 namePosition = 0;
                while (true)
                {
                    Int32 labelLength = name.IndexOf('.', namePosition) - namePosition;
                    if (labelLength < 0)
                    {
                        bufferIndex++;
                        break;
                    }

                    dnsRequestBuffer[bufferIndex++] = (byte)labelLength;
                    Int32 labelLengthVerify = System.Text.Encoding.UTF8.GetBytes(name, namePosition, labelLength, dnsRequestBuffer, bufferIndex);
                    // if the label was not decoded as 8-bit characters, throw an exception; international punycode-encoded domains are not supported.
                    if (labelLengthVerify != labelLength)
                        throw new ArgumentException();

                    namePosition += labelLength + 1;
                    bufferIndex += labelLength;
                }
                // Query Type
                dnsRequestBuffer[bufferIndex++] = (byte)((((UInt16)recordType) >> 8) & 0xFF);
                dnsRequestBuffer[bufferIndex++] = (byte)(((UInt16)recordType) & 0xFF);
                // Query Class
                UInt16 queryClass = DNS_RECORD_CLASS_INTERNET; 
                dnsRequestBuffer[bufferIndex++] = (byte)((queryClass >> 8) & 0xFF);
                dnsRequestBuffer[bufferIndex++] = (byte)(queryClass & 0xFF);

                /* NOTE: this implementation queries the primary DNS address */
                // send DNS request
                socket.SendTo(dnsRequestBuffer, 0, bufferIndex, 0, timeoutInMachineTicks, dnsServerIPAddress, DNS_SERVER_PORT);

                // wait for DNS response
                bool success = RetrieveDnsResponse(socket, transactionID, out dnsResponse, timeoutInMachineTicks);

                if (success)
                {
                    return true;
                }
            }
            finally
            {
                // close the reserved socket
                _ipv4Layer.CloseSocket(socketHandle);
            }

            return false;  /* could not retrieve DNS response */
        }

        bool RetrieveDnsResponse(UdpSocket socket, UInt16 transactionID, out DnsResponse dnsResponse, Int64 timeoutInMachineTicks)
        {
            byte[] dnsFrameBuffer = new byte[DNS_FRAME_BUFFER_LENGTH];
            while (timeoutInMachineTicks > Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks)
            {
                Int32 bytesReceived = socket.Receive(dnsFrameBuffer, 0, dnsFrameBuffer.Length, 0, timeoutInMachineTicks);

                if (bytesReceived == 0) // timeout
                {
                    break;
                }

                /* parse our DNS response */
                Int32 bufferIndex = 0;
                // verify that the transaction ID matches
                UInt16 verifyTransactionID = (UInt16)(
                    (UInt16)(dnsFrameBuffer[bufferIndex++] << 8) +
                    (UInt16)(dnsFrameBuffer[bufferIndex++])
                    );
                if (transactionID != verifyTransactionID)
                    continue; /* filter out this DHCP frame */
                // Flags
                UInt16 flags = (UInt16)(
                    (UInt16)(dnsFrameBuffer[bufferIndex++] << 8) +
                    (UInt16)(dnsFrameBuffer[bufferIndex++])
                    );
                DnsResponseCode responseCode = (DnsResponseCode)(flags & 0x0F);
                // Query Count
                UInt16 queryCount = (UInt16)(
                    (UInt16)(dnsFrameBuffer[bufferIndex++] << 8) +
                    (UInt16)(dnsFrameBuffer[bufferIndex++])
                    );
                // Answer Record Count
                UInt16 answerRecordCount = (UInt16)(
                    (UInt16)(dnsFrameBuffer[bufferIndex++] << 8) +
                    (UInt16)(dnsFrameBuffer[bufferIndex++])
                    );
                // Authority Record Count
                UInt16 authorityRecordCount = (UInt16)(
                    (UInt16)(dnsFrameBuffer[bufferIndex++] << 8) +
                    (UInt16)(dnsFrameBuffer[bufferIndex++])
                    );
                // Additional Information Record Count
                UInt16 additionalInformationRecordCount = (UInt16)(
                    (UInt16)(dnsFrameBuffer[bufferIndex++] << 8) +
                    (UInt16)(dnsFrameBuffer[bufferIndex++])
                    );

                /* parse our query records */
                string queryName = "";
                DnsRecordType queryType = (DnsRecordType)0;
                for (int iRecord = 0; iRecord < queryCount; iRecord++)
                {
                    // Query Name
                    bufferIndex += ParseDnsName(dnsFrameBuffer, bufferIndex, out queryName);
                    queryType = (DnsRecordType)(
                        (UInt16)(dnsFrameBuffer[bufferIndex++] << 8) +
                        (UInt16)(dnsFrameBuffer[bufferIndex++])
                        );
                    UInt16 queryClass = (UInt16)(
                        (UInt16)(dnsFrameBuffer[bufferIndex++] << 8) +
                        (UInt16)(dnsFrameBuffer[bufferIndex++])
                        );
                    if (queryClass != DNS_RECORD_CLASS_INTERNET)
                        continue; /* filter out the current query */
                }

                /* parse our answer records */
                DnsResourceRecord[] answerRecords = new DnsResourceRecord[answerRecordCount];
                for (int iRecord = 0; iRecord < answerRecordCount; iRecord++)
                {
                    // store answer record
                    bufferIndex += ParseResourceRecord(dnsFrameBuffer, bufferIndex, out answerRecords[iRecord]);
                }

                /* parse our authority records */
                DnsResourceRecord[] authorityRecords = new DnsResourceRecord[authorityRecordCount];
                for (int iRecord = 0; iRecord < authorityRecordCount; iRecord++)
                {
                    // store authority record
                    bufferIndex += ParseResourceRecord(dnsFrameBuffer, bufferIndex, out authorityRecords[iRecord]);
                }

                /* parse our authority records */
                DnsResourceRecord[] additionalInformationRecords = new DnsResourceRecord[additionalInformationRecordCount];
                for (int iRecord = 0; iRecord < additionalInformationRecordCount; iRecord++)
                {
                    // store authority record
                    bufferIndex += ParseResourceRecord(dnsFrameBuffer, bufferIndex, out additionalInformationRecords[iRecord]);
                }

                dnsResponse = new DnsResponse(responseCode, queryType, queryName, answerRecords, authorityRecords, additionalInformationRecords);
                return true;
            }

            // if we did not receive a message before timeout, return false.
            dnsResponse = null;
            return false;
        }

        /* this function returns the number of bytes processed */
        Int32 ParseResourceRecord(byte[] buffer, Int32 offset, out DnsResourceRecord resourceRecord)
        {
            Int32 bytesProcessed = 0;
            // Name
            string name;
            int dnsNameBytesProcessed = ParseDnsName(buffer, offset, out name);
            bytesProcessed += dnsNameBytesProcessed;
            offset += bytesProcessed;
            // Type
            DnsRecordType recordType = (DnsRecordType)(
                (UInt16)(buffer[offset++] << 8) +
                (UInt16)(buffer[offset++])
                );
            bytesProcessed += 2;
            // Class
            UInt16 recordClass = (UInt16)(
                (UInt16)(buffer[offset++] << 8) +
                (UInt16)(buffer[offset++])
                );
            bytesProcessed += 2;
            // TTL
            UInt32 timeToLive = (
                (UInt32)(buffer[offset++] << 24) +
                (UInt32)(buffer[offset++] << 16) +
                (UInt32)(buffer[offset++] << 8) +
                (UInt32)(buffer[offset++])
                );
            bytesProcessed += 4;
            // RDLENGTH
            UInt16 dataLength = (UInt16)(
                (UInt16)(buffer[offset++] << 8) +
                (UInt16)(buffer[offset++])
                );
            bytesProcessed += 2;
            // RDATA
            byte[] data = new byte[dataLength];
            Array.Copy(buffer, offset, data, 0, dataLength);
            offset += dataLength;
            bytesProcessed += dataLength;

            if (recordClass == DNS_RECORD_CLASS_INTERNET)
            {
                resourceRecord = new DnsResourceRecord(name, recordType, timeToLive, data);
            }
            else
            {
                /* filter out the current query */
                resourceRecord = new DnsResourceRecord();
            }

            return bytesProcessed;
        }

        /* this function returns the number of bytes processed */
        Int32 ParseDnsName(byte[] buffer, Int32 offset, out string name)
        {
            System.Text.StringBuilder nameBuilder = new System.Text.StringBuilder();
            Int32 bytesProcessed = 0;

            UInt16 labelLength = 0;
            while (true)
            {
                if ((buffer[offset] & 0xC0) == 0xC0)
                {
                    // the remainder of the name is located at another position in the buffer
                    string nameSuffix;
                    UInt16 labelAbsolutePosition = (UInt16)((buffer[offset] & 0x3F) + buffer[offset + 1]);
                    ParseDnsName(buffer, labelAbsolutePosition, out nameSuffix);
                    nameBuilder.Append(nameSuffix);

                    bytesProcessed += 2;
                    break;
                }
                else
                {
                    labelLength = buffer[offset];
                    bytesProcessed++;
                    offset++;

                    if (labelLength == 0)
                        break;

                    nameBuilder.Append(System.Text.Encoding.UTF8.GetChars(buffer, offset, labelLength));
                    nameBuilder.Append('.');

                    bytesProcessed += labelLength;
                    offset += labelLength;
                }
            }

            name = nameBuilder.ToString();
            return bytesProcessed;
        }

        // this function is called ocassionally to clean up timed-out entries in the DNS cache
        void CleanupDnsCache(object state)
        {
            Int64 nowTicks = Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks;
            bool keyWasRemoved = true;

            // NOTE: this loop isn't very efficient since it starts over after every key removal: we may consider more efficient removal processes in the future.
            while (keyWasRemoved == true)
            {
                keyWasRemoved = false; // default to "no keys removed"
                lock (_dnsCacheLock)
                {
                    foreach (string key in _dnsCache.Keys)
                    {
                        if (((DnsCacheEntry)_dnsCache[key]).ExpirationTicks < nowTicks)
                        {
                            _dnsCache.Remove(key);
                            keyWasRemoved = true;
                            break; // exit the loop so we can parse the set of keys again; since we just removed a key the collection is not in a valid enumerable state.
                        }
                    }
                }
            }
        }

    }
}
