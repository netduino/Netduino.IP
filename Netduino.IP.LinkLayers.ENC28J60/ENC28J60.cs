// Netduino.IP Stack
// Copyright (c) 2015 Secret Labs LLC. All rights reserved.
// Licensed under the Apache 2.0 License

using Microsoft.SPOT;
using Microsoft.SPOT.Hardware;
using System;

/* THESE ARE ERRATA NOTES WHICH DO NOT APPLY TO OUR CURRENT IMPLEMENTATION -- BUT MAY APPLY TO FUTURE REVISIONS
 * ERRATA NOTE: per ENC28J60 errata 2, if we use software reset in the future we must wait 1ms to know that the PHY is ready.  This is because the CLKRDY bit will not be cleared by a software reset.
 * ERRATA NOTE: per ENC28J60 errata 15, if a collision occurs after 64 bytes have been sent, the Late Collision Error bit may not be set (although TX Error Interrupt will be set).  See workaround details. 
 */

/* THESE ARE ERRATA NOTES WHICH MAY APPLY TO OUR CURRENT IMPLEMENTATION
 * ERRATA NOTE: per ENC28J60 errata 13, some transmissions may never complete (in our half-duplex mode).  We need to capture the TX error flag and potentially retransmit our failed TX frames 
 */

namespace Netduino.IP.LinkLayers
{
    public class ENC28J60 : Netduino.IP.ILinkLayer
    {
        // our SPI bus instance
        SPI _spi = null;
        // our SPI bus lock (NOTE: we lock this whenever we use our SPI bus or shared bus-related buffers)
        object _spiLock = new object();

        InterruptPort _interruptPin = null;
        OutputPort _chipSelectPin = null;
        const bool _chipSelectActiveLevel = false;
        OutputPort _resetPin = null;
        Cpu.Pin _resetPinID = Cpu.Pin.GPIO_NONE;
        InterruptPort _wakeupPin = null;
        Cpu.Pin _wakeupPinID = Cpu.Pin.GPIO_NONE;

        byte[] _macAddress = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

        // buffer size and maximum frame length
        const UInt16 TOTAL_BUFFER_SIZE = 8192;
        const UInt16 WRITE_BUFFER_SIZE = 1528; /* 1528 bytes provides for a full TX frame including control byte and status vector...plus 2 bytes of empty buffer; this must be an even number and we need at least one byte empty buffer space */
        const UInt16 READ_BUFFER_SIZE = TOTAL_BUFFER_SIZE - WRITE_BUFFER_SIZE; /* use all but WRITE_BUFFER_SIZE bytes for the RX buffer; remaining bytes will be allocated to the TX buffer automatically */
        const UInt16 READ_BUFFER_BEGIN = 0; // ERRATA NOTE: per ENC28J60 errata 5, the receive buffer effectively MUST begin at 0x000
        const UInt16 WRITE_BUFFER_BEGIN = (READ_BUFFER_BEGIN + READ_BUFFER_SIZE) % TOTAL_BUFFER_SIZE;
        /* NOTE: for ENC28J60, MAX_TX_FRAME_LENGTH and MAX_RX_FRAME_LENGTH should be the same length */
        const int MAX_TX_FRAME_LENGTH = 1526; /* 1518 bytes + 1 control byte + 7 bytes for status vector */
        const int MAX_RX_FRAME_LENGTH = 1526; /* 1518 bytes + 4 byte CRC + 4 bytes for status vector */

        const int ETHERNET_FCS_LENGTH = 4;

        bool _isInitialized = false;

        event PacketReceivedEventHandler OnPacketReceived;
        event LinkStateChangedEventHandler OnLinkStateChanged;
        bool _lastLinkState = false;

        byte _lastRegisterBank = 0x00;

        // fixed buffers for reading/writing registers
        byte[] _readControlRegister_WriteBuffer = new byte[3];
        byte[] _readControlRegister_ReadBuffer = new byte[1];
        byte[] _writeControlRegister_WriteBuffer = new byte[2];

        //// fixed buffer for exiting power down mode
        //byte[] _exitPowerDownMode_WriteBuffer = new byte[1];

        // fixed buffers for reading/writing TX/RX buffers
        byte[] _writeBufferMemory_WriteBuffer = new byte[1];
        byte[] _writeBufferMemory_ReadBuffer = new byte[0];
        byte[] _readBufferMemory_WriteBuffer = new byte[1];

        // fixed buffers for incoming data
        byte[] _incomingFrame = new byte[MAX_RX_FRAME_LENGTH];

        // fixed buffers for received packets
        byte[] _retrievePacket_NextPacketPointerBuffer = new byte[2];
        byte[] _retrievePacket_ReceiveStatusVectorBuffer = new byte[4];
        UInt16 _retrievePacket_NextPacketPointer = READ_BUFFER_BEGIN;

        // fixed buffers for sending packets
        byte[] _sendPacketControlByte = new byte[1];
        // synchronization event for waiting on TX buffer to become free
        System.Threading.AutoResetEvent _sendPacketTxBufferFreeEvent;

        // ENC28J60 opcodes (pre-shifted left by 5 bits)
        const byte ENC28J60_SPI_INSTRUCTION_OPCODE_BITSHIFT = 5;
        const byte ENC28J60_SPI_INSTRUCTION_ARGUMENT_MASK = 0x1F;
        enum ENC28J60Opcode : byte
        {
            ReadControlRegister = 0x00 << ENC28J60_SPI_INSTRUCTION_OPCODE_BITSHIFT,
            ReadBufferMemory = 0x01 << ENC28J60_SPI_INSTRUCTION_OPCODE_BITSHIFT,
            WriteControlRegister = 0x02 << ENC28J60_SPI_INSTRUCTION_OPCODE_BITSHIFT,
            WriteBufferMemory = 0x03 << ENC28J60_SPI_INSTRUCTION_OPCODE_BITSHIFT,
            BitFieldSet = 0x04 << ENC28J60_SPI_INSTRUCTION_OPCODE_BITSHIFT,
            BitFieldClear = 0x05 << ENC28J60_SPI_INSTRUCTION_OPCODE_BITSHIFT,
            // 0x06 << ENC28J60_SPI_INSTRUCTION_OPCODE_BITSHIFT
            //SystemResetCommand  = 0x07 << ENC28J60_SPI_INSTRUCTION_OPCODE_BITSHIFT,
        }

        // ENC28J60Register registers
        /* NOTE: the upper byte of the value is the register bank; the lower byte of the value is the register address within that bank; bank 0xFF means "any bank" for the common registers */
        enum ENC28J60Register : ushort
        {
            /* bank 0 */
            ERDPTL = 0x0000, // ERDPTL (Read Pointer Low Byte)
            ERDPTH = 0x0001, // ERDPTH (Read Pointer High Byte)
            EWRPTL = 0x0002, // EWRPTL (Write Pointer Low Byte)
            EWRPTH = 0x0003, // EWRPTH (Write Pointer High Byte)
            ETXSTL = 0x0004, // ETXSTL (TX Start Low Byte)
            ETXSTH = 0x0005, // ETXSTH (TX Start High Byte
            ETXNDL = 0x0006, // ETXNDL (TX End Low Byte)
            ETXNDH = 0x0007, // ETXNDH (TX End High Byte)
            ERXSTL = 0x0008, // ERXSTL (RX Start Low Byte)
            ERXSTH = 0x0009, // ERXSTH (RX Start High Byte)
            ERXNDL = 0x000A, // ERXNDL (RX End Low Byte)
            ERXNDH = 0x000B, // ERXNDH (RX End High Byte)
            ERXRDPTL = 0x000C, // ERXRDPTL (RX RD Pointer Low Byte)
            ERXRDPTH = 0x000D, // ERXRDPTH (RX RD Pointer High Byte)
            //ERXWRPTL = 0x000E,
            //ERXWRPTH = 0x000F,
            //EDMASTL = 0x0010,
            //EDMASTH = 0x0011,
            //EDMANDL = 0x0012,
            //EDMANDH = 0x0013,
            //EDMADSTL = 0x0014,
            //EDMADSTH = 0x0015,
            //EDMACSL = 0x0016,
            //EDMACSH = 0x0017,
            // 0x0018
            // 0x0019
            // 0x001A // Reserved
            /* bank 1 */
            //EHT0 = 0x0100,
            //EHT1 = 0x0101,
            //EHT2 = 0x0102,
            //EHT3 = 0x0103,
            //EHT4 = 0x0104,
            //EHT5 = 0x0105,
            //EHT6 = 0x0106,
            //EHT7 = 0x0107,
            //EPMM0 = 0x0108,
            //EPMM1 = 0x0109,
            //EPMM2 = 0x010A,
            //EPMM3 = 0x010B,
            //EPMM4 = 0x010C,
            //EPMM5 = 0x010D,
            //EPMM6 = 0x010E,
            //EPMM7 = 0x010F,
            //EPMCSL = 0x0110,
            //EPMCSH = 0x0111,
            // 0x0112
            // 0x0113
            //EPMOL = 0x0114,
            //EPMOH = 0x0115,
            // 0x0116 // Reserved
            // 0x0117 // Reserved
            ERXFCON = 0x0118, // ERXFCON (Ethernet Receive Filter Control Register)
            EPKTCNT = 0x0119, // EPKTCNT (Ethernet Packet Count)
            // 0x011A // Reserved
            /* bank 2 */
            MACON1 = 0x0200, // MACON1 (MAC Control Register 1)
            // 0x0201 // Reserved
            MACON3 = 0x0202, // MACON3 (MAC Control Register 3)
            MACON4 = 0x0203, // MACON4 (MAC Control Register 4)
            MABBIPG = 0x0204, // MABBIPG (MAC Back-to-back Inter-packet Gap Register)
            // 0x0205
            MAIPGL = 0x0206, // MAIPGL (Non-Back-to-Back Inter-Packet Gap Low Byte)
            MAIPGH = 0x0207, // MAIPGH (Non-Back-to-Back Inter-Packet Gap High Byte)
            MACLCON1 = 0x0208, // MACLCON1 (Retransmission Maximum)
            MACLCON2 = 0x0209, // MACLCON2 (Collision Window)
            MAMXFLL = 0x020A, // MAMXFLL (Maximum Frame Length Low Byte)
            MAMXFLH = 0x020B, // MAMXFLH (Maximum Frame Length High Byte)
            // 0x020C // Reserved
            // 0x020D // Reserved
            // 0x020E // Reserved
            // 0x020F
            // 0x0210 // Reserved
            // 0x0211 // Reserved
            MICMD = 0x0212, // MICMD (MII Command Register)
            // 0x0213
            MIREGADR = 0x0214, // MIREGADR (MII Register Address)
            // 0x0215 // Reserved
            MIWRL = 0x0216, // MIWRL (MII Write Data Low Byte)
            MIWRH = 0x0217, // MIWRH (MII Write Data High Byte)
            MIRDL = 0x0218, // MIRDL (MII Read Data Low Byte)
            MIRDH = 0x0219, // MIRDH (MII Read Data High Byte)
            // 0x021A // Reserved
            /* bank 3 */
            MAADR5 = 0x0300, // MAADR5 (MAC Address Byte 5)
            MAADR6 = 0x0301, // MAADR6 (MAC Address Byte 6)
            MAADR3 = 0x0302, // MAADR3 (MAC Address Byte 3)
            MAADR4 = 0x0303, // MAADR4 (MAC Address Byte 4)
            MAADR1 = 0x0304, // MAADR1 (MAC Address Byte 1)
            MAADR2 = 0x0305, // MAADR2 (MAC Address Byte 2)
            //EBSTSD = 0x0306,
            //EBSTCON = 0x0307,
            //EBSTCSL = 0x0308,
            //EBSTCSH = 0x0309,
            MISTAT = 0x030A, // MISTAT (MII Status Register)
            // 0x030B
            // 0x030C
            // 0x030D
            // 0x030E
            // 0x030F
            // 0x0310
            // 0x0311
            //EREVID = 0x0312,
            // 0x0313
            // 0x0314
            ECOCON = 0x0315, // ECOCON (Clock Output Control Register)
            // 0x0316 // Reserved
            //EFLOCON = 0x0317,
            //EPAUSL = 0x0318,
            //EPAUSH = 0x0319,
            // 0x031A // Reserved
            /* common registers */
            EIE = 0xFF1B, // EIE (Ethernet Interrupt Enable Register)
            EIR = 0xFF1C, // EIR (Ethernet Interrupt Request (Flag) Register)
            ESTAT = 0xFF1D, // ESTAT (Ethernet Status Register)
            ECON2 = 0xFF1E, // ECON2 (Ethernet Control Register 2)
            ECON1 = 0xFF1F, // ECON1 (Ethernet Control Register 1)
        }

        // ENC28J60 registers' bit options and flags
        enum ENC28J60RegisterBits : byte
        {
            // ERXFCON (Ethernet Receive Filter Control Register)
            ERXFCON_BCEN = (1 << 0), // Broadcast Filter Enable
            ERXFCON_MCEN = (1 << 1), // Multicast Filter Enable
            ERXFCON_PMEN = (1 << 4), // Pattern Match Filter Enable
            ERXFCON_CRCEN = (1 << 5), // Post-Filter CRC Check Enable
            ERXFCON_ANDOR = (1 << 6), // AND/OR Filter Select, set this bit to select AND and clear it to select OR
            ERXFCON_UCEN = (1 << 7), // Unicast Filter Enable
            // MACON1 (MAC Control Register 1)
            MACON1_MARXEN = (1 << 0), // MAC Receive Enable
            // MACON3 (MAC Control Register 3)
            MACON3_TXCRCEN = (1 << 4), // Transmit CRC Enable
            MACON3_PADCFG0 = (1 << 5), // Automatic Pad and CRC Configuration (bit 0)
            MACON3_PADCFG1 = (1 << 6), // Automatic Pad and CRC Configuration (bit 1)
            MACON3_PADCFG2 = (1 << 7), // Automatic Pad and CRC Configuration (bit 2)
            // MACON4 (MAC Control Register 4)
            MACON4_DEFER = (1 << 6), // Defer Transmission Enable
            // MICMD (MII Command Register)
            MICMD_MIIRD = (1 << 0), // MII Read Enable
            // MISTAT (MII Status Register)
            MISTAT_BUSY = (1 << 0), // MII Management Busy
            // EIE (Ethernet Interrupt Enable Register)
            //EIE_RXERIE = (1 << 0), // Receive Error Interrupt Enable
            //EIE_TXERIE = (1 << 1), // Transmit Error Interrupt Enable
            EIE_TXIE = (1 << 3), // Transmit Interrupt Enable
            EIE_LINKIE = (1 << 4), // Link Status Change Interrupt Enable
            //EIE_DMAIE = (1 << 5), // DMA Interrupt Enable
            EIE_PKTIE = (1 << 6), // Receive Packet Pending Interrupt Enable
            EIE_INTIE = (1 << 7), // Global INT Interrupt Enable
            // EIR (Ethernet Interrupt Request (Flag) Register)
            //EIR_RXERIF = (1 << 0), // Receive Error Interrupt Flag
            EIR_TXERIF = (1 << 1), // Transmit Error Interrupt Flag
            EIR_TXIF = (1 << 3), // Transmit Interrupt Flag
            EIR_LINKIF = (1 << 4), // Link Change Interrupt Flag
            //EIR_DMAIF = (1 << 5), // DMA Interrupt Flag
            EIR_PKTIF = (1 << 6), // Receive Packet Pending Interrupt Flag
            // ESTAT (Ethernet Status Register)
            ESTAT_CLKRDY = (1 << 0), // Clock Ready
            ESTAT_RXBUSY = (1 << 2), // Receive Busy
            // ECON2 (Ethernet Control Register 2)
            ECON2_VRPS = (1 << 3), // Voltage Regulator Power Save Enable
            ECON2_PWRSV = (1 << 5), // Power Save Enable    
            ECON2_PKTDEC = (1 << 6), // Packet Decrement
            ECON2_AUTOINC = (1 << 7), // Automatic Buffer Pointer Increment Enable
            // ECON1 (Ethernet Control Register 1)
            ECON1_RXEN = (1 << 2), // Receive Enable
            ECON1_TXRTS = (1 << 3), // Transmit Request to Send (transmissions attempt in progress)
        }

        enum ENC28J60PhyRegister : byte
        {
            PHCON1 = 0x00, // PHCON1 (PHY Control Register 1)
            PHSTAT1 = 0x01, // PHSTAT1 (PHY Status Register 1)
            //PHID1 = 0x02,
            //PHID2 = 0x03,
            PHCON2 = 0x10, // PHCON2 (PHY Control Register 2)
            PHSTAT2 = 0x11, // PHSTAT2 (PHY Status Register 2)
            PHIE = 0x12, // PHIE (PHY Interrupt Enable Register)
            PHIR = 0x13, // PHIR (PHY Interrupt Request (Flag) Register)
            PHLCON = 0x14, // PHLCON (PHY Module LED Control Register)
        }

        enum ENC28J60PhyRegisterBits : ushort
        {
            // PHCON1 (PHY Control Register 1)
            PHCON1_PDPXMD = (1 << 8), // PHY Duplex Mode
            // PHSTAT1 (PHY Status Register 1)
            PHSTAT1_LLSTAT = (1 << 2), // PHY Latching Link Status
            // PHCON2 (PHY Control Register 2)
            PHCON2_HDLDIS = (1 << 8), // PHY Half-Duplex Loopback Disable
            // PHSTAT2 (PHY Status Register 2)
            PHSTAT2_LSTAT = (1 << 10), // PHY Link Status bit (non-latching)
            // PHIE (PHY Interrupt Enable Register)
            PHIE_PGEIE = (1 << 1), // PHY Global Interrupt Enable
            PHIE_PLNKIE = (1 << 4), // PHY Link Change Interrupt Enable
            // PHIR (PHY Interrupt Request (Flag) Register)
            //PHIR_PGIF = (1 << 2), // PHY Global Interrupt Flag
            //PHIR_PLNKIF = (1 << 4), // PHY Link Change Interrupt Flag
            // PHLCON (PHY Module LED Control Register)
            PHLCON_STRCH = (1 << 1), // LED Pulse Stretching Enable
        }

        public ENC28J60(SPI.SPI_module spiBusID, Cpu.Pin csPinID, Cpu.Pin intPinID, Cpu.Pin resetPinID, Cpu.Pin wakeupPinID)
        {
            // create our chip select pin and SPI bus objects
            _chipSelectPin = new OutputPort(csPinID, true);
            // ERRATA NOTE: per ENC28J60 errata 1, our SPI clock speed must be at least 8,000,000Hz.  Because our SPI drivers may be clocked via a "/2^x" division, our clock rate must be 16MHz-20MHz. */
            _spi = new SPI(new SPI.Configuration(Cpu.Pin.GPIO_NONE, false, 0, 0, false, true, 20000, spiBusID));

            // wire up our interrupt, for future use
            _interruptPin = new InterruptPort(intPinID, true, Port.ResistorMode.PullUp, Port.InterruptMode.InterruptEdgeLow);
            _interruptPin.DisableInterrupt();
            _interruptPin.OnInterrupt += _interruptPin_OnInterrupt;

            // save our reset pin ID (which we will use to control the reset pin a bit later on)
            _resetPinID = resetPinID;

            // save our wakeup pin ID (which we can use to create an InterruptPort right before we go to sleep)
            _wakeupPinID = wakeupPinID;

            // create our _sendPacketTxBufferFreeEvent
            _sendPacketTxBufferFreeEvent = new System.Threading.AutoResetEvent(false);

            // we are not initialized; we will initialize when we are started. 
            _isInitialized = false;
        }

        void _interruptPin_OnInterrupt(uint data1, uint data2, DateTime time)
        {
            if (!_isInitialized)
                return;

            // disable interrupts
            BitFieldClear(ENC28J60Register.EIE, (byte)ENC28J60RegisterBits.EIE_INTIE);

            byte eir = ReadControlRegister(ENC28J60Register.EIR);
            /* ERRATA NOTE: per ENC28J60 errata 6, /INT will be fired when packets are received...but the EIR_PKTIF flag may not be set correctly.  The workaround is to check the packet count. */
            //if ((eir & (byte)ENC28J60RegisterBits.EIR_PKTIF) == (byte)ENC28J60RegisterBits.EIR_PKTIF)
            int rxPacketCount = ReadControlRegister(ENC28J60Register.EPKTCNT);
            if (rxPacketCount > 0)
            {
                byte[] buffer;
                int index;
                int count;

                // retrieve the number of RX packets to dequeue
                //int rxPacketCount = ReadControlRegister(ENC28J60Register.EPKTCNT);
                while (rxPacketCount-- > 0)
                {
                    /* WARNING: the returned packet is a reference to a single internal buffer.  The data must be retrieved and processed before calling RetrievePacket again. 
                                this pattern was chosen to minimize garbage collection, by reusing an existing buffer without freeing/creating large extraneous objects on the heap. */
                    RetrievePacket(out buffer, out index, out count);
                    if (count > 0)
                    {
                        if (OnPacketReceived != null)
                            OnPacketReceived(this, buffer, index, count - ETHERNET_FCS_LENGTH);
                    }
                    else
                    {
                        break; // if no packet is available, stop requesting packets now.
                    }

                    /* NOTE: our RetrievePacket call will set the ECON2_PKTDEC bit when a packet is retrieved; this will automatically de-assert the EIR_PKTIF flag as well. */
                }
            }
            if ((eir & (byte)ENC28J60RegisterBits.EIR_TXIF) == (byte)ENC28J60RegisterBits.EIR_TXIF)
            {
                /* TODO: should we analyze the ESTAT.TXABRT bit to see if our TX was aborted (unsuccessful)?  or analyze the 7-byte TX status vector? */

                // de-assert tx interrupt flag (complete)
                BitFieldClear(ENC28J60Register.EIR, (byte)ENC28J60RegisterBits.EIR_TXIF);
                // enable another frame to be sent
                _sendPacketTxBufferFreeEvent.Set();
            }
            //if ((eir & (byte)ENC28J60RegisterBits.EIR_TXERIF) == (byte)ENC28J60RegisterBits.EIR_TXERIF)
            //{
            //    // tx error
            //    BitFieldClear(ENC28J60Register.EIR, (byte)ENC28J60RegisterBits.EIR_TXERIF);
            //}
            if ((eir & (byte)ENC28J60RegisterBits.EIR_LINKIF) == (byte)ENC28J60RegisterBits.EIR_LINKIF)
            {
                // read our PHIR register to clear the LINKIF event
                ReadPhyRegister(ENC28J60PhyRegister.PHIR);

                bool lastLinkState = _lastLinkState;
                _lastLinkState = ((Netduino.IP.ILinkLayer)this).GetLinkState();
                // we also check the latching link bit (which will return "0" if the link is down or has been down since we last checked PHSTAT1_LLSTAT)...
                bool latchingLinkState = ((ReadPhyRegister(ENC28J60PhyRegister.PHSTAT1) & (UInt16)ENC28J60PhyRegisterBits.PHSTAT1_LLSTAT) == (UInt16)ENC28J60PhyRegisterBits.PHSTAT1_LLSTAT);
                if (lastLinkState == true && _lastLinkState == true && !latchingLinkState)
                {
                    // our link was and is up...but went down sometime in between.  raise an event for the missing "down" state.
                    if (OnLinkStateChanged != null)
                        OnLinkStateChanged(this, false);
                    lastLinkState = false;
                }

                // if our link state has changed, raise an event.
                if (lastLinkState != _lastLinkState)
                {
                    if (OnLinkStateChanged != null)
                        OnLinkStateChanged(this, _lastLinkState);
                }
            }

            // re-enable interrupts
            BitFieldSet(ENC28J60Register.EIE, (byte)ENC28J60RegisterBits.EIE_INTIE);
        }

        void ILinkLayer.Start()
        {
            Initialize();
        }

        void ILinkLayer.Stop()
        {
            // we are now un-initializing
            _isInitialized = false;

            // disable our interrupt pin
            _interruptPin.DisableInterrupt();

            // power down our network chip
            EnterPowerDownMode();
        }

        // this function restarts and initializes our network chip
        void Initialize()
        {
            _isInitialized = false;

            // hardware-reset our network chip
            if (_resetPin == null)
            {
                _resetPin = new OutputPort(_resetPinID, false);
            }
            else
            {
                _resetPin.Write(false);
            }
            // sleep for at least 400ns; we are sleeping for 2ms+ instead
            System.Threading.Thread.Sleep(1); // 1000us (1ms) should be plenty of time
            // take our hardware chip out of reset
            _resetPin.Write(true);

            // attempt to connect to network chip for 1000ms
            Int64 startTicks = Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks;
            byte estat;
            bool chipIsReady = false;
            do
            {
                /* ESTAT_CLKRDY indicates that the PHY is ready and we can safely read/write PHY registers.  Technically this means that the oscillator has stabilized.
                 * We could actually read/write Ethernet registers and setup our buffers before this bit gets set, but since it only takes about 300us to boot then we'll just wait the 300us. */
                estat = ReadControlRegister(ENC28J60Register.ESTAT);
                if (((estat & (byte)ENC28J60RegisterBits.ESTAT_CLKRDY) == (byte)ENC28J60RegisterBits.ESTAT_CLKRDY) && (estat != 0xFF))
                {
                    chipIsReady = true;
                    break;
                }
            } while ((Utility.GetMachineTime().Ticks - startTicks) / System.TimeSpan.TicksPerMillisecond < 1000);

            if (!chipIsReady)
                throw new Exception(); /* TODO: throw the proper exception for "could not connect to network interface */

            /* CONFIGURE RX BUFFER AND FILTERS */
            // configure RX buffer size; the TX buffer will automatically use the remaining SRAM
            // ERRATA NOTE: per ENC28J60 errata 5, the receive buffer effectively MUST begin at 0x000
            WriteControlRegister(ENC28J60Register.ERXSTL, ENC28J60Register.ERXSTH, READ_BUFFER_BEGIN); /* begin RX buffer at 0x0000 */
            UInt16 endOfReadBuffer = (READ_BUFFER_BEGIN + READ_BUFFER_SIZE - 1) % TOTAL_BUFFER_SIZE;
            WriteControlRegister(ENC28J60Register.ERXNDL, ENC28J60Register.ERXNDH, endOfReadBuffer); /* end RX buffer at READ_BUFFER_BEGIN+READ_BUFFER_SIZE-1, leaving the remaining bytes for the TX buffer */
            // set our read pointer (which we use to read memory) to the beginning of the read buffer.
            _retrievePacket_NextPacketPointer = READ_BUFFER_BEGIN;
            WriteControlRegister(ENC28J60Register.ERDPTL, ENC28J60Register.ERDPTH, _retrievePacket_NextPacketPointer);
            // set RX buffer pointer to end of the free portion of the RX buffer
            WriteControlRegister(ENC28J60Register.ERXRDPTL, ENC28J60Register.ERXRDPTH, endOfReadBuffer % 2 == 0 ? (UInt16)((endOfReadBuffer + TOTAL_BUFFER_SIZE - 1) % TOTAL_BUFFER_SIZE) : endOfReadBuffer); /* ERRATA NOTE: per ENC28J60 errata 14, this value must be odd */

            /* TODO: once we complete our IP stack, configure ERXFCON so that we don't get all packets forwarded to us--but only unicast packets and multicast/broadcast packets--including ARP/DHCP--that we want */
            // configure RX filters; by default we will set them up in OR mode (so any matched filter will accept the packet), enable unicast/(all)multicast/broadcast packets and discard any packets that fail CRC validation.
            /* TODO: consider disabling broadcast/multicast packets.  If we do this after initialization, see if we need to disable and re-enable the RX, etc. */
            /* NOTE: we'we should probably use PMEN (pattern match) instead of MCEN (multicast) for multicast; MCEN will receive ALL multicast packets including lots of packets we are not interested in */
            WriteControlRegister(ENC28J60Register.ERXFCON, (byte)(ENC28J60RegisterBits.ERXFCON_UCEN | ENC28J60RegisterBits.ERXFCON_CRCEN | ENC28J60RegisterBits.ERXFCON_BCEN));
            //WriteControlRegister(ENC28J60Register.ERXFCON, (byte)(ENC28J60RegisterBits.ERXFCON_UCEN | ENC28J60RegisterBits.ERXFCON_CRCEN | ENC28J60RegisterBits.ERXFCON_PMEN | ENC28J60RegisterBits.ERXFCON_BCEN));
            //WriteControlRegister(ENC28J60Register.ERXFCON, (byte)(ENC28J60RegisterBits.ERXFCON_UCEN | ENC28J60RegisterBits.ERXFCON_CRCEN | ENC28J60RegisterBits.ERXFCON_MCEN | ENC28J60RegisterBits.ERXFCON_BCEN));
            //WriteControlRegister(ENC28J60Register.ERXFCON, (byte)(ENC28J60RegisterBits.ERXFCON_UCEN | ENC28J60RegisterBits.ERXFCON_CRCEN));

            /* enable AUTOINC so that we can read/write frames without manually moving the pointer between every byte access. */
            BitFieldSet(ENC28J60Register.ECON2, (byte)ENC28J60RegisterBits.ECON2_AUTOINC);

            /* turn off our CLKOUT clock output; that is not needed */
            byte ecocon = ReadControlRegister(ENC28J60Register.ECOCON);
            ecocon &= 0xFF ^ 0x07;
            WriteControlRegister(ENC28J60Register.ECOCON, ecocon);

            /* enable low-current mode when powered down */
            BitFieldSet(ENC28J60Register.ECON2, (byte)ENC28J60RegisterBits.ECON2_VRPS);

            /* INITIALIZE MAC */
            // enable automatic frame padding (to at least 60 bytes) and automatic calculation/addition of CRC; for compatibility with all switches/devices, we will not enable full duplex
            byte macon3 = ReadControlRegister(ENC28J60Register.MACON3, true);
            macon3 |= (byte)ENC28J60RegisterBits.MACON3_PADCFG0; /* pad frames to at least 60 bytes */
            macon3 |= (byte)ENC28J60RegisterBits.MACON3_TXCRCEN;
            WriteControlRegister(ENC28J60Register.MACON3, macon3);
            // set our DEFER bit for 802.3 half duplex compliance
            byte macon4 = ReadControlRegister(ENC28J60Register.MACON4, true);
            macon4 |= (byte)ENC28J60RegisterBits.MACON4_DEFER;
            WriteControlRegister(ENC28J60Register.MACON4, macon4);
            // configure our maximum frame length (1518 bytes + 8 bytes for status vector and control byte)
            WriteControlRegister(ENC28J60Register.MAMXFLL, ENC28J60Register.MAMXFLH, MAX_TX_FRAME_LENGTH);
            // configure our MAC back-to-back inter-packet gap (for half-duplex, the recommended setting is 0x12 (9.6us)).
            WriteControlRegister(ENC28J60Register.MABBIPG, 0x12);
            // configure our MAC non-back-to-back inter-packet gap (for half-duplex, the recommended setting is 0x12 for low byte and 0x0C for high byte).
            WriteControlRegister(ENC28J60Register.MAIPGL, 0x12);
            WriteControlRegister(ENC28J60Register.MAIPGH, 0x0C);
            // since we are using half-duplex mode, configure the retransmission and collision window registers; we use the default (reset) values.
            WriteControlRegister(ENC28J60Register.MACLCON1, 0x0F);
            WriteControlRegister(ENC28J60Register.MACLCON2, 0x37);
            // set our MAC address
            WriteControlRegister(ENC28J60Register.MAADR1, _macAddress[0]);
            WriteControlRegister(ENC28J60Register.MAADR2, _macAddress[1]);
            WriteControlRegister(ENC28J60Register.MAADR3, _macAddress[2]);
            WriteControlRegister(ENC28J60Register.MAADR4, _macAddress[3]);
            WriteControlRegister(ENC28J60Register.MAADR5, _macAddress[4]);
            WriteControlRegister(ENC28J60Register.MAADR6, _macAddress[5]);

            /* INITIALIZE PHY */
            // configure PHCON1.PDPXMD; this should be set to half-duplex by external hardware config--but we double-check it to enforce half-duplex mode
            UInt16 phcon1 = ReadPhyRegister(ENC28J60PhyRegister.PHCON1);
            if (((UInt16)(phcon1 & (UInt16)ENC28J60PhyRegisterBits.PHCON1_PDPXMD) == (UInt16)ENC28J60PhyRegisterBits.PHCON1_PDPXMD))
            {
                // force change to half-duplex
                phcon1 &= (UInt16)(~ENC28J60PhyRegisterBits.PHCON1_PDPXMD);
                WritePhyRegister(ENC28J60PhyRegister.PHCON1, phcon1);
            }
            // set PHCON2.HDLDIS bit to prevent automatic loopback in half-duplex mode
            UInt16 phcon2 = ReadPhyRegister(ENC28J60PhyRegister.PHCON2);
            phcon2 |= (UInt16)ENC28J60PhyRegisterBits.PHCON2_HDLDIS;
            WritePhyRegister(ENC28J60PhyRegister.PHCON2, phcon2);
            // configure LEDs
            UInt16 phlcon = ReadPhyRegister(ENC28J60PhyRegister.PHLCON);
            phlcon |= (UInt16)ENC28J60PhyRegisterBits.PHLCON_STRCH; /* enable stretch */
            phlcon &= 0xFFFF ^ (0x3 << 2); /* clear stretch */
            phlcon |= (0x1 << 2); /* seleect medium stretch */
            phlcon &= 0xFFFF ^ (0xF << 4); /* clear LEDB */
            phlcon |= (0xD << 4); /* LEDB: Display link status and transmit/receive activity (always stretched) */
            phlcon &= 0xFFFF ^ (0xF << 8); /* clear LEDA */
            phlcon |= (0x9 << 8); /* LEDA: Off */
            WritePhyRegister(ENC28J60PhyRegister.PHLCON, phlcon);

            // enable MAC frame reception (i.e. enable packet reception)
            byte macon1 = ReadControlRegister(ENC28J60Register.MACON1, true);
            macon1 |= (byte)ENC28J60RegisterBits.MACON1_MARXEN;
            WriteControlRegister(ENC28J60Register.MACON1, macon1);
            // write incoming packets which pass our filters to be received into our RX buffer
            BitFieldSet(ENC28J60Register.ECON1, (byte)ENC28J60RegisterBits.ECON1_RXEN);

            // configure interrupts for INT pin (trigger on packet RX and link change)
            byte eie = ReadControlRegister(ENC28J60Register.EIE);
            eie |= (byte)ENC28J60RegisterBits.EIE_PKTIE; /* Receive Packet Pending Interrupt Enable */
            eie |= (byte)ENC28J60RegisterBits.EIE_LINKIE; /* Link Status Change Interrupt Enable */
            eie |= (byte)ENC28J60RegisterBits.EIE_INTIE; /* Global INT Interrupt Enable */
            WriteControlRegister(ENC28J60Register.EIE, eie);
            UInt16 phie = ReadPhyRegister(ENC28J60PhyRegister.PHIE);
            phie |= (UInt16)ENC28J60PhyRegisterBits.PHIE_PLNKIE; /* PHY Link Change Interrupt Enable */
            phie |= (UInt16)ENC28J60PhyRegisterBits.PHIE_PGEIE; /* PHY Global Interrupt Enable */
            WritePhyRegister(ENC28J60PhyRegister.PHIE, phie);
            // enable our interrupt pin
            _interruptPin.EnableInterrupt();

            // enable transmission via our TX buffer
            _sendPacketTxBufferFreeEvent.Set();

            // we are now initialized
            _isInitialized = true;
        }

        byte[] ILinkLayer.GetMacAddress()
        {
            return _macAddress;
        }

        void ILinkLayer.SetMacAddress(byte[] macAddress)
        {
            if (macAddress == null || macAddress.Length != 6)
                throw new ArgumentException();

            // write over our MAC address with the new MAC address
            Array.Copy(macAddress, _macAddress, _macAddress.Length);

            if (_isInitialized)
            {
                throw new NotSupportedException();
                /* TODO: if we're already started, re-initialize our MAC address and any other settings in the network chip */
            }
        }

        /* TODO: ERRATA 19 indicates that we cannot reset the chip while in power down mode.  Does this affect us? */
        void EnterPowerDownMode()
        {
            // turn off packet reception
            BitFieldClear(ENC28J60Register.ECON1, (byte)ENC28J60RegisterBits.ECON1_RXEN);
            // wait for any in-progress incoming frames to complete reception
            while ((ReadControlRegister(ENC28J60Register.ESTAT) & (byte)ENC28J60RegisterBits.ESTAT_RXBUSY) == (byte)ENC28J60RegisterBits.ESTAT_RXBUSY)
            { }
            // wait for any in-progress outgoing frame to complete transmission
            while ((ReadControlRegister(ENC28J60Register.ECON1) & (byte)ENC28J60RegisterBits.ECON1_TXRTS) == (byte)ENC28J60RegisterBits.ECON1_TXRTS)
            { }
            // put the chip into sleep mode
            BitFieldSet(ENC28J60Register.ECON2, (byte)ENC28J60RegisterBits.ECON2_PWRSV);
        }

        // ReadControlRegister retrieves the value of one of our network chip's internal registers
        /* if reading a MAC or MII register, set isMacOrMiiRegister to true (to add an extra byte delay between command and read */
        byte ReadControlRegister(ENC28J60Register registerAddress, bool isMacOrMiiRegister = false)
        {
            lock (_spiLock)
            {
                // before we read the register: if the register is not a common register, then select the register bank
                if (((UInt16)registerAddress & 0xFF00) != 0xFF00)
                {
                    byte registerBank = (byte)(((UInt16)registerAddress >> 8) & 0x03);
                    if (registerBank != _lastRegisterBank)
                    {
                        BitFieldClear(ENC28J60Register.ECON1, (byte)0x03);
                        BitFieldSet(ENC28J60Register.ECON1, registerBank);
                        _lastRegisterBank = registerBank;
                    }
                }

                // WriteBuffer byte 0 [Opcode]: ReadControlRegister (3 bits << 5) + register address (5 bits << 0)
                _readControlRegister_WriteBuffer[0] = (byte)((byte)ENC28J60Opcode.ReadControlRegister + ((byte)registerAddress & ENC28J60_SPI_INSTRUCTION_ARGUMENT_MASK));
                // WriteBuffer bytes 1-2: dummy data

                // write our ReadControlRegister command and retrieve the register data
                _chipSelectPin.Write(_chipSelectActiveLevel);
                _spi.WriteRead(_readControlRegister_WriteBuffer, 0, isMacOrMiiRegister ? 2 : 1, _readControlRegister_ReadBuffer, 0, 1, isMacOrMiiRegister ? 2 : 1);
                _chipSelectPin.Write(!_chipSelectActiveLevel);

                // return the register data as a byte
                return _readControlRegister_ReadBuffer[0];
            }
        }

        // ReadControlRegister retrieves the value of one of our network chip's internal registers
        UInt16 ReadControlRegister(ENC28J60Register registerAddressLow, ENC28J60Register registerAddressHigh, bool isMacOrMiiRegister = false)
        {
            /* NOTE: this overload of ReadControlRegister reads a set of LOW/HIGH registers */
            lock (_spiLock)
            {
                /* reading low/high bytes requires no specific order; it _is_ possible that one is written between transactions...so in theory these values COULD be corrupted */
                byte lowByte = ReadControlRegister(registerAddressLow, isMacOrMiiRegister);
                byte highByte = ReadControlRegister(registerAddressHigh, isMacOrMiiRegister);
                return (UInt16)((((UInt16)highByte) << 8) + lowByte);
            }
        }

        // WriteControlRegister sets the value of one of our network chip's internal registers
        void WriteControlRegister(ENC28J60Register registerAddress, byte value)
        {
            lock (_spiLock)
            {
                // before we write the register: if the register is not a common register, then select the register bank
                if (((UInt16)registerAddress & 0xFF00) != 0xFF00)
                {
                    byte registerBank = (byte)(((UInt16)registerAddress >> 8) & 0x03);
                    if (registerBank != _lastRegisterBank)
                    {
                        BitFieldClear(ENC28J60Register.ECON1, (byte)0x03);
                        BitFieldSet(ENC28J60Register.ECON1, registerBank);
                        _lastRegisterBank = registerBank;
                    }
                }

                // WriteBuffer byte 0 [Opcode]: WriteControlRegister (3 bits << 5) + register address (5 bits << 0)
                _writeControlRegister_WriteBuffer[0] = (byte)((byte)ENC28J60Opcode.WriteControlRegister + ((byte)registerAddress & ENC28J60_SPI_INSTRUCTION_ARGUMENT_MASK));
                // WriteBuffer byte 1: data
                _writeControlRegister_WriteBuffer[1] = value;

                _chipSelectPin.Write(_chipSelectActiveLevel);
                _spi.Write(_writeControlRegister_WriteBuffer);
                _chipSelectPin.Write(!_chipSelectActiveLevel);
            }
        }

        // WriteControlRegister sets the value of one of our network chip's internal registers
        void WriteControlRegister(ENC28J60Register registerAddressLow, ENC28J60Register registerAddressHigh, UInt16 value)
        {
            /* NOTE: this overload of WriteControlRegister writes a set of LOW/HIGH registers in the required order */
            lock (_spiLock)
            {
                /* writing LOW byte will buffer the LOW byte */
                WriteControlRegister(registerAddressLow, (byte)(value & 0xFF));
                /* writing HIGH byte will also retrieve the buffered LOW byte and write the full value in a single transaction */
                WriteControlRegister(registerAddressHigh, (byte)(value >> 8));
            }
        }

        UInt16 ReadPhyRegister(ENC28J60PhyRegister registerAddress)
        {
            lock (_spiLock)
            {
                // write the PHY register address to MIREGADR
                WriteControlRegister(ENC28J60Register.MIREGADR, (byte)registerAddress);
                // set the MICMD.MIIRD bit to retrieve the register value
                byte micmd = ReadControlRegister(ENC28J60Register.MICMD, true);
                micmd |= (byte)ENC28J60RegisterBits.MICMD_MIIRD;
                WriteControlRegister(ENC28J60Register.MICMD, micmd);

                // wait for the PHY register value to be read successfully
                Int64 startTicks = Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks;
                bool miiOperationSuccess = false;
                byte mistat;
                do
                {
                    mistat = ReadControlRegister(ENC28J60Register.MISTAT, true);
                    if ((mistat & (byte)ENC28J60RegisterBits.MISTAT_BUSY) == 0x00)
                    {
                        miiOperationSuccess = true;
                        break;
                    }
                } while ((Utility.GetMachineTime().Ticks - startTicks) / System.TimeSpan.TicksPerMillisecond < 20); // wait up to 20ms

                if (!miiOperationSuccess)
                    throw new Exception(); /* TODO: this should be a "could not configure network interface" exception */

                // retrieve our register value
                UInt16 returnValue = ReadControlRegister(ENC28J60Register.MIRDL, ENC28J60Register.MIRDH, true);

                // clear the MICMD.MIIRD bit
                micmd &= (byte)~ENC28J60RegisterBits.MICMD_MIIRD;
                WriteControlRegister(ENC28J60Register.MICMD, micmd);

                // return our register value
                return returnValue;
            }
        }

        void WritePhyRegister(ENC28J60PhyRegister registerAddress, UInt16 value)
        {
            lock (_spiLock)
            {
                // write the PHY register address to MIREGADR
                WriteControlRegister(ENC28J60Register.MIREGADR, (byte)registerAddress);
                // write the value to our MIWRL and MIWRH registers, in that order.
                WriteControlRegister(ENC28J60Register.MIWRL, ENC28J60Register.MIWRH, value);

                // wait for the PHY register value to be written successfully
                Int64 startTicks = Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks;
                bool miiOperationSuccess = false;
                byte mistat;
                do
                {
                    mistat = ReadControlRegister(ENC28J60Register.MISTAT, true);
                    if ((mistat & (byte)ENC28J60RegisterBits.MISTAT_BUSY) == 0x00)
                    {
                        miiOperationSuccess = true;
                        break;
                    }
                } while ((Utility.GetMachineTime().Ticks - startTicks) / System.TimeSpan.TicksPerMillisecond < 20); // wait up to 20ms

                if (!miiOperationSuccess)
                    throw new Exception(); /* TODO: this should be a "could not configure network interface" exception */
            }
        }

        // BitFieldSet sets bits in one of our network chip's internal registers while not affecting unselected bits
        void BitFieldSet(ENC28J60Register registerAddress, byte bitmask)
        {
            lock (_spiLock)
            {
                // before we write the register: if the register is not a common register, then select the register bank
                if (((UInt16)registerAddress & 0xFF00) != 0xFF00)
                {
                    byte registerBank = (byte)(((UInt16)registerAddress >> 8) & 0x03);
                    if (registerBank != _lastRegisterBank)
                    {
                        BitFieldClear(ENC28J60Register.ECON1, (byte)0x03);
                        BitFieldSet(ENC28J60Register.ECON1, registerBank);
                        _lastRegisterBank = registerBank;
                    }
                }

                // WriteBuffer byte 0 [Opcode]: BitFieldSet (3 bits << 5) + register address (5 bits << 0)
                _writeControlRegister_WriteBuffer[0] = (byte)((byte)ENC28J60Opcode.BitFieldSet + ((byte)registerAddress & ENC28J60_SPI_INSTRUCTION_ARGUMENT_MASK));
                // WriteBuffer byte 1: data
                _writeControlRegister_WriteBuffer[1] = bitmask;

                _chipSelectPin.Write(_chipSelectActiveLevel);
                _spi.Write(_writeControlRegister_WriteBuffer);
                _chipSelectPin.Write(!_chipSelectActiveLevel);
            }
        }

        // BitFieldClear sets bits in one of our network chip's internal registers while not affecting unselected bits
        void BitFieldClear(ENC28J60Register registerAddress, byte bitmask)
        {
            lock (_spiLock)
            {
                // before we write the register: if the register is not a common register, then select the register bank
                if (((UInt16)registerAddress & 0xFF00) != 0xFF00)
                {
                    byte registerBank = (byte)(((UInt16)registerAddress >> 8) & 0x03);
                    if (registerBank != _lastRegisterBank)
                    {
                        BitFieldClear(ENC28J60Register.ECON1, (byte)0x03);
                        BitFieldSet(ENC28J60Register.ECON1, registerBank);
                        _lastRegisterBank = registerBank;
                    }
                }

                // WriteBuffer byte 0 [Opcode]: BitFieldClear (3 bits << 5) + register address (5 bits << 0)
                _writeControlRegister_WriteBuffer[0] = (byte)((byte)ENC28J60Opcode.BitFieldClear + ((byte)registerAddress & ENC28J60_SPI_INSTRUCTION_ARGUMENT_MASK));
                // WriteBuffer byte 1: data
                _writeControlRegister_WriteBuffer[1] = bitmask;

                _chipSelectPin.Write(_chipSelectActiveLevel);
                _spi.Write(_writeControlRegister_WriteBuffer);
                _chipSelectPin.Write(!_chipSelectActiveLevel);
            }
        }

        // ReadBufferMemory copies a partial byte array from our chip's TX buffer to a local buffer
        void ReadBufferMemory(byte[] buffer, int offset, int count)
        {
            lock (_spiLock)
            {
                // WriteBuffer byte 0 [Opcode]: WriteBufferMemory (3 bits << 5) + 0b11010 (5 bits << 0)
                _readBufferMemory_WriteBuffer[0] = (byte)((byte)ENC28J60Opcode.ReadBufferMemory + ((byte)0x1A & ENC28J60_SPI_INSTRUCTION_ARGUMENT_MASK));

                _chipSelectPin.Write(_chipSelectActiveLevel);
                _spi.Write(_readBufferMemory_WriteBuffer);
                /* TODO: extend _spi with a .Read() function that just sends out dummy bytes...or at least makes it so we don't have to pass in an array of data to send */
                _spi.WriteRead(buffer, 0, count, buffer, offset, count, 0); /* NOTE: _writeBufferMemory_ReadBuffer is a dummy, zero-byte array; can we pass null instead? */
                _chipSelectPin.Write(!_chipSelectActiveLevel);
            }
        }

        // WriteBufferMemory copies a partial byte array to our chip's TX buffer
        /* NOTE: desinationIndex should be an even value */
        void WriteBufferMemory(byte[] buffer, int offset, int count)
        {
            lock (_spiLock)
            {
                // WriteBuffer byte 0 [Opcode]: WriteBufferMemory (3 bits << 5) + 0b11010 (5 bits << 0)
                _writeBufferMemory_WriteBuffer[0] = (byte)((byte)ENC28J60Opcode.WriteBufferMemory + ((byte)0x1A & ENC28J60_SPI_INSTRUCTION_ARGUMENT_MASK));

                _chipSelectPin.Write(_chipSelectActiveLevel);
                _spi.Write(_writeBufferMemory_WriteBuffer);
                _spi.WriteRead(buffer, offset, count, _writeBufferMemory_ReadBuffer, 0, 0, 0); /* NOTE: _writeBufferMemory_ReadBuffer is a dummy, zero-byte array; can we pass null instead? */
                _chipSelectPin.Write(!_chipSelectActiveLevel);
            }
        }

        // returns the latest packet from the RX buffer
        /* WARNING: the returned packet is a reference to a single internal buffer.  The data must be retrieved and processed before calling RetrievePacket again. 
                    this pattern was chosen to minimize garbage collection, by reusing an existing buffer without freeing/creating large extraneous objects on the heap. */
        void RetrievePacket(out byte[] buffer, out int index, out int count)
        {
            try
            {
                // set our read pointer to the previous "next packet pointer"
                WriteControlRegister(ENC28J60Register.ERDPTL, ENC28J60Register.ERDPTH, _retrievePacket_NextPacketPointer);

                // retrieve "next packet pointer"
                ReadBufferMemory(_retrievePacket_NextPacketPointerBuffer, 0, 2);
                _retrievePacket_NextPacketPointer = (UInt16)((_retrievePacket_NextPacketPointerBuffer[1] << 8) + _retrievePacket_NextPacketPointerBuffer[0]);

                // retrieve receive status vector (including packet length)
                byte[] receiveStatusVector = new byte[4];
                ReadBufferMemory(receiveStatusVector, 0, 4);
                count = (UInt16)((receiveStatusVector[1] << 8) + receiveStatusVector[0]);
                if (count == 0)
                {
                    //Debug.Print("zero length packet! packetLength: " + count);
                    buffer = null;
                    index = 0;
                    //count = 0;
                    return;
                }

                // retrieve our packet
                ReadBufferMemory(_incomingFrame, 0, count);

                // change our buffer array to point to _incomingFrame
                buffer = _incomingFrame;
                // calculate index and count values for our caller
                index = 0; // packet starts at beginning of buffer (since we retrieved the next packet pointer and receive status vector separately)
                //count = packetLength; // return the ACTUAL packet length (not counting headers, etc.)

                // advance our "read buffer pointer"
                if (_retrievePacket_NextPacketPointer == READ_BUFFER_BEGIN)
                {
                    UInt16 endOfReadBuffer = (READ_BUFFER_BEGIN + READ_BUFFER_SIZE - 1) % TOTAL_BUFFER_SIZE;
                    WriteControlRegister(ENC28J60Register.ERXRDPTL, ENC28J60Register.ERXRDPTH, endOfReadBuffer % 2 == 0 ? (UInt16)((endOfReadBuffer + TOTAL_BUFFER_SIZE - 1) % TOTAL_BUFFER_SIZE) : endOfReadBuffer); /* ERRATA NOTE: per ENC28J60 errata 14, this value must be odd */
                }
                else
                {
                    WriteControlRegister(ENC28J60Register.ERXRDPTL, ENC28J60Register.ERXRDPTH, _retrievePacket_NextPacketPointer % 2 == 0 ? (UInt16)((_retrievePacket_NextPacketPointer + TOTAL_BUFFER_SIZE - 1) % TOTAL_BUFFER_SIZE) : _retrievePacket_NextPacketPointer); /* ERRATA NOTE: per ENC28J60 errata 14, this value must be odd */
                }
            }
            finally
            {
                /* set ECON2_PKTDEC to decrement our packet counter; this will also de-assert the EIR_PKTIF interrupt flag */
                BitFieldSet(ENC28J60Register.ECON2, (byte)ENC28J60RegisterBits.ECON2_PKTDEC);
            }
        }

        /* NOTE on ENC28J60: while the receive buffer is a circular FIFO buffer, the transmit buffer is not a circular buffer.  The ENC28J60 does not guarantee by design that a transmit packet may overlap the 
         * end of the TXqueue.  Because of this and because most overflow and speed issues are related to incoming packets, we have effectively made the TX buffer the size of a single Ethernet frame.  We have 
         * consequently made the TX buffer a single-frame buffer, waiting on the current TX frame to transmit before enabling another frame to be queued.
         * It is possible to write additional frames (using the EWRPT pointer and WriteBufferMemory) into non-used (presumably non-wrapping) portions of the TX buffer--remembering to reserve at least 8 bytes 
         * beyond the end of frame for the 7-byte TX status vector and the required 1 byte of unused buffer.  As the TX buffer may fit as few as one frame, this may not deliver noticable speed benefits. */
        void ILinkLayer.SendFrame(int numBuffers, byte[][] buffer, int[] index, int[] count, Int64 timeoutInMachineTicks)
        {
            // if another packet is currently being transmitted, wait here (but do not wait longer than our expiration).
            Int32 millisecondsUntilTimeout = (Int32)((timeoutInMachineTicks != Int64.MaxValue) ? (timeoutInMachineTicks - Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks) / System.TimeSpan.TicksPerMillisecond : Int32.MaxValue);
            if (millisecondsUntilTimeout < 0) millisecondsUntilTimeout = 0;
            if (!_sendPacketTxBufferFreeEvent.WaitOne(millisecondsUntilTimeout, false)) return; /* if we timeout, drop the frame */

            int totalBufferLength = 0;
            for (int i = 0; i < numBuffers; i++)
            {
                totalBufferLength += count[i];
            }

            /* TODO: we should move the following errata logic to the interrupt event; we should also make sure we are dealing with retransmission aborted/failed frames if/when required. */
            /* ERRATA NOTE: per ENC28J60 errata 12, if a previous frame transmission was aborted this can sometimes cause a stall of the internal transmit logic; as the recommended workaround, we should
             * set and then clear ECON_TXRST...and then clear the EIR_TXERIF flag.  This purpose-aborted empty frame will reset the internal transmit logic. */
            if ((ReadControlRegister(ENC28J60Register.EIR) & (byte)ENC28J60RegisterBits.EIR_TXERIF) == (byte)ENC28J60RegisterBits.EIR_TXERIF)
            {
                // set our frame TX range to a single byte...in case the chip is so fast that it tries to send the bogus frame while we're dealing with this errata.
                WriteControlRegister(ENC28J60Register.ETXSTL, ENC28J60Register.ETXSTH, WRITE_BUFFER_BEGIN);
                WriteControlRegister(ENC28J60Register.ETXNDL, ENC28J60Register.ETXNDH, WRITE_BUFFER_BEGIN);
                // set and clear our ECON_TXRST flag
                BitFieldSet(ENC28J60Register.ECON1, (byte)ENC28J60RegisterBits.ECON1_TXRTS);
                BitFieldClear(ENC28J60Register.ECON1, (byte)ENC28J60RegisterBits.ECON1_TXRTS);
                // clear our TX error flag
                BitFieldClear(ENC28J60Register.EIR, (byte)ENC28J60RegisterBits.EIR_TXERIF);
            }

            UInt16 txBufferStart = WRITE_BUFFER_BEGIN; /* NOTE: this driver always starts frames at the beginning of the TX buffer */
            // set our TX buffer start pointer (first position occupied by frame)
            WriteControlRegister(ENC28J60Register.ETXSTL, ENC28J60Register.ETXSTH, txBufferStart);
            // set our TX buffer write pointer (current position to write)
            WriteControlRegister(ENC28J60Register.EWRPTL, ENC28J60Register.EWRPTH, txBufferStart);

            // write our control byte to the TX buffer
            _sendPacketControlByte[0] = 0x00; /* do not override MACON3 padding/CRC/etc. settings for this packet */
            WriteBufferMemory(new byte[] { 0 }, 0, 1);
            // write our frame(s) to the TX buffer
            for (int i = 0; i < numBuffers; i++)
            {
                WriteBufferMemory(buffer[i], index[i], count[i]);
            }

            // set our TX buffer end pointer (last position occupied by frame)
            UInt16 txBufferFrameEnd = (UInt16)(txBufferStart + 1 + totalBufferLength - 1); /* TODO: deal with buffer rollover */
            WriteControlRegister(ENC28J60Register.ETXNDL, ENC28J60Register.ETXNDH, txBufferFrameEnd);

            // clear our TX interrupt flag
            //BitFieldClear(ENC28J60Register.EIR, (byte)(ENC28J60RegisterBits.EIR_TXIF | ENC28J60RegisterBits.EIR_TXERIF));
            BitFieldClear(ENC28J60Register.EIR, (byte)ENC28J60RegisterBits.EIR_TXIF);

            // enable our TX interrupt
            //BitFieldSet(ENC28J60Register.EIE, (byte)(ENC28J60RegisterBits.EIE_TXIE | ENC28J60RegisterBits.EIE_TXERIE | ENC28J60RegisterBits.EIE_INTIE));
            BitFieldSet(ENC28J60Register.EIE, (byte)(ENC28J60RegisterBits.EIE_TXIE | ENC28J60RegisterBits.EIE_INTIE));

            // enable frame transmission
            BitFieldSet(ENC28J60Register.ECON1, (byte)ENC28J60RegisterBits.ECON1_TXRTS);

            /* TODO: eventually, add a timeout waiting for the TXI interrupt to be fired: we can either abort the TX transmission by clearing ECON1_TXRTS after a timer timeout...or wait here for timeout. */
        }

        bool ILinkLayer.GetLinkState()
        {
            UInt16 phstat2 = ReadPhyRegister(ENC28J60PhyRegister.PHSTAT2);
            return ((phstat2 & (UInt16)ENC28J60PhyRegisterBits.PHSTAT2_LSTAT) == (UInt16)ENC28J60PhyRegisterBits.PHSTAT2_LSTAT);
        }


        event LinkStateChangedEventHandler ILinkLayer.LinkStateChanged
        {
            add { OnLinkStateChanged += value; }
            remove { OnLinkStateChanged -= value; }
        }


        event PacketReceivedEventHandler ILinkLayer.PacketReceived
        {
            add { OnPacketReceived += value; }
            remove { OnPacketReceived -= value; }
        }
    }
}