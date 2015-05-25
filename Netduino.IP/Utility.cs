using System;

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
            return CalculateInternetChecksum(_singleBuffer, _singleOffset, _singleCount);
        }

        /* NOTE: all buffers except for the last one must have an even number of characters */
        public static UInt16 CalculateInternetChecksum(byte[][] buffer, int[] offset, int[] count)
        {
            int totalLength = 0;
            for (int iBuffer = 0; iBuffer < buffer.Length; iBuffer++)
            {
                totalLength += count[iBuffer];
            }

            UInt32 checksum = 0;
            int extraPadding = 0;

            for (int iBuffer = 0; iBuffer < buffer.Length; iBuffer++)
            {
                if (iBuffer == buffer.Length - 1)
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

    }
}
