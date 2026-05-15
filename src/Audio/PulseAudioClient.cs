// PulseAudio / PipeWire control via pactl subprocesses
//
// All routing operations (create sinks, loopbacks, volume, mute, default-sink, sink-input moves) go through pactl. This works transparently with PipeWire's pipewire-pulse shim
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace RazerMixerBridge.Audio;

/// <summary>One row from `pactl list sink-inputs` — only the fields we use for rehoming</summary>
public sealed record SinkInputInfo(int Index, string SinkName, string? AppName);

/// <summary>One line from `pactl subscribe`, e.g. "Event 'new' on sink-input #42"</summary>
public sealed record PulseEvent(string Type, string Facility, int Index);

public sealed class PulseAudioClient
{
    private readonly ILogger<PulseAudioClient> _log;

    public PulseAudioClient(ILogger<PulseAudioClient> log) => _log = log;

    // -- Module management --------------------------------------------------

    /// <summary>Loads a PA module and returns its numeric module ID</summary>
    public async Task<int?> LoadModuleAsync(string module, string args,
        CancellationToken ct = default)
    {
        string output = await RunAsync(["load-module", module, .. args.Split(' ', StringSplitOptions.RemoveEmptyEntries)], ct);
        if (int.TryParse(output.Trim(), out int id))
            return id;
        _log.LogWarning("load-module {Module} returned unexpected output: {Out}", module, output);
        return null;
    }

    public async Task UnloadModuleAsync(int moduleId, CancellationToken ct = default) =>
        await RunAsync(["unload-module", moduleId.ToString()], ct);

    // -- Sink management --------------------------------------------------

    /// <summary>
    /// Returns the name of the first sink whose description contains
    /// <paramref name="substringCi"/> (case-insensitive), or null
    /// </summary>
    public async Task<string?> FindSinkByDescriptionAsync(string substringCi,
        CancellationToken ct = default)
    {
        // `pactl list sinks` output has alternating "Name:" / "Description:" lines
        string output = await RunAsync(["list", "sinks"], ct);
        string? currentName = null;
        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("Name:"))
                currentName = trimmed["Name:".Length..].Trim();
            else if (trimmed.StartsWith("Description:") && currentName is not null)
            {
                string desc = trimmed["Description:".Length..].Trim();
                if (desc.Contains(substringCi, StringComparison.OrdinalIgnoreCase))
                    return currentName;
                currentName = null;
            }
        }
        return null;
    }

    /// <summary>Returns the current PA default sink name</summary>
    public async Task<string?> GetDefaultSinkAsync(CancellationToken ct = default)
    {
        string output = await RunAsync(["get-default-sink"], ct);
        string name = output.Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>Sets the PA default sink</summary>
    public async Task SetDefaultSinkAsync(string sinkName, CancellationToken ct = default) =>
        await RunAsync(["set-default-sink", sinkName], ct);

    /// <summary>
    /// Returns the sink-id to the sink-name map built from `pactl list sinks short`
    /// Useful for resolving the numeric Sink IDs that appear in `pactl list sink-inputs`
    /// </summary>
    private async Task<Dictionary<int, string>> GetSinkIdNameMapAsync(CancellationToken ct)
    {
        string output = await RunAsync(["list", "sinks", "short"], ct);
        var map = new Dictionary<int, string>();
        foreach (string line in output.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] cols = line.Split('\t');
            if (cols.Length >= 2 && int.TryParse(cols[0], out int id))
                map[id] = cols[1];
        }
        return map;
    }

    // -- Sink-input enumeration & move (for rehoming) -----------------------

    /// <summary>
    /// Enumerates active sink-inputs
    /// Only returns inputs that have an application.name set, this filters out most PA-internal streams like module-loopback sink-inputs
    /// </summary>
    public async Task<List<SinkInputInfo>> ListSinkInputsAsync(CancellationToken ct = default)
    {
        var sinkIdToName = await GetSinkIdNameMapAsync(ct);
        string output = await RunAsync(["list", "sink-inputs"], ct);

        var results = new List<SinkInputInfo>();
        int currentIndex = -1;
        int currentSinkId = -1;
        string? currentAppName = null;

        void Flush()
        {
            if (currentIndex >= 0 &&
                !string.IsNullOrEmpty(currentAppName) &&
                sinkIdToName.TryGetValue(currentSinkId, out var name))
            {
                results.Add(new SinkInputInfo(currentIndex, name, currentAppName));
            }
            currentIndex = -1;
            currentSinkId = -1;
            currentAppName = null;
        }

        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("Sink Input #"))
            {
                Flush();
                currentIndex = int.TryParse(trimmed["Sink Input #".Length..], out int id) ? id : -1;
            }
            else if (currentSinkId == -1 && trimmed.StartsWith("Sink:"))
            {
                currentSinkId = int.TryParse(trimmed["Sink:".Length..].Trim(), out int sid) ? sid : -1;
            }
            else if (trimmed.StartsWith("application.name ="))
            {
                int eq = trimmed.IndexOf('=');
                currentAppName = trimmed[(eq + 1)..].Trim().Trim('"');
            }
        }
        Flush();
        return results;
    }

    /// <summary>Moves a sink-input to a different sink</summary>
    public async Task MoveSinkInputAsync(int index, string targetSink, CancellationToken ct = default) =>
        await RunAsync(["move-sink-input", index.ToString(), targetSink], ct);

    // -- Volume / mute ------------------------------------------------------

    /// <summary>
    /// Sets a sink's volume. fraction is 0..1; sends as integer percentage
    /// </summary>
    public async Task SetSinkVolumeAsync(string sinkName, float fraction,
        CancellationToken ct = default)
    {
        int pct = (int)Math.Round(Math.Clamp(fraction, 0f, 1f) * 100);
        await RunAsync(["set-sink-volume", sinkName, $"{pct}%"], ct);
    }

    /// <summary>Sets a sink's mute state</summary>
    public async Task SetSinkMuteAsync(string sinkName, bool mute,
        CancellationToken ct = default) =>
        await RunAsync(["set-sink-mute", sinkName, mute ? "1" : "0"], ct);

    // -- Stale module cleanup -----------------------------------------------

    /// <summary>
    /// Unloads any null-sinks and loopbacks left from a previous daemon run (crash / hard-kill) Identifies them by sink_name= or source= token match
    /// </summary>
    public async Task RemoveStaleModulesAsync(
        IEnumerable<string> sinkNames, string? razerMixerSink,
        CancellationToken ct = default)
    {
        var nullSinkTokens = sinkNames.Select(n => $"sink_name={n}").ToHashSet();
        var loopbackSources = sinkNames.Select(n => $"source={n}.monitor").ToHashSet();
        if (razerMixerSink is not null)
            loopbackSources.Add($"source={razerMixerSink}.monitor");

        string output = await RunAsync(["list", "modules"], ct);
        // Parse: "Module #N" then "Name: ..." then "Argument: ..."
        int moduleId = -1;
        string? moduleName = null;
        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("Module #"))
            {
                moduleId = int.TryParse(trimmed["Module #".Length..], out int id) ? id : -1;
                moduleName = null;
            }
            else if (trimmed.StartsWith("Name:"))
                moduleName = trimmed["Name:".Length..].Trim();
            else if (trimmed.StartsWith("Argument:") && moduleId >= 0)
            {
                string args = trimmed["Argument:".Length..].Trim();
                var tokens = new HashSet<string>(args.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                bool isOurs =
                    (moduleName == "module-null-sink" && nullSinkTokens.Overlaps(tokens)) ||
                    (moduleName == "module-loopback" && loopbackSources.Overlaps(tokens));
                if (isOurs)
                {
                    _log.LogInformation("Removing stale {Name} #{Id} ({Args})",
                        moduleName, moduleId, args);
                    await UnloadModuleAsync(moduleId, ct);
                }
                moduleId = -1;
            }
        }
    }

    // -- Event subscription -------------------------------------------------

    private static readonly Regex EventPattern = new(
        @"Event '(?<type>\w+)' on (?<facility>[\w-]+) #(?<index>\d+)",
        RegexOptions.Compiled);

    /// <summary>
    /// Streams PulseAudio events from a long-lived `pactl subscribe` subprocess
    /// Yields one PulseEvent per parseable line; non-matching lines are dropped
    /// On cancellation the subprocess is killed (pactl subscribe never exits on its own)
    /// </summary>
    public async IAsyncEnumerable<PulseEvent> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var psi = new ProcessStartInfo("pactl")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("subscribe");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start pactl subscribe");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line = await proc.StandardOutput.ReadLineAsync(ct);
                if (line is null) break;

                var m = EventPattern.Match(line);
                if (m.Success && int.TryParse(m.Groups["index"].Value, out int idx))
                    yield return new PulseEvent(
                        m.Groups["type"].Value,
                        m.Groups["facility"].Value,
                        idx);
            }
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch {}
        }
    }

    // -- Internal -----------------------------------------------------------

    private async Task<string> RunAsync(string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("pactl")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)!;
        string stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        string stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
            _log.LogWarning("pactl {Args} exited {Code}: {Err}",
                string.Join(' ', args), proc.ExitCode, stderr.Trim());

        return stdout;
    }
}
