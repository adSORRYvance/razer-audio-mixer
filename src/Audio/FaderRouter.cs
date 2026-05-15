// Routes fader and mute events to PulseAudio
// Fader moves keep only the latest value, so fast moves never queue up pactl calls
// Mutes go through right away
using RazerMixerBridge.Hid;
using RazerMixerBridge.Protocol;

namespace RazerMixerBridge.Audio;

public sealed class FaderRouter
{
    private const float DeadzoneFraction = 0.005f;

    private readonly PulseAudioClient _pulse;
    private readonly ILogger<FaderRouter> _log;

    public string[] FaderSinkNames { get; set; } = [];
    public RazerHidDevice? Device { get; set; }

    private readonly float?[] _pending = new float?[4];
    private readonly float[] _lastSent = new float[4];
    private readonly SemaphoreSlim[] _slots =
        [new(1, 1), new(1, 1), new(1, 1), new(1, 1)];
    private readonly object _gate = new();

    public FaderRouter(PulseAudioClient pulse, ILogger<FaderRouter> log)
    {
        _pulse = pulse;
        _log = log;
        Array.Fill(_lastSent, -1f);
    }

    public Task HandleFaderMovedAsync(FaderMoved ev, CancellationToken ct)
    {
        int idx = ev.Channel - 1;
        if (idx < 0 || idx >= 4) return Task.CompletedTask;

        lock (_gate) { _pending[idx] = ev.Value01; }
        _ = FlushAsync(idx, ct);
        return Task.CompletedTask;
    }

    public async Task HandleMuteToggledAsync(MuteToggled ev, CancellationToken ct)
    {
        int idx = ev.Channel - 1;
        if (idx >= 0 && idx < FaderSinkNames.Length)
            await _pulse.SetSinkMuteAsync(FaderSinkNames[idx], ev.Muted, ct);

        Device?.WriteFeatureReport(RazerProtocol.EncodeMuteFeedback(ev.Channel, ev.Muted));

        _log.LogInformation("Ch{Ch} mute => {State}", ev.Channel, ev.Muted ? "muted" : "active");
    }

    private async Task FlushAsync(int idx, CancellationToken ct)
    {
        // One flusher per fader
        // If the slot's busy, the running one picks up our value on its next loop
        if (!await _slots[idx].WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            while (!ct.IsCancellationRequested && idx < FaderSinkNames.Length)
            {
                float value;
                lock (_gate)
                {
                    if (_pending[idx] is not { } v) break;
                    value = v;
                    _pending[idx] = null;
                }

                if (Math.Abs(value - _lastSent[idx]) < DeadzoneFraction) continue;

                await _pulse.SetSinkVolumeAsync(FaderSinkNames[idx], value, ct).ConfigureAwait(false);
                _lastSent[idx] = value;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "Fader {Ch} flush failed", idx + 1); }
        finally
        {
            _slots[idx].Release();
            // A move could land between the loop exit and the release
            // Re-check and restart if so
            bool hasMore;
            lock (_gate) { hasMore = _pending[idx] is not null; }
            if (hasMore) _ = FlushAsync(idx, ct);
        }
    }
}
