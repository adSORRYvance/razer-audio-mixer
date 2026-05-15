// Locates the /dev/hidrawN node for the Razer Audio Mixer's HID interface 6
// The mixer exposes several HID interfaces; the control plane (SET_REPORT / GET_REPORT for DSP and lighting) lives on interface 6. We find it by walking /sys/class/hidraw/ and checking:
//   1. uevent file contains HID_ID=0003:00001532:0000053E  (bus=USB, vid/pid)
//   2. ../../bInterfaceNumber == "06"
// The sysfs path for hidrawN is:
//   /sys/class/hidraw/hidrawN/device/  → the HID device node
//   /sys/class/hidraw/hidrawN/device/../../  → the USB interface node
//   /sys/class/hidraw/hidrawN/device/../../bInterfaceNumber
namespace RazerMixerBridge.Hid;

public static class HidDeviceLocator
{
    private const string TargetHidId = "HID_ID=0003:00001532:0000053E";
    private const string TargetInterface = "06";

    /// <summary>
    /// Returns the /dev/hidrawN path for the mixer's interface 6, or null if the device is not connected / not yet visible in sysfs
    /// </summary>
    public static string? FindHidrawPath()
    {
        const string hidrawBase = "/sys/class/hidraw";
        if (!Directory.Exists(hidrawBase))
            return null;

        foreach (string hidrawDir in Directory.EnumerateDirectories(hidrawBase))
        {
            string name = Path.GetFileName(hidrawDir); // "hidraw0", "hidraw1", …

            string ueventPath = Path.Combine(hidrawDir, "device", "uevent");
            if (!File.Exists(ueventPath))
                continue;

            string uevent = File.ReadAllText(ueventPath);
            if (!uevent.Contains(TargetHidId, StringComparison.OrdinalIgnoreCase))
                continue;

            // Walk up two levels from the HID device node to the USB interfacew
            string ifaceNumPath = Path.Combine(
                hidrawDir, "device", "..", "..", "bInterfaceNumber");
            string? ifaceNum = ReadSysfsFile(ifaceNumPath);
            if (ifaceNum?.Trim() != TargetInterface)
                continue;

            return $"/dev/{name}";
        }

        return null;
    }

    private static string? ReadSysfsFile(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return null; }
    }
}
