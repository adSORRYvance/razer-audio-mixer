// Decoder for Report 0x09 - the 8-byte interrupt-IN push from EP 0x84
// The device sends this whenever fader positions or button state changes
// Wire layout (0-indexed):
//   [0]    0x09 (report ID)
//   [1]    mute bitmask  bit0=ch1, bit1=ch2, bit2=ch3, bit3=ch4, bit5=mic
//   [2]    unknown (changes with fader state; do not interpret)
//   [3..6] fader 1..4 positions, u8 range 0x00..0x64 (0–100)
//   [7]    0x05 (constant)
// Mute handling uses edge detection: the bitmask reflects button-held state, so each 0-to-1 transition (rising edge) flips the logical mute state for that channel. Callers hold logicalMute across calls
// Reference: docs/protocol.md "Interrupt-IN path"
namespace RazerMixerBridge.Protocol;

public sealed record InputReport(byte MuteMask, int[] Faders)
{
    public static InputReport? TryParse(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 8 || frame[0] != RazerProtocol.ReportInputState)
            return null;
        return new InputReport(
            MuteMask: frame[1],
            Faders: [frame[3], frame[4], frame[5], frame[6]]
        );
    }

    /// <summary>
    /// Diff this report against <paramref name="prev"/>, updating
    /// <paramref name="logicalMute"/> in-place on rising mute-button edges,
    /// and returning the resulting events.
    /// </summary>
    public IReadOnlyList<IInputEvent> Diff(InputReport prev, bool[] logicalMute)
    {
        var events = new List<IInputEvent>();

        // Rising edge detection on the mute bitmask.
        byte rising = (byte)(~prev.MuteMask & MuteMask);
        foreach (var (bit, channel) in MuteBitMap)
        {
            if ((rising & (1 << bit)) != 0)
            {
                logicalMute[channel] = !logicalMute[channel];
                events.Add(new MuteToggled(channel, logicalMute[channel]));
            }
        }

        // Fader moves.
        for (int i = 0; i < Faders.Length; i++)
        {
            if (Faders[i] != prev.Faders[i])
                events.Add(new FaderMoved(i + 1, Faders[i] / 100f));
        }

        return events;
    }

    // bit index → channel number (1-4 = faders, 5 = mic)
    private static readonly (int Bit, int Channel)[] MuteBitMap =
        [(0, 1), (1, 2), (2, 3), (3, 4), (5, 5)];
}

public interface IInputEvent { }

public sealed record FaderMoved(int Channel, float Value01) : IInputEvent;
public sealed record MuteToggled(int Channel, bool Muted) : IInputEvent;
