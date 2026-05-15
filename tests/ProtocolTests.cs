// Protocol encoder hex fixtures, locked to Synapse pcap captures in docs/pcaps/.
using RazerMixerBridge.Protocol;

namespace RazerMixerBridge.Tests;

public class ProtocolTests
{
    static byte[] H(string hex) => Convert.FromHexString(hex);
    static string Pad64(string head) => head.PadRight(128, '0');

    // -- EncodeMuteFeedback -------------------------------------------------
    // Source: mute-ch1_synapse.pcapng, mute-mic_synapse.pcapng

    [Theory]
    [InlineData(1, true, "07001f000000030810000101")]
    [InlineData(1, false, "07001f000000030810000100")]
    [InlineData(5, true, "07001f000000030810000501")]
    [InlineData(5, false, "07001f000000030810000500")]
    public void MuteFeedback_MatchesCapture(int channel, bool muted, string headHex) =>
        Assert.Equal(H(Pad64(headHex)), RazerProtocol.EncodeMuteFeedback(channel, muted));

    [Fact]
    public void MuteFeedback_InvalidChannel_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RazerProtocol.EncodeMuteFeedback(0, false));

    // -- InputReport.TryParse -------------------------------------------------

    [Fact]
    public void InputReport_ParsesValidFrame()
    {
        byte[] frame = [0x09, 0x05, 0x00, 0x00, 0x32, 0x64, 0x0A, 0x05];
        var report = InputReport.TryParse(frame);
        Assert.NotNull(report);
        Assert.Equal(0x05, report.MuteMask);
        Assert.Equal([0, 50, 100, 10], report.Faders);
    }

    [Fact]
    public void InputReport_RejectsWrongReportId()
    {
        byte[] frame = [0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        Assert.Null(InputReport.TryParse(frame));
    }

    [Fact]
    public void InputReport_RejectsTooShortFrame()
    {
        byte[] frame = [0x09, 0x00, 0x00];
        Assert.Null(InputReport.TryParse(frame));
    }

    // -- InputReport.Diff -------------------------------------------------

    [Fact]
    public void InputReport_Diff_EmitsFaderMoved()
    {
        var prev = new InputReport(0x00, [0, 0, 0, 0]);
        var curr = new InputReport(0x00, [50, 0, 0, 0]);
        bool[] mute = new bool[6];

        var events = curr.Diff(prev, mute);

        var ev = Assert.Single(events);
        var fm = Assert.IsType<FaderMoved>(ev);
        Assert.Equal(1, fm.Channel);
        Assert.Equal(0.5f, fm.Value01, precision: 3);
    }

    [Fact]
    public void InputReport_Diff_EmitsMuteToggle_OnRisingEdge()
    {
        var prev = new InputReport(0x00, [0, 0, 0, 0]);
        var curr = new InputReport(0x01, [0, 0, 0, 0]);
        bool[] mute = new bool[6];

        var events = curr.Diff(prev, mute);

        var ev = Assert.Single(events);
        var mt = Assert.IsType<MuteToggled>(ev);
        Assert.Equal(1, mt.Channel);
        Assert.True(mt.Muted);
        Assert.True(mute[1]);
    }

    [Fact]
    public void InputReport_Diff_NoToggle_WhileButtonHeld()
    {
        var held = new InputReport(0x01, [0, 0, 0, 0]);
        bool[] mute = new bool[6] { false, true, false, false, false, false };

        var events = held.Diff(held, mute);

        Assert.Empty(events);
    }

    [Fact]
    public void InputReport_Diff_SecondPress_TogglesBack()
    {
        var unpressed = new InputReport(0x00, [0, 0, 0, 0]);
        var pressed = new InputReport(0x01, [0, 0, 0, 0]);
        bool[] mute = new bool[6] { false, true, false, false, false, false };

        var events = pressed.Diff(unpressed, mute);
        var mt = Assert.IsType<MuteToggled>(Assert.Single(events));
        Assert.False(mt.Muted);
        Assert.False(mute[1]);
    }
}
