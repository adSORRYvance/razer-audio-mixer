// Watches for the Razer Audio Mixer being plugged / unplugged.
// HidSharp fires DeviceList.Changed whenever the hidraw device list changes (inotify on Linux). On each change we re-run the locator; if the result differs from the last seen path we emit DeviceArrived or DeviceRemoved
using HidSharp;
using Microsoft.Extensions.Logging;

namespace RazerMixerBridge.Hid;

public sealed class HidDeviceWatcher : IDisposable
{
    private readonly ILogger<HidDeviceWatcher> _log;
    private string? _currentPath;
    private volatile bool _started;

    public event Action<string>? DeviceArrived;
    public event Action? DeviceRemoved;

    public HidDeviceWatcher(ILogger<HidDeviceWatcher> log) => _log = log;

    public void Start()
    {
        if (_started) return;
        _started = true;

        DeviceList.Local.Changed += OnChanged;

        // Check immediately — the device may already be plugged in.
        CheckNow();
    }

    private void OnChanged(object? sender, DeviceListChangedEventArgs e) => CheckNow();

    private void CheckNow()
    {
        string? found = HidDeviceLocator.FindHidrawPath();
        if (found == _currentPath) return;

        if (found is not null && _currentPath is null)
        {
            _log.LogInformation("Razer Audio Mixer arrived at {Path}", found);
            _currentPath = found;
            DeviceArrived?.Invoke(found);
        }
        else if (found is null && _currentPath is not null)
        {
            _log.LogInformation("Razer Audio Mixer removed (was {Path})", _currentPath);
            _currentPath = null;
            DeviceRemoved?.Invoke();
        }
    }

    public void Dispose() => DeviceList.Local.Changed -= OnChanged;
}
