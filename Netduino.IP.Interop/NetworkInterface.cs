using Microsoft.SPOT;
using System;
using System.Runtime.CompilerServices;

namespace Netduino.IP.Interop
{
    public class NetworkInterface
    {
        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        public extern static int GetNetworkInterfaceCount();

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        public extern static object GetNetworkInterface(uint interfaceIndex);

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        public extern void InitializeNetworkInterfaceSettings();

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        public extern void UpdateConfiguration(int updateType);

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        public extern static uint IPAddressFromString(string ipAddress);
    }
}
