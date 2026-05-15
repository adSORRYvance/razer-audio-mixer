// HID protocol encoders for the Razer Audio Mixer (USB 1532:053e)
// Wire reference: docs/protocol.md
namespace RazerMixerBridge.Protocol;

public static class RazerProtocol
{
    public const byte ReportChannelCfg = 0x07;   // 64-byte lighting / mute feedback
    public const byte ReportInputState = 0x09;   // 8-byte input push (EP 0x84)

    /// <summary>
    /// Report 0x07 sub-command D: tell the device which channel's mute LED to update. channel 1-4 = fader channels, 5 = mic
    /// Captured from mute-ch{1..4}_synapse.pcapng and mute-mic_synapse.pcapng
    /// </summary>
    public static byte[] EncodeMuteFeedback(int channel, bool muted)
    {
        if (channel is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(channel), "must be 1-5");

        Span<byte> payload = stackalloc byte[64];
        payload.Clear();
        payload[0] = ReportChannelCfg;
        payload[2] = 0x1F;
        payload[6] = 0x03;
        payload[7] = 0x08;
        payload[8] = 0x10;
        payload[10] = (byte)channel;
        payload[11] = muted ? (byte)0x01 : (byte)0x00;
        return payload.ToArray();
    }
}
