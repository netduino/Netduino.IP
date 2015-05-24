using Microsoft.SPOT;
using System;
using System.Runtime.CompilerServices;

namespace Netduino.IP.Interop
{
    class Wireless
    {
        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        private extern static void UpdateConfiguration(object wirelessConfigurations, bool useEncryption);

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        private extern static void SaveAllConfigurations();
    }
}
