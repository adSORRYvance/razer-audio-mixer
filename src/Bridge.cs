// Owns the device and PA lifecycle
// On plug, we wire 4 fader sinks through the razer hardware sink to the real output, and promote fader 4 to PA default
// On unplug, we restore the default and unload everything
using RazerMixerBridge.Audio;
using RazerMixerBridge.Config;
using RazerMixerBridge.Hid;
using RazerMixerBridge.Protocol;

namespace RazerMixerBridge;

public sealed class Bridge : BackgroundService
{
    private static readonly string[] FaderSinkNames =
        ["razer_fader1", "razer_fader2", "razer_fader3", "razer_fader4"];
    private const string LandingSink = "razer_fader4";

    private readonly HidDeviceWatcher _watcher;
    private readonly PulseAudioClient _pulse;
    private readonly FaderRouter _router;
    private readonly BridgeConfig _config;
    private readonly ILogger<Bridge> _log;
    private readonly ILoggerFactory _loggerFactory;

    private RazerHidDevice? _device;
    private readonly List<int> _moduleIds = [];
    private string? _razerSink;
    private string? _previousDefault;
    private CancellationTokenSource _readCts = new();
    private Task _readTask = Task.CompletedTask;
    private CancellationTokenSource _subscribeCts = new();
    private Task _subscribeTask = Task.CompletedTask;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public Bridge(
        HidDeviceWatcher watcher,
        PulseAudioClient pulse,
        FaderRouter router,
        BridgeConfig config,
        ILogger<Bridge> log,
        ILoggerFactory loggerFactory)
    {
        _watcher = watcher;
        _pulse = pulse;
        _router = router;
        _config = config;
        _log = log;
        _loggerFactory = loggerFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _watcher.DeviceArrived += path => _ = OnDeviceArrivedAsync(path, stoppingToken);
        _watcher.DeviceRemoved += () => _ = OnDeviceRemovedAsync(stoppingToken);
        _watcher.Start();

        _log.LogInformation("Razer Mixer Bridge started, waiting for device");
        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
    }

    // Plug-in

    private async Task OnDeviceArrivedAsync(string hidrawPath, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _log.LogInformation("Device plug-in: {Path}", hidrawPath);

            _device = new RazerHidDevice(hidrawPath, _loggerFactory.CreateLogger<RazerHidDevice>());

            await SetupPulseAsync(ct);

            _readCts = new CancellationTokenSource();
            _readTask = Task.Run(() => ReadLoopAsync(_readCts.Token), CancellationToken.None);

            _subscribeCts = new CancellationTokenSource();
            _subscribeTask = Task.Run(() => SubscribeLoopAsync(_subscribeCts.Token), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to initialise device");
        }
        finally
        {
            _lock.Release();
        }
    }

    // Unplug

    private async Task OnDeviceRemovedAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _log.LogInformation("Device removed, tearing down");
            await _readCts.CancelAsync();
            await _subscribeCts.CancelAsync();
            try { await _readTask; } catch { }
            try { await _subscribeTask; } catch { }

            await TeardownPulseAsync(ct);

            _device?.Dispose();
            _device = null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error during device removal");
        }
        finally
        {
            _lock.Release();
        }
    }

    // PA routing

    private async Task SetupPulseAsync(CancellationToken ct)
    {
        _razerSink = await _pulse.FindSinkByDescriptionAsync("razer audio mixer", ct);
        await _pulse.RemoveStaleModulesAsync(FaderSinkNames, _razerSink, ct);

        // If the captured default is one of our sinks, a prior run crashed before restoring it
        // Skip the restore so we don't point at a sink that's about to vanish
        _previousDefault = await _pulse.GetDefaultSinkAsync(ct);
        if (_previousDefault is not null &&
            (FaderSinkNames.Contains(_previousDefault) || _previousDefault == _razerSink))
        {
            _log.LogWarning("Captured default {Sink} is one of ours, skipping unplug-restore", _previousDefault);
            _previousDefault = null;
        }

        string? postMixerTarget = _config.Audio.OutputSink ?? _previousDefault;

        _moduleIds.Clear();
        string template = _config.Audio.SinkDescriptionTemplate;
        for (int i = 0; i < FaderSinkNames.Length; i++)
        {
            string name = FaderSinkNames[i];
            string desc = template.Replace("{n}", (i + 1).ToString());
            int? id = await _pulse.LoadModuleAsync("module-null-sink",
                $"sink_name={name} sink_properties=device.description={desc}", ct);
            if (id is not null) _moduleIds.Add(id.Value);
            _log.LogInformation("Created sink {Name}", name);
        }

        if (_razerSink is null)
        {
            _log.LogWarning("Razer sink not visible to PA, running degraded (faders only, no passthrough)");
            _router.FaderSinkNames = FaderSinkNames;
            _router.Device = _device;
            return;
        }

        foreach (string name in FaderSinkNames)
        {
            int? id = await _pulse.LoadModuleAsync("module-loopback",
                $"source={name}.monitor sink={_razerSink} latency_msec=20", ct);
            if (id is not null) _moduleIds.Add(id.Value);
            _log.LogInformation("Loopback {Name} to {Sink}", name, _razerSink);
        }

        if (postMixerTarget is not null)
        {
            int? id = await _pulse.LoadModuleAsync("module-loopback",
                $"source={_razerSink}.monitor sink={postMixerTarget} latency_msec=20", ct);
            if (id is not null) _moduleIds.Add(id.Value);
            _log.LogInformation("Post-mixer loopback {Razer} to {Target}", _razerSink, postMixerTarget);
        }
        else
        {
            _log.LogWarning("No post-mixer destination, mixer audio stops at the hardware sink");
        }

        await _pulse.SetDefaultSinkAsync(LandingSink, ct);
        _log.LogInformation("Default sink set to {Sink}", LandingSink);

        await RehomeSinkInputsAsync(ct);

        _router.FaderSinkNames = FaderSinkNames;
        _router.Device = _device;
    }

    private async Task RehomeSinkInputsAsync(CancellationToken ct, int? onlyIndex = null)
    {
        var inputs = await _pulse.ListSinkInputsAsync(ct);
        foreach (var input in inputs)
        {
            if (onlyIndex is not null && input.Index != onlyIndex.Value)
                continue;
            if (input.SinkName.StartsWith("razer_fader") || input.SinkName == _razerSink)
                continue;

            string target = LandingSink;
            if (input.AppName is not null &&
                _config.Audio.Routing.TryGetValue(input.AppName, out string? rule))
            {
                if (rule.Equals("stay", StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogInformation("{App} stays on {Sink}", input.AppName, input.SinkName);
                    continue;
                }
                target = rule;
            }

            _log.LogInformation("Rehoming {App} (#{Idx}) from {From} to {To}",
                input.AppName, input.Index, input.SinkName, target);
            await _pulse.MoveSinkInputAsync(input.Index, target, ct);
        }
    }

    private async Task TeardownPulseAsync(CancellationToken ct)
    {
        if (_previousDefault is not null)
        {
            _log.LogInformation("Restoring default to {Sink}", _previousDefault);
            await _pulse.SetDefaultSinkAsync(_previousDefault, ct);
        }

        foreach (int id in Enumerable.Reverse(_moduleIds))
        {
            try { await _pulse.UnloadModuleAsync(id, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to unload module {Id}", id); }
        }
        _moduleIds.Clear();
        _router.FaderSinkNames = [];
        _router.Device = null;
        _razerSink = null;
        _previousDefault = null;
    }

    // PA subscribe loop, rehomes new sink-inputs that appear after device plug-in

    private async Task SubscribeLoopAsync(CancellationToken ct)
    {
        _log.LogInformation("PA subscribe loop started");
        try
        {
            await foreach (var ev in _pulse.SubscribeAsync(ct))
            {
                if (ev.Type != "new" || ev.Facility != "sink-input")
                    continue;
                try
                {
                    await RehomeSinkInputsAsync(ct, onlyIndex: ev.Index);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to rehome sink-input #{Idx}", ev.Index);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "PA subscribe loop crashed");
        }
        _log.LogInformation("PA subscribe loop stopped");
    }

    // Read loop

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        _log.LogInformation("Input read loop started");
        InputReport? prev = null;
        bool[] logicalMute = new bool[6];   // 1-indexed, [1]=ch1 .. [5]=mic

        while (!ct.IsCancellationRequested)
        {
            byte[]? raw = _device?.ReadInputReport();
            if (raw is null) continue;

            var report = InputReport.TryParse(raw);
            if (report is null) continue;

            if (prev is null) { prev = report; continue; }

            foreach (var ev in report.Diff(prev, logicalMute))
            {
                switch (ev)
                {
                    case FaderMoved fm:
                        await _router.HandleFaderMovedAsync(fm, ct);
                        break;

                    case MuteToggled mt:
                        await _router.HandleMuteToggledAsync(mt, ct);
                        break;
                }
            }

            prev = report;
        }
        _log.LogInformation("Input read loop stopped");
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        _watcher.Dispose();
        await OnDeviceRemovedAsync(ct);
        await base.StopAsync(ct);
    }
}