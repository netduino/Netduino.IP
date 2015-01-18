// Netduino.IP Stack
// Copyright (c) 2015 Secret Labs LLC. All rights reserved.
// Licensed under the Apache 2.0 License

using System;

namespace Netduino.IP
{
    public delegate void LinkStateChangedEventHandler(object sender, bool state);
    public delegate void PacketReceivedEventHandler(object sender, byte[] buffer, int index, int count);

    public interface ILinkLayer
    {
        bool GetLinkState();
        byte[] GetMacAddress();
        void SendFrame(int numBuffers, byte[][] buffer, int[] index, int[] count, Int64 timeoutInMachineTicks);
        void SetMacAddress(byte[] macAddress);
        void Start();
        void Stop();

        event LinkStateChangedEventHandler LinkStateChanged;
        event PacketReceivedEventHandler PacketReceived;
    }
}
