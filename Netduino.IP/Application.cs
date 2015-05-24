using Microsoft.SPOT.Hardware;
using System;
using System.Reflection;

namespace Netduino.IP
{
    internal class Application
    {
#if NETDUINOIP_AX88796C
        const string LINK_LAYER_BASE_TYPENAME = "Netduino.IP.LinkLayers.AX88796C, Netduino.IP.LinkLayers.AX88796C";
#elif NETDUINOIP_ENC28J60
        const string LINK_LAYER_BASE_TYPENAME = "Netduino.IP.LinkLayers.ENC28J60, Netduino.IP.LinkLayers.ENC28J60";
#elif NETDUINOIP_CC3100
        const string LINK_LAYER_BASE_TYPENAME = "Netduino.IP.LinkLayers.CC3100, Netduino.IP.LinkLayers.CC3100";
#endif
        static System.Threading.Thread _applicationStartThread = null;

        static internal EthernetInterface _ethernetInterface = null;
        static internal ArpResolver _arp = null;

        static Application()
        {
            /* NOTE: this code will run automatically when the application begins */
            _applicationStartThread = new System.Threading.Thread(ApplicationStartThread);
            _applicationStartThread.Start();
        }

        static void ApplicationStartThread()
        {
#if CC3100
            Type socketNativeType = Type.GetType("Netduino.IP.LinkLayers.CC3100SocketNative, Netduino.IP.LinkLayers.CC3100");
            System.Reflection.MethodInfo initializeMethod = socketNativeType.GetMethod("Initialize", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            initializeMethod.Invoke(null, new object[] { });
#else
            // create the appropriate link layer object
            Type linkLayerType = Type.GetType(LINK_LAYER_BASE_TYPENAME);
            System.Reflection.ConstructorInfo linkLayerConstructor = linkLayerType.GetConstructor(new Type[] {typeof(SPI.SPI_module), typeof(Cpu.Pin), typeof(Cpu.Pin), typeof(Cpu.Pin), typeof(Cpu.Pin)});
#if NETDUINOIP_AX88796C
            ILinkLayer linkLayer = (ILinkLayer)linkLayerConstructor.Invoke(new object[] { SPI.SPI_module.SPI2  /* spiBusID */, (Cpu.Pin)0x28 /* csPinID */, (Cpu.Pin)0x04 /* intPinID */, (Cpu.Pin)0x12 /* resetPinID */, (Cpu.Pin)0x44 /* wakeupPinID */ });
#elif NETDUINOIP_ENC28J60
            ILinkLayer linkLayer = (ILinkLayer)linkLayerConstructor.Invoke(new object[] { SPI.SPI_module.SPI2  /* spiBusID */, (Cpu.Pin)0x28 /* csPinID */, (Cpu.Pin)0x04 /* intPinID */, (Cpu.Pin)0x32 /* resetPinID */, Cpu.Pin.GPIO_NONE /* wakeupPinID */ });
#elif NETDUINOIP_CC3100
#endif
            // retrieve MAC address from the config sector
            object networkInterface = Netduino.IP.Interop.NetworkInterface.GetNetworkInterface(0);
            byte[] physicalAddress = (byte[])networkInterface.GetType().GetMethod("get_PhysicalAddress").Invoke(networkInterface, new object[] { });
            linkLayer.SetMacAddress(physicalAddress);

            // create Ethernet object
            _ethernetInterface = new Netduino.IP.EthernetInterface(linkLayer);
            // create ARP object
            _arp = new ArpResolver(_ethernetInterface);

            // start up our link layer
            linkLayer.Start();
#endif
        }
    }
}
