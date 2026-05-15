// Wrapper around the hidraw node for the Razer Audio Mixer
// SET_REPORT (EP0 class control transfer) is sent via HidSharp's SetFeature(), which uses the HIDIOCSFEATURE ioctl under the hood on Linux
// Input reports (EP 0x84, interrupt-IN) arrive via blocking reads on the HidStream. HidSharp's Read() returns one complete report per call
using HidSharp;
using Microsoft.Extensions.Logging;

namespace RazerMixerBridge.Hid;

public sealed class RazerHidDevice : IDisposable
{
    private readonly HidStream _stream;
    private readonly ILogger<RazerHidDevice> _log;

    public RazerHidDevice(string hidrawPath, ILogger<RazerHidDevice> log)
    {
        _log = log;
        var deviceList = DeviceList.Local;

        // HidSharp identifies Linux hidraw devices by path.
        var device = deviceList.GetHidDevices()
            .FirstOrDefault(d => d.DevicePath == hidrawPath)
            ?? throw new InvalidOperationException($"HID device not found: {hidrawPath}");

        var openConfig = new OpenConfiguration();
        openConfig.SetOption(OpenOption.Interruptible, true);

        _stream = device.Open(openConfig);
        _stream.ReadTimeout = 100;   // ms; non-blocking feel for the read loop
        _stream.WriteTimeout = 1000;
        _log.LogDebug("Opened HID device {Path}", hidrawPath);
    }

    /// <summary>
    /// Sends a Feature report (SET_REPORT via HIDIOCSFEATURE ioctl)
    /// payload[0] must be the report ID
    /// </summary>
    public void WriteFeatureReport(byte[] payload)
    {
        try
        {
            _stream.SetFeature(payload);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SetFeature failed (report 0x{Id:X2})", payload[0]);
        }
    }

    /// <summary>
    /// Reads the next interrupt-IN report (EP 0x84). Returns null on timeout
    /// Blocks up to ReadTimeout ms
    /// </summary>
    public byte[]? ReadInputReport()
    {
        try
        {
            return _stream.Read();
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "HID read failed");
            return null;
        }
    }

    public void Dispose()
    {
        _stream.Close();
        _stream.Dispose();
    }
}
