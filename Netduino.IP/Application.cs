using Microsoft.SPOT.Hardware;
using System;
using System.Reflection;

namespace Netduino.IP
{
    internal class Application
    {
        static System.Threading.Thread _applicationStartThread = null;

        static Application()
        {
            /* NOTE: this code will run automatically when the application begins */
            _applicationStartThread = new System.Threading.Thread(SocketsInterface.Initialize);
            _applicationStartThread.Start();
        }
    }
}
