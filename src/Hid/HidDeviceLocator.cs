// Locates the /dev/hidrawN node for the Razer Audio Mixer's HID control interface
namespace RazerMixerBridge.Hid;

public static class HidDeviceLocator
{
    private const string TargetHidId = "HID_ID=0003:00001532:0000053E";

    /// <summary>
    /// Returns the /dev/hidrawN path for the mixer, or null if the device is not connected / not yet visible in sysfs
    /// </summary>
    public static string? FindHidrawPath()
    {
        const string hidrawBase = "/sys/class/hidraw";
        if (!Directory.Exists(hidrawBase))
            return null;

        foreach (string hidrawDir in Directory.EnumerateDirectories(hidrawBase))
        {
            string ueventPath = Path.Combine(hidrawDir, "device", "uevent");
            if (!File.Exists(ueventPath))
                continue;

            string uevent = File.ReadAllText(ueventPath);
            if (!uevent.Contains(TargetHidId, StringComparison.OrdinalIgnoreCase))
                continue;

            return $"/dev/{Path.GetFileName(hidrawDir)}";
        }

        return null;
    }
}