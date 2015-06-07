// Netduino.IP Stack
// Copyright (c) 2015 Secret Labs LLC. All rights reserved.
// Licensed under the Apache 2.0 License

using Microsoft.SPOT;
using Microsoft.SPOT.Hardware;
using System;

namespace Netduino.IP.LinkLayers
{
    public class AX88796C : Netduino.IP.ILinkLayer
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

        //const int MAX_RX_FRAME_LENGTH = 1536;
        const int MAX_RX_FRAME_LENGTH = 2000;

        // an ID for our PHY; we re-set this every time we initialize in case it was changed by other code
        byte _phyID = 0x10;

        bool _isInitialized = false;

        event PacketReceivedEventHandler OnPacketReceived;
        event LinkStateChangedEventHandler OnLinkStateChanged;
        bool _lastLinkState = false;

        // fixed buffers for reading/writing registers
        byte[] _readGlobalRegister_WriteBuffer = new byte[6];
        byte[] _readGlobalRegister_ReadBuffer = new byte[2];
        byte[] _writeGlobalRegister_WriteBuffer = new byte[4];

        // fixed buffer for exiting power down mode
        byte[] _exitPowerDownMode_WriteBuffer = new byte[1];

        // fixed buffer for reading status
        byte[] _readStatus_WriteBuffer = new byte[4];
        byte[] _readStatus_ReadBuffer = new byte[3];

        // fixed buffers for reading/writing TX/RX buffers
        byte[] _writeTxqBuffer_WriteBuffer = new byte[4];
        byte[] _writeTxqBuffer_ExtraPadding = new byte[3];
        byte[] _readTxqBuffer_WriteBuffer = new byte[5];
        byte[] _retrievePacketHeader_ReadBuffer = new byte[6];

        // fixed buffers for incoming data
        byte[] _incomingFrame = new byte[MAX_RX_FRAME_LENGTH];

        // fixed buffers for sending packets
        byte[] _sendPacketSopHeader = new byte[8];
        byte[] _sendPacketEopHeader = new byte[4];
        byte _sendPacketSequenceNumber = 0;
        // synchronization event for waiting on TX buffers to become free
        System.Threading.AutoResetEvent _sendPacketTxPagesFreeEvent;

        /* TODO: rethink the name of this enum; rethink its location--here or in an interface etc. rethink speed and also duplex settings too */
        public enum PhySpeedOption : byte
        {
            AutoNegotiate = 0,
            Speed10 = 1,
            Speed100 = 2,
        }
        PhySpeedOption _phySpeed = PhySpeedOption.AutoNegotiate;

        // AX88796C opcodes
        enum AX88796COpcode : byte
        {
            ReadGlobalRegister  = 0x03,
            WriteGlobalRegister = 0xD8,
            ReadRxq             = 0x0B,
            WriteTxq            = 0x02,
            //ReadStatus          = 0x05,
            //EnableQcsMode       = 0x38,
            //ExitPowerDown       = 0xAB,
            //ResetSpiMode        = 0xFF,
            //ReadWriteRxqTxq     = 0xB2,
        }

        // AX88796CRegister registers
        enum AX88796CRegister : byte
        {
            PSR     = 0x00, // PSR (Page Select Register)
            //BOR     = 0x02,
            FER     = 0x04, // FER (Function Enable Register)
            ISR     = 0x06, // ISR (Interrupt Status Register)
            IMR     = 0x08, // IMR (Interrupt Mask Register)
            WFCR    = 0x0A, // WFCR (Wakeup Frame Configuration Register)
            PSCR    = 0x0C, // PSCR (Power Saving Configuration Register)
            //MACCR   = 0x0E,
            TFBFCR  = 0x10, // TFBFCR (TX Free Buffer Count Register)
            TSNR    = 0x12, // TSNR (TX Sequence Number Register)
            //RTDPR   = 0x14,
            RXBCR1  = 0x16, // RXBCR1 (RX Bridge Control Register 1)
            RXBCR2  = 0x18, // RXBCR2 (RX Bridge Control Register 2)
            RTWCR   = 0x1A, // RTWCR (RX Total Word Count Register)
            RCPHR   = 0x1C, // RCPHR (RX Current Packet Header Register)
            //RWR     = 0x1E,
            //// 0x20
            RPPER   = 0x22, // RPPER (RX Packet Process Enable Register)
            //// 0x24
            //// 0x26
            //MRCR    = 0x28,
            //MDR     = 0x2A,
            //RMPR    = 0x2C,
            //TMPR    = 0x2E,
            RXBSPCR = 0x30, // RXBSPCR (RX Bridge Stuffing Packet Control Register)
            //RXMCR   = 0x32,
            //// 0x34
            //// 0x36
            //// 0x38
            //// 0x3A
            //// 0x3C
            //// 0x3E
            //// 0x40
            //ICR     = 0x42,
            PCR     = 0x44, // PCR (PHY Control Register)
            //PHYSR   = 0x46,
            MDIODR  = 0x48, // MDIODR (MDIO Read/Write Data Register)
            MDIOCR  = 0x4A, // MDIOCR (MDIO Read/Write Control Register)
            LCR0    = 0x4C, // LCR0 (LED Control Register 0)
            LCR1    = 0x4E, // LCR1 (LED Control Register 1)
            //IPGCR   = 0x50,
            //CRIR    = 0x52,
            //FLHWCR  = 0x54,
            RXCR    = 0x56, // RXCR (RX Control Register)
            //JLCR    = 0x58,
            //// 0x5A
            //MPLR    = 0x5C,
            //// 0x5C
            //// 0x60
            MACASR0 = 0x62, // MACASR0 (MAC Address Setup Register 0)
            MACASR1 = 0x64, // MACASR1 (MAC Address Setup Register 1)
            MACASR2 = 0x66, // MACASR2 (MAC Address Setup Register 2)
            //MFAR01  = 0x68,
            //MFAR23  = 0x6A,
            //MFAR45  = 0x6C,
            //MFAR67  = 0x6E,
            //VID0FR  = 0x70,
            //VID1FR  = 0x72,
            //EECSR   = 0x74,
            //EEDR    = 0x76,
            //EECR    = 0x78,
            //TPCR    = 0x7A,
            //TPLR    = 0x7C,
            //// 0x7E
            //// 0x80
            //GPIOER  = 0x82,
            //GPIOCR  = 0x84,
            //GPIOWCR = 0x86,
            //// 0x88
            //SPICR   = 0x8A, // SPICR (SPI Configuration Register)
            //SPIISMR = 0x8C,
            //// 0x8E
            //// 0x90
            COERCR0 = 0x92, // COERCR0 (COE RX Control Register 0)
            //COERCR1 = 0x94,
            //COETCR0 = 0x96,
            //COETCR1 = 0x98,
            //// 0x9A
            //// 0x9C
            //// 0x9E
            //// 0xA0
            //WFTR    = 0xA2,
            //WFCCR   = 0xA4,
            //WFCR03  = 0xA6,
            //WFCR47  = 0xA8,
            //WF0BMR0 = 0xAA,
            //WF0BMR1 = 0xAC,
            //WF0CR   = 0xAE,
            //WF0OBR  = 0xB0,
            //WF1BMR0 = 0xB2,
            //WF1BMR1 = 0xB4,
            //WF1CR   = 0xB6,
            //WF1OBR  = 0xB8,
            //WF2BMR0 = 0xBA,
            //WF2BMR1 = 0xBC,
            //// 0xBE
            //// 0xC0
            //WF2CR   = 0xC2,
            //WF2OBR  = 0xC4,
            //WF3BMR0 = 0xC6,
            //WF3BMR1 = 0xC8,
            //WF3CR   = 0xCA,
            //WF3OBR  = 0xCC,
            //WF4BMR0 = 0xCE,
            //WF4BMR1 = 0xD0,
            //WF4CR   = 0xD2,
            //WF4OBR  = 0xD4,
            //WF5BMR0 = 0xD6,
            //WF5BMR1 = 0xD8,
            //WF5CR   = 0xDA,
            //WF5OBR  = 0xDC,
            //// 0xDE
            //// 0xE0
            //WF6BMR0 = 0xE2,
            //WF6BMR1 = 0xE4,
            //WF6CR   = 0xE6,
            //WF6OBR  = 0xE8,
            //WF7BMR0 = 0xEA,
            //WF7BMR1 = 0xEC,
            //WF7CR   = 0xEE,
            //WF7OBR  = 0xF0,
            //WFR01   = 0xF2,
            //WFR23   = 0xF4,
            //WFR45   = 0xF6,
            //WFR67   = 0xF8,
            //WFPC0   = 0xFA,
            //WFPC1   = 0xFC,
            //RWR2    = 0xFE   /* duplicate RWR? */
        }

        // AX88796C registers' bit options and flags
        enum AX88796CRegisterBits : ushort
        {
            // PSR (Page Select Register)
            PSR_DEVICE_READY = (1 << 7),
            //PSR_N_SOFTRESET = (1 << 15),
            // FER (Function Enable Register)
            FER_BRIDGE_BYTE_SWAP = (1 << 9),
            FER_RX_BRIDGE_ENABLE = (1 << 14),
            FER_TX_BRIDGE_ENABLE = (1 << 15),
            // ISR (Interrupt Status Register)
            ISR_RXPCT = (1 << 0),
            ISR_TXPAGES_FREE = (1 << 6),
            ISR_TXERR = (1 << 8),
            ISR_LINKCHANGE = (1 << 9),
            // IMR (Interrupt Mask Register)
            IMR_RXPCT_MASK = (1 << 0),
            IMR_TXERR_MASK = (1 << 8),
            IMR_LINKCHANGE_MASK = (1 << 9),
            // WFCR (Wakeup Framework Configuration Register)
            WFCR_ENTER_SLEEPMODE = (1 << 4),
            // PSCR (Power Saving Configuration Register)
            PSCR_POWER_SAVING_CONFIG_MASK = (0x7 << 0),
            PSCR_POWER_SAVING_DISABLE = (0x0 << 0),
            PSCR_POWER_SAVING_LEVEL_1 = (0x1 << 0),
            PSCR_POWER_SAVING_LEVEL_2 = (0x2 << 0),
            PSCR_PHY_IN_LINK_STATE = (1 << 14),
            // TFBFCR (TX Free Buffer Count Register)
            TFBFCR_FREE_BUFFER_COUNT_MASK = 0x007F,
            TFBFCR_ACTIVATE_TXPAGES_INTERRUPT = (1 << 13),
            // TSNR (TX Sequence Number Register)
            TSNR_TXB_IDLE = (1 << 6),
            TSNR_TXB_REINITIALIZE = (1 << 14),
            TSNR_TXB_START = (1 << 15),
            // RXBCR1 (RX Bridge Control Register 1)
            RXBCR1_RXB_START = (1 << 15),
            // RXBCR2 (RX Bridge Control Register 2)
            RXBCR2_RX_PACKET_COUNT_MASK = 0xFF,
            RXBCR2_RXB_IDLE = (1 << 14),
            RXBCR2_RXB_REINITIALIZE = (1 << 14),
            // RTWCR (RX Total Word Count Register)
            RTWCR_RX_LATCH = (1 << 15),
            // RCPHR (RX Current Packet Header Register)
            RCPHR_PACKET_LENGTH_MASK = 0x07FF,
            // RPPER (RX Packet Process Enable Register)
            RPPER_RX_PACKET_ENABLE = (1 << 0),
            // RXBSPCR (RX Bridge Stuffing Packet Control Register)
            RXBSPCR_ENABLE_STUFFING = (1 << 15),
            // PCR (PHY Control Register)
            PCR_AUTO_POLL_ENABLE = (1 << 0),
            PCR_POLL_FLOW_CONTROL = (1 << 1),
            PCR_POLL_SELECT_MR0 = (1 << 2),
            // MDIOCR (MDIO Read/Write Control Register)
            MDIOCR_MDIO_READWRITE_COMPLETE = (1 << 13),
            MDIOCR_MDIO_READ = (1 << 14),
            MDIOCR_MDIO_WRITE = (1 << 15),
            // LCR0 (LED Control Register 0)
            LCR0_LED0_OPTION_DISABLED = 0,
            LCR0_LED1_OPTION_DISABLED = 0,
            LCR0_LED1_OPTION_LINKACT = (0x09 << 8),
            // LCR1 (LED Control Register 1)
            LCR1_LED2_OPTION_DISABLED = 0,
            LCR1_LED2_OPTION_LINKACT = (0x09 << 0),
            // RXCR (RX Control Register)
            RXCR_PACKET_TYPE_PROMISCUOUS = (1 << 0),
            RXCR_PACKET_TYPE_ALLMULTICAST = (1 << 1),
            RXCR_PACKET_TYPE_BROADCAST = (1 << 3),
            RXCR_PACKET_TYPE_MULTICAST = (1 << 4),
            // SPICR (SPI Configuration Register)
            //SPICR_REGISTER_COMPRESSION = (1 << 0),
            //SPICR_QUEUE_COMPRESSION = (1 << 1),
            // COERCR0 (COE RX Control Register 0)
            COERCR0_RXIPCE = (1 << 0),
            COERCR0_RXTCPE = (1 << 3),
            COERCR0_RXUDPE = (1 << 4),
        }

        enum AX88796CPhyRegister : byte
        {
            MR0 = 0x00, // MR0 (Basic Mode Control Register)
            MR4 = 0x04, // MR4 (Auto Negotiation Advertisement Register)
        }

        enum AX88796CPhyRegisterBits : ushort
        {
            // MR0 (Basic Mode Control Register)
            MR0_FULL_DUPLEX = (1 << 8),
            MR0_RESTART_AUTONEGOTIATION = (1 << 9),
            MR0_AUTO_NEGOTIATION_ENABLE = (1 << 12),
            MR0_SPEED_SELECTION_100 = (1 << 13), // NOTE: must be set to 1 if auto negotiation is enabled
            // MR4 (Auto Negotation Advertisement Register)
            MR4_SPEED_DUPLEX_AUTO = 0x05E1,
            MR4_SPEED_DUPLEX_100FULL = 0x0501,
            MR4_SPEED_DUPLEX_100HALF = 0x0081,
            MR4_SPEED_DUPLEX_100ANY = 0x0581,
            MR4_SPEED_DUPLEX_10FULL = 0x0441,
            MR4_SPEED_DUPLEX_10HALF = 0x0021,
            MR4_SPEED_DUPLEX_10ANY = 0x0461,
        }

        public AX88796C(SPI.SPI_module spiBusID, Cpu.Pin csPinID, Cpu.Pin intPinID, Cpu.Pin resetPinID, Cpu.Pin wakeupPinID)
        {
            // create our chip select pin and SPI bus objects
            _chipSelectPin = new OutputPort(csPinID, true);
            _spi = new SPI(new SPI.Configuration(Cpu.Pin.GPIO_NONE, false, 0, 0, false, true, 40000, spiBusID));

            // wire up our interrupt, for future use
            _interruptPin = new InterruptPort(intPinID, true, Port.ResistorMode.PullUp, Port.InterruptMode.InterruptEdgeLow);
            _interruptPin.DisableInterrupt();
            _interruptPin.OnInterrupt += _interruptPin_OnInterrupt;

            // save our reset pin ID (which we will use to control the reset pin a bit later on)
            _resetPinID = resetPinID;

            // save our wakeup pin ID (which we can use to create an InterruptPort right before we go to sleep)
            _wakeupPinID = wakeupPinID;

            // create our _sendPacketTxPagesFreeEvent
            _sendPacketTxPagesFreeEvent = new System.Threading.AutoResetEvent(false);

            // we are not initialized; we will initialize when we are started. 
            _isInitialized = false;
        }

        void _interruptPin_OnInterrupt(uint data1, uint data2, DateTime time)
        {
            if (!_isInitialized)
                return;

            // disable interrupts
            UInt16 imr = ReadGlobalRegister(AX88796CRegister.IMR);
            WriteGlobalRegister(AX88796CRegister.IMR, 0xFFFF);

            UInt16 isr = ReadGlobalRegister(AX88796CRegister.ISR);
            if ((isr & (UInt16)AX88796CRegisterBits.ISR_RXPCT) == (UInt16)AX88796CRegisterBits.ISR_RXPCT)
            {
                // clear receive packet event
                WriteGlobalRegister(AX88796CRegister.ISR, (UInt16)AX88796CRegisterBits.ISR_RXPCT);

                byte[] buffer;
                int index;
                int count;

                // ensure that RX bridge is idle
                if ((ReadGlobalRegister(AX88796CRegister.RXBCR2) & (UInt16)AX88796CRegisterBits.RXBCR2_RXB_IDLE) == (UInt16)AX88796CRegisterBits.RXBCR2_RXB_IDLE)
                {
                    // latch the RTWCR register so that we can read the # of packets from the RXBCR2 register
                    ushort rtwcr = ReadGlobalRegister(AX88796CRegister.RTWCR);
                    WriteGlobalRegister(AX88796CRegister.RTWCR, (ushort)(rtwcr | (UInt16)AX88796CRegisterBits.RTWCR_RX_LATCH));
                    // now read the # of RX packets to dequeue
                    int rxPacketCount = ReadGlobalRegister(AX88796CRegister.RXBCR2) & (UInt16)AX88796CRegisterBits.RXBCR2_RX_PACKET_COUNT_MASK;
                    while (rxPacketCount-- > 0)
                    {
                        /* WARNING: the returned packet is a reference to a single internal buffer.  The data must be retrieved and processed before calling RetrievePacket again. 
                                    this pattern was chosen to minimize garbage collection, by reusing an existing buffer without freeing/creating large extraneous objects on the heap. */
                        RetrievePacket(out buffer, out index, out count);
                        if (count > 0)
                        {
                            if (OnPacketReceived != null)
                                OnPacketReceived(this, buffer, index, count);
                        }
                        else
                        {
                            break; // if no packet is available, stop requesting packets now.
                        }
                    }
                }
                else
                {
                    // RX bridge not idle; re-initialize the RX bridge and then this ISR should automatically re-launch post-reinitialization
                    WriteGlobalRegister(AX88796CRegister.RXBCR2, (UInt16)AX88796CRegisterBits.RXBCR2_RXB_REINITIALIZE);
                }
            }
            if ((isr & (UInt16)AX88796CRegisterBits.ISR_TXPAGES_FREE) == (UInt16)AX88796CRegisterBits.ISR_TXPAGES_FREE)
            {
                // clear TX pages free event
                WriteGlobalRegister(AX88796CRegister.ISR, (UInt16)AX88796CRegisterBits.ISR_TXPAGES_FREE);
                _sendPacketTxPagesFreeEvent.Set();
            }
            if ((isr & (UInt16)AX88796CRegisterBits.ISR_LINKCHANGE) == (UInt16)AX88796CRegisterBits.ISR_LINKCHANGE)
            {
                // clear link change event
                WriteGlobalRegister(AX88796CRegister.ISR, (UInt16)AX88796CRegisterBits.ISR_LINKCHANGE);

                bool lastLinkState = _lastLinkState;
                _lastLinkState = ((Netduino.IP.ILinkLayer)this).GetLinkState();
                if (lastLinkState != _lastLinkState)
                {
                    /* NOTE: through experimentation, we found that the network chip needs about 50ms for the link to be completely ready after the ISR_LINKCHANGE event is received */
                    System.Threading.Thread.Sleep(50);
                    if (OnLinkStateChanged != null)
                        OnLinkStateChanged(this, _lastLinkState);
                }
            }

            // re-enable interrupts
            WriteGlobalRegister(AX88796CRegister.IMR, imr);
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
            // sleep for at least 200us; we are sleeping for 2ms+ instead
            System.Threading.Thread.Sleep(2); // 2000us (2ms) should be plenty of time
            // take our hardware chip out of reset
            _resetPin.Write(true);

            // attempt to connect to network chip for 1000ms
            Int64 startTicks = Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks;
            ushort psr;
            bool chipIsReady = false;
            do
            {
                psr = ReadGlobalRegister(AX88796CRegister.PSR);
                if (((psr & (UInt16)AX88796CRegisterBits.PSR_DEVICE_READY) == (UInt16)AX88796CRegisterBits.PSR_DEVICE_READY) && (psr != 0xFFFF))
                {
                    chipIsReady = true;
                    break;
                }
            } while ((Utility.GetMachineTime().Ticks - startTicks) / System.TimeSpan.TicksPerMillisecond < 1000);

            if (!chipIsReady)
                throw new Exception(); /* TODO: throw the proper exception for "could not connect to network interface */

            // enable RX packet processing
            UInt16 rpper = ReadGlobalRegister(AX88796CRegister.RPPER);
            rpper |= (UInt16)AX88796CRegisterBits.RPPER_RX_PACKET_ENABLE;
            WriteGlobalRegister(AX88796CRegister.RPPER, rpper);

            // disable RX bridge stuffing.  We only read one packet from the RX buffer at a time; buffer stuffing is designed to deal with synchronization issues during multi-packet DMA requests.
            UInt32 rxbspcr = ReadGlobalRegister(AX88796CRegister.RXBSPCR);
            rxbspcr &= ~(uint)AX88796CRegisterBits.RXBSPCR_ENABLE_STUFFING;
            WriteGlobalRegister(AX88796CRegister.RXBSPCR, (UInt16)rxbspcr);

            // set our MAC address
            WriteGlobalRegister(AX88796CRegister.MACASR0, (ushort)(((ushort)_macAddress[4]) << 8 + _macAddress[5]));
            WriteGlobalRegister(AX88796CRegister.MACASR1, (ushort)(((ushort)_macAddress[2]) << 8 + _macAddress[3]));
            WriteGlobalRegister(AX88796CRegister.MACASR2, (ushort)(((ushort)_macAddress[0]) << 8 + _macAddress[1]));

            // set checksum options
            UInt16 coercr0 = (UInt16)(AX88796CRegisterBits.COERCR0_RXIPCE | AX88796CRegisterBits.COERCR0_RXTCPE | AX88796CRegisterBits.COERCR0_RXUDPE); // enable IPv4, TCP and UDP checksum checks
            WriteGlobalRegister(AX88796CRegister.COERCR0, coercr0);
            //UInt16 coetcr0 = AX88796CRegisterBits.COETCR0_TXIP | AX88796CRegisterBits.COETCR0_TXTCP | AX88796CRegisterBits.COETCR0_TXUDP; // enable IPv4, TCP and UDP checksum insertions
            //WriteGlobalRegister(AX88796CRegister.COETCR0, coetcr0);

            /* TODO: if desired, disable RX IP header alignment function; see FER register for details */

            /* TODO: once we complete our IP stack, configure RXCR so that we don't get all packets forwarded to us--but only unicast packets and multicast/broadcast packets--including ARP/DHCP--that we want */
            // configure RX packet reception
            //UInt32 rxcr = ReadGlobalRegister(AX88796CRegister.RXCR);
            //rxcr &= ~(UInt32)AX88796CRegisterBits.RXCR_PACKET_TYPE_PROMISCUOUS; // disable promiscuous mode
            //rxcr |= (UInt32)(AX88796CRegisterBits.RXCR_PACKET_TYPE_BROADCAST | AX88796CRegisterBits.RXCR_PACKET_TYPE_ALLMULTICAST); // enable broadcast and (all?) multicast frames
            //WriteGlobalRegister(AX88796CRegister.RXCR, (UInt16)rxcr);

            /* TODO: if desired, drop CRC from received packets; see FER register for details */

            // enable byte swap within word on data port bridge
            UInt16 fer = ReadGlobalRegister(AX88796CRegister.FER);
            fer |= (UInt16)AX88796CRegisterBits.FER_BRIDGE_BYTE_SWAP;
            WriteGlobalRegister(AX88796CRegister.FER, fer);

            // enable RX and TX functions
            fer = ReadGlobalRegister(AX88796CRegister.FER);
            fer |= (UInt16)(AX88796CRegisterBits.FER_RX_BRIDGE_ENABLE | AX88796CRegisterBits.FER_TX_BRIDGE_ENABLE);
            WriteGlobalRegister(AX88796CRegister.FER, fer);

            /* INITIALIZE PHY */
            // set power saving mode [cable disconnect mode 2]
            UInt32 pscr = ReadGlobalRegister(AX88796CRegister.PSCR);
            pscr &= ~(UInt32)AX88796CRegisterBits.PSCR_POWER_SAVING_CONFIG_MASK;
            pscr |= (UInt16)AX88796CRegisterBits.PSCR_POWER_SAVING_LEVEL_2;
            WriteGlobalRegister(AX88796CRegister.PSCR, (UInt16)pscr);
            //
            // configure LEDs /* TODO: configure these based on current speed, user settings, etc.*/
            UInt16 lcr0 = (UInt16)AX88796CRegisterBits.LCR0_LED0_OPTION_DISABLED;
            WriteGlobalRegister(AX88796CRegister.LCR0, lcr0);
            UInt16 lcr1 = (UInt16)AX88796CRegisterBits.LCR1_LED2_OPTION_LINKACT;
            WriteGlobalRegister(AX88796CRegister.LCR1, lcr1);
            //
            // configure PHY control register options
            UInt16 pcr = ReadGlobalRegister(AX88796CRegister.PCR);
            // retrieve our PHY ID
            _phyID = (byte)((pcr >> 8) & 0x1F); // retrieve our PHY ID
            // enable PHY auto polling and PHY auto polling flow control
            pcr |= (UInt16)(AX88796CRegisterBits.PCR_AUTO_POLL_ENABLE | AX88796CRegisterBits.PCR_POLL_FLOW_CONTROL);
            // select our auto polling register (MR0 vs MR4)
            pcr |= (UInt16)AX88796CRegisterBits.PCR_POLL_SELECT_MR0; /* TODO: do extensive testing, make sure this is giving us the data we're looking for */
            WriteGlobalRegister(AX88796CRegister.PCR, pcr);
            //
            // set PHY speed 
            // NOTE: we may need to validate MACCR, althought it should auto-poll from PHY.
            /* TODO: should we be setting Opmode in PCR instead? and then setting this to AUTO in the PHY register? */
            switch (_phySpeed)
            {
                case PhySpeedOption.AutoNegotiate:
                    WritePhyRegister(AX88796CPhyRegister.MR4, (UInt16)AX88796CPhyRegisterBits.MR4_SPEED_DUPLEX_AUTO);
                    break;
                case PhySpeedOption.Speed10:
                    WritePhyRegister(AX88796CPhyRegister.MR4, (UInt16)AX88796CPhyRegisterBits.MR4_SPEED_DUPLEX_10ANY);
                    break;
                case PhySpeedOption.Speed100:
                    WritePhyRegister(AX88796CPhyRegister.MR4, (UInt16)AX88796CPhyRegisterBits.MR4_SPEED_DUPLEX_100ANY);
                    break;
            }
            // enable and restart auto-negotiation
            UInt16 mr0 = (UInt16)(AX88796CPhyRegisterBits.MR0_FULL_DUPLEX | AX88796CPhyRegisterBits.MR0_RESTART_AUTONEGOTIATION | AX88796CPhyRegisterBits.MR0_AUTO_NEGOTIATION_ENABLE | AX88796CPhyRegisterBits.MR0_SPEED_SELECTION_100);
            WritePhyRegister(AX88796CPhyRegister.MR0, mr0);

            // configure interrupts for INT pin (trigger on packet RX and link change)
            UInt32 imr = ReadGlobalRegister(AX88796CRegister.IMR);
            imr &= ((~(UInt32)(AX88796CRegisterBits.IMR_RXPCT_MASK | AX88796CRegisterBits.IMR_LINKCHANGE_MASK)) & 0xFFFF);
            WriteGlobalRegister(AX88796CRegister.IMR, (UInt16)imr);
            // enable our interrupt pin
            _interruptPin.EnableInterrupt();

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

        void EnterPowerDownMode()
        {
            // put the chip in sleep mode
            ushort wfcr = ReadGlobalRegister(AX88796CRegister.WFCR);
            wfcr |= (ushort)AX88796CRegisterBits.WFCR_ENTER_SLEEPMODE;
            WriteGlobalRegister(AX88796CRegister.WFCR, wfcr);
        }

        // ReadGlobalRegister retrieves the value of one of our network chip's internal registers
        UInt16 ReadGlobalRegister(AX88796CRegister registerAddress)
        {
            lock (_spiLock)
            {
                // WriteBuffer byte 0 [Opcode]: ReadGlobalRegister
                // WriteBuffer byte 1 [Address]: register address
                _readGlobalRegister_WriteBuffer[0] = (byte)AX88796COpcode.ReadGlobalRegister;
                _readGlobalRegister_WriteBuffer[1] = (byte)registerAddress;
                // WriteBuffer bytes 2-3: dummy data

                // write our ReadGlobalRegister command and retrieve the register data
                _chipSelectPin.Write(_chipSelectActiveLevel);
                _spi.WriteRead(_readGlobalRegister_WriteBuffer, _readGlobalRegister_ReadBuffer, 4);
                _chipSelectPin.Write(!_chipSelectActiveLevel);

                // return the register data as a UInt16
                return (UInt16)(_readGlobalRegister_ReadBuffer[0] + ((UInt16)_readGlobalRegister_ReadBuffer[1] << 8));
            }
        }

        // WriteGlobalRegister sets the value of one of our network chip's internal registers
        void WriteGlobalRegister(AX88796CRegister registerAddress, UInt16 value)
        {
            lock (_spiLock)
            {
                // WriteBuffer byte 0 [Opcode]: WriteGlobalRegister
                _writeGlobalRegister_WriteBuffer[0] = (byte)AX88796COpcode.WriteGlobalRegister;
                // WriteBuffer byte 1 [Address]: register address
                _writeGlobalRegister_WriteBuffer[1] = (byte)registerAddress;
                // WriteBuffer bytes 2-3: data
                _writeGlobalRegister_WriteBuffer[2] = (byte)(value & 0xFF);
                _writeGlobalRegister_WriteBuffer[3] = (byte)((value >> 8) & 0xFF);

                _chipSelectPin.Write(_chipSelectActiveLevel);
                _spi.Write(_writeGlobalRegister_WriteBuffer);
                _chipSelectPin.Write(!_chipSelectActiveLevel);
            }
        }

        void WritePhyRegister(AX88796CPhyRegister registerAddress, UInt16 value)
        {
            // store our PHY register value
            WriteGlobalRegister(AX88796CRegister.MDIODR, value);

            // start writing our PHY register value
            UInt16 mdiocr = (UInt16)(ReadGlobalRegister(AX88796CRegister.MDIOCR) & 0xE0E0); // mask out the bits we will be setting
            mdiocr |= (UInt16)AX88796CRegisterBits.MDIOCR_MDIO_WRITE;
            mdiocr |= (UInt16)((UInt16)registerAddress & 0x1F);
            mdiocr |= (UInt16)(((UInt16)_phyID) << 8);
            WriteGlobalRegister(AX88796CRegister.MDIOCR, (UInt16)mdiocr);

            // wait for the PHY register value to be written successfully
            Int64 startTicks = Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks;
            bool mdioOperationSuccess = false;
            do
            {
                mdiocr = ReadGlobalRegister(AX88796CRegister.MDIOCR);
                if ((mdiocr & (UInt16)AX88796CRegisterBits.MDIOCR_MDIO_READWRITE_COMPLETE) == (UInt16)AX88796CRegisterBits.MDIOCR_MDIO_READWRITE_COMPLETE)
                {
                    mdioOperationSuccess = true;
                    break;
                }
            } while ((Utility.GetMachineTime().Ticks - startTicks) / System.TimeSpan.TicksPerMillisecond < 250); // wait up to 250ms

            if (!mdioOperationSuccess)
                throw new Exception(); /* TODO: this should be a "could not configure network interface" exception */
        }

        UInt16 ReadPhyRegister(AX88796CPhyRegister registerAddress)
        {
            // start reading our PHY register value
            UInt16 mdiocr = (UInt16)(ReadGlobalRegister(AX88796CRegister.MDIOCR) & 0xE0E0); // mask out the bits we will be setting
            mdiocr |= (UInt16)AX88796CRegisterBits.MDIOCR_MDIO_READ;
            mdiocr |= (UInt16)((UInt16)registerAddress & 0x1F);
            mdiocr |= (UInt16)(((UInt16)_phyID) << 8);
            WriteGlobalRegister(AX88796CRegister.MDIOCR, (UInt16)mdiocr);

            // wait for the PHY register value to be read successfully
            Int64 startTicks = Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks;
            bool mdioOperationSuccess = false;
            do
            {
                mdiocr = ReadGlobalRegister(AX88796CRegister.MDIOCR);
                if ((mdiocr & (UInt16)AX88796CRegisterBits.MDIOCR_MDIO_READWRITE_COMPLETE) == (UInt16)AX88796CRegisterBits.MDIOCR_MDIO_READWRITE_COMPLETE)
                {
                    mdioOperationSuccess = true;
                    break;
                }
            } while ((Utility.GetMachineTime().Ticks - startTicks) / System.TimeSpan.TicksPerMillisecond < 250); // wait up to 250ms

            if (!mdioOperationSuccess)
                throw new Exception(); /* TODO: this should be a "could not configure network interface" exception */

            return ReadGlobalRegister(AX88796CRegister.MDIODR);
        }

        // returns the latest packet from the RX buffer
        /* WARNING: the returned packet is a reference to a single internal buffer.  The data must be retrieved and processed before calling RetrievePacket again. 
                    this pattern was chosen to minimize garbage collection, by reusing an existing buffer without freeing/creating large extraneous objects on the heap. */
        void RetrievePacket(out byte[] buffer, out int index, out int count)
        {
            /* lock SPI access to our network chip in case there is a potential conflict between RXQ and RXQ accesses; technically this may not be necessary */
            lock (_spiLock)
            {
                // latch our RX bridge's receive buffer to get accurate packet size
                WriteGlobalRegister(AX88796CRegister.RTWCR, (UInt16)AX88796CRegisterBits.RTWCR_RX_LATCH);
                int packetLength = ReadGlobalRegister(AX88796CRegister.RCPHR) & (int)AX88796CRegisterBits.RCPHR_PACKET_LENGTH_MASK;
                if (packetLength == 0)
                {
                    //Debug.Print("zero length packet! packetLength: " + packetLength);
                    buffer = null;
                    index = 0;
                    count = 0;
                    return;
                }

                int packetLengthIncludingRxHeader = packetLength + 6;
                // our packet will be 32-bit-word-aligned, so add padding if necessary.  [we add an extra 6 bytes here for the rx header]
                int extraPadding = (4 - (packetLengthIncludingRxHeader % 4)) % 4;
                int packetLengthIncludingRxHeaderAndExtraPadding = packetLengthIncludingRxHeader + extraPadding;
                int wordLength = packetLengthIncludingRxHeaderAndExtraPadding / 2;

                // start the read 
                UInt16 rxbcr1 = (UInt16)((UInt16)AX88796CRegisterBits.RXBCR1_RXB_START | (wordLength & 0x3FFF));
                WriteGlobalRegister(AX88796CRegister.RXBCR1, rxbcr1);

                // get packet
                ReadRxq(out buffer, packetLengthIncludingRxHeaderAndExtraPadding);
                // calculate index and count values for our caller
                index = 6; // packet starts 6 bytes into buffer (after RX headers 1 and 2)
                count = packetLength; // return the ACTUAL packet length (not counting headers, etc.)

                // if we are not sure that the received data was valid, discard it.
                if ((ReadGlobalRegister(AX88796CRegister.RXBCR2) & (UInt16)AX88796CRegisterBits.RXBCR2_RXB_IDLE) != (UInt16)AX88796CRegisterBits.RXBCR2_RXB_IDLE)
                {
                    // re-initialize the RX bridge and then this ISR should automatically re-launch post-reinitialization
                    WriteGlobalRegister(AX88796CRegister.RXBCR2, (UInt16)AX88796CRegisterBits.RXBCR2_RXB_REINITIALIZE);
                    // discard our received data
                    index = 0;
                    count = 0;
                }
            }
        }

        void ILinkLayer.SendFrame(int numBuffers, byte[][] buffer, int[] index, int[] count, Int64 timeoutInMachineTicks)
        {
            int totalBufferLength = 0;
            for (int i = 0; i < numBuffers; i++)
            {
                totalBufferLength += count[i];
            }

            int packetLengthInverse = ~totalBufferLength & 0x07FF;
            byte sequenceNumberInverse = (byte)(~_sendPacketSequenceNumber & 0x1F);

            // populate start of packet header
            // SOP Header
            _sendPacketSopHeader[0] = (byte)((totalBufferLength >> 8) & 0x07);
            _sendPacketSopHeader[1] = (byte)(totalBufferLength & 0xFF);
            _sendPacketSopHeader[2] = (byte)(_sendPacketSequenceNumber << 3);
            _sendPacketSopHeader[2] |= (byte)((packetLengthInverse >> 8) & 0x07);
            _sendPacketSopHeader[3] = (byte)(packetLengthInverse & 0xFF);
            // Segment Header
            _sendPacketSopHeader[4] = (byte)((totalBufferLength >> 8) & 0x07);
            _sendPacketSopHeader[4] |= (1 << 7) | (1 << 6) | ((0x00 & 0x07) << 3); // first segment, last segment, segment 0
            _sendPacketSopHeader[5] = (byte)(totalBufferLength & 0xFF);
            _sendPacketSopHeader[6] = (byte)((packetLengthInverse >> 8) & 0x07);
            _sendPacketSopHeader[7] = (byte)(packetLengthInverse & 0xFF);

            // populate end of packet header
            _sendPacketEopHeader[0] = (byte)(_sendPacketSequenceNumber << 3);
            _sendPacketEopHeader[0] |= (byte)((totalBufferLength >> 8) & 0x07);
            _sendPacketEopHeader[1] = (byte)(totalBufferLength & 0xFF);
            _sendPacketEopHeader[2] = (byte)(sequenceNumberInverse << 3);
            _sendPacketEopHeader[2] |= (byte)((packetLengthInverse >> 8) & 0x07);
            _sendPacketEopHeader[3] = (byte)(packetLengthInverse & 0xFF);

            // calculate required page count
            int totalLength = totalBufferLength + _sendPacketSopHeader.Length + _sendPacketEopHeader.Length;
            int requiredPageCount = 1;
            if ((totalLength > 120) && (((totalLength - 120) % 128) == 0))
            {
                requiredPageCount = 1 + ((totalLength - 120) / 128);
            }
            else
            {
                requiredPageCount = 2 + ((totalLength - 120) / 128);
            }

            // get count of available TX pages
            UInt16 freePageCount = (UInt16)(ReadGlobalRegister(AX88796CRegister.TFBFCR) & (UInt16)AX88796CRegisterBits.TFBFCR_FREE_BUFFER_COUNT_MASK);

            // if we don't have enough free pages, wait for pages to become available
            if (freePageCount < requiredPageCount)
            {
                // activate our "TX pages" interrupt, to let us know when enough buffers are free.
                UInt16 tfbfcr = ReadGlobalRegister(AX88796CRegister.TFBFCR);
                tfbfcr |= (UInt16)(requiredPageCount << 7);
                tfbfcr |= (UInt16)AX88796CRegisterBits.TFBFCR_ACTIVATE_TXPAGES_INTERRUPT;
                WriteGlobalRegister(AX88796CRegister.TFBFCR, tfbfcr);
                // wait for free pages to become available (but do not wait longer than our expiration)
                Int32 millisecondsUntilTimeout = (Int32)((timeoutInMachineTicks != Int64.MaxValue) ? (timeoutInMachineTicks - Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks) / System.TimeSpan.TicksPerMillisecond : Int32.MaxValue);
                if (millisecondsUntilTimeout < 0) millisecondsUntilTimeout = 0;
                if (!_sendPacketTxPagesFreeEvent.WaitOne(millisecondsUntilTimeout, false)) return; /* if we timeout, drop the frame */
            }

            UInt16 tsnr = 0;
            /* lock SPI access to our network chip in case there is a potential conflict between RXQ and RXQ accesses; technically this may not be necessary */
            lock (_spiLock)
            {
                // indicate to the TX bridge that we are sending one packet
                tsnr = ReadGlobalRegister(AX88796CRegister.TSNR);
                tsnr |= (1 << 8); // we are transmitting one packet
                tsnr |= (UInt16)AX88796CRegisterBits.TSNR_TXB_START; // we are starting to send our packet
                WriteGlobalRegister(AX88796CRegister.TSNR, tsnr);

                // calculate how much extra padding we need
                int extraPadding = (4 - (totalBufferLength % 4)) % 4;

                // send our packet to the TXQ buffer
                WriteTxq(_sendPacketSopHeader, 0, _sendPacketSopHeader.Length);
                // write our frame(s) to the TX buffer
                for (int i = 0; i < numBuffers; i++)
                {
                    WriteTxq(buffer[i], index[i], count[i], (i == numBuffers - 1) ? extraPadding : 0);
                }
                WriteTxq(_sendPacketEopHeader, 0, _sendPacketEopHeader.Length);
            }

            // increment our sequence number (rolling over at 0x20)
            _sendPacketSequenceNumber = (byte)((_sendPacketSequenceNumber + 1) % 0x20);

            // check TSNR to make sure our TX bridge is idle
            tsnr = ReadGlobalRegister(AX88796CRegister.TSNR);
            if ((tsnr & (UInt16)AX88796CRegisterBits.TSNR_TXB_IDLE) == 0)
            {
                // error; do not attempt to retransmit packet but do re-sync the sequence number
                // re-sync our sequence number
                tsnr = ReadGlobalRegister(AX88796CRegister.TSNR);
                _sendPacketSequenceNumber = (byte)(tsnr & 0x1F);
                // re-initialize tx bridge
                WriteGlobalRegister(AX88796CRegister.TSNR, (UInt16)AX88796CRegisterBits.TSNR_TXB_REINITIALIZE);
            }
            // and also check ISR to make sure we didn't have a TX error
            UInt16 isr = ReadGlobalRegister(AX88796CRegister.ISR);
            if ((isr & (UInt16)AX88796CRegisterBits.ISR_TXERR) == (UInt16)AX88796CRegisterBits.ISR_TXERR)
            {
                // error; do not attempt to retransmit packet but do re-sync the sequence number
                // clear ISR TX error
                WriteGlobalRegister(AX88796CRegister.ISR, (UInt16)AX88796CRegisterBits.ISR_TXERR);
                // re-sync our sequence number
                tsnr = ReadGlobalRegister(AX88796CRegister.TSNR);
                _sendPacketSequenceNumber = (byte)(tsnr & 0x1F);
                // re-initialize tx bridge
                WriteGlobalRegister(AX88796CRegister.TSNR, (UInt16)AX88796CRegisterBits.TSNR_TXB_REINITIALIZE);
            }

            /* TODO: if we are not including "queued by not transmitted" frames in our timeout, we should do so--cancelling outstanding frame(s) when a timeout occurs. */
        }

        void ReadRxq(out byte[] buffer, int length)
        {
            _readTxqBuffer_WriteBuffer[0] = (byte)AX88796COpcode.ReadRxq;

            lock (_spiLock)
            {
                _chipSelectPin.Write(_chipSelectActiveLevel);
                _spi.Write(_readTxqBuffer_WriteBuffer);
                /* TODO: extend _spi with a .Read() function that just sends out dummy bytes...or at least makes it so we don't have to pass in an array of data to send */
                _spi.WriteRead(_incomingFrame, 0, length, _incomingFrame, 0, length, 0);
                _chipSelectPin.Write(!_chipSelectActiveLevel);
                buffer = _incomingFrame;
            }
        }

        void WriteTxq(byte[] buffer, int index, int count, int extraPaddingBytes = 0)
        {
            if (extraPaddingBytes > _writeTxqBuffer_ExtraPadding.Length)
                throw new ArgumentOutOfRangeException();

            _writeTxqBuffer_WriteBuffer[0] = (byte)AX88796COpcode.WriteTxq;

            lock (_spiLock)
            {
                _chipSelectPin.Write(_chipSelectActiveLevel);
                _spi.Write(_writeTxqBuffer_WriteBuffer);
                _spi.WriteRead(buffer, index, count, null, 0, 0, 0);
                _spi.WriteRead(_writeTxqBuffer_ExtraPadding, 0, extraPaddingBytes, null, 0, 0, 0);
                _chipSelectPin.Write(!_chipSelectActiveLevel);
            }
        }

        bool ILinkLayer.GetLinkState()
        {
            UInt16 pscr = ReadGlobalRegister(AX88796CRegister.PSCR);
            return ((pscr & (UInt16)AX88796CRegisterBits.PSCR_PHY_IN_LINK_STATE) == (UInt16)AX88796CRegisterBits.PSCR_PHY_IN_LINK_STATE);
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
