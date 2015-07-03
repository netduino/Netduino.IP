using System;
using System.Reflection;

namespace Netduino.IP
{
    static class Utility
    {
        static byte[][] _singleBuffer = new byte[1][];
        static int[] _singleOffset = new int[1];
        static int[] _singleCount = new int[1];

        public static UInt16 CalculateInternetChecksum(byte[] buffer, int offset, int count)
        {
            _singleBuffer[0] = buffer;
            _singleOffset[0] = offset;
            _singleCount[0] = count;
            return CalculateInternetChecksum(_singleBuffer, _singleOffset, _singleCount, _singleBuffer.Length);
        }

        public static UInt16 CalculateInternetChecksum(byte[][] buffer, int[] offset, int[] count)
        {
            return CalculateInternetChecksum(buffer, offset, count, buffer.Length);
        }

        /* NOTE: all buffers except for the last one must have an even number of characters */
        public static UInt16 CalculateInternetChecksum(byte[][] buffer, int[] offset, int[] count, int numBuffers)
        {
            int totalLength = 0;
            for (int iBuffer = 0; iBuffer < numBuffers; iBuffer++)
            {
                totalLength += count[iBuffer];
            }

            UInt32 checksum = 0;
            int extraPadding = 0;

            for (int iBuffer = 0; iBuffer < numBuffers; iBuffer++)
            {
                if (iBuffer == numBuffers - 1)
                    extraPadding += (totalLength % 2 == 0) ? 0 : 1;

                for (int i = offset[iBuffer]; i < offset[iBuffer] + count[iBuffer] + extraPadding; i += 2)
                {
                    if ((extraPadding > 0) && (i == offset[iBuffer] + count[iBuffer] + extraPadding - 2))
                    {
                        checksum += (UInt16)((buffer[iBuffer][i] << 8) + 0);
                    }
                    else
                    {
                        checksum += (UInt16)((buffer[iBuffer][i] << 8) + buffer[iBuffer][i + 1]);
                    }
                }
            }

            // add any carry-over digits to the checksum
            checksum = (checksum & 0xFFFF) + (checksum >> 16);

            // invert all bits
            checksum ^= 0xFFFF;

            return (UInt16)checksum;
        }

        internal static Exception NewSocketException(SocketError errorCode)
        {
            Type socketErrorType = Type.GetType("System.Net.Sockets.SocketError, System");
            ConstructorInfo constructorInfo = Type.GetType("System.Net.Sockets.SocketException, System").GetConstructor(new Type[] { socketErrorType });
            return (Exception)(constructorInfo.Invoke(new object[] { errorCode }));
        }
    }
}
