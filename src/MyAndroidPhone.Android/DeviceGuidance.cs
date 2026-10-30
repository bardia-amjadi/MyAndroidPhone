using MyAndroidPhone.Core.Devices;

namespace MyAndroidPhone.Android;

/// <summary>User-facing explanations for device states and capability checks.</summary>
public static class DeviceGuidance
{
    public static string? ForState(DeviceConnectionState state, DeviceConnectionType type) => state switch
    {
        DeviceConnectionState.Online => null,
        DeviceConnectionState.Unauthorized =>
            "This computer is not authorized. Unlock the phone and tap Allow on the \"Allow USB debugging?\" prompt (tick \"Always allow from this computer\" if you trust it). " +
            "If no prompt appears, unplug and reconnect the cable, or turn on Developer options > Revoke USB debugging authorizations and try again.",
        DeviceConnectionState.Authorizing =>
            "Waiting for you to approve the debugging prompt on the phone.",
        DeviceConnectionState.Offline =>
            "adb sees the device but it is not responding. Unlock the phone, then unplug and reconnect it, or use Restart adb. " +
            (type == DeviceConnectionType.Wireless ? "For a wireless device, check that both devices are on the same network and Wireless debugging is still on." : "Try a different cable or USB port."),
        DeviceConnectionState.NoPermissions =>
            "The system refused access to the device. Install the manufacturer's USB driver (or Google's USB driver), then reconnect it.",
        DeviceConnectionState.Connecting =>
            "adb is still connecting to the device.",
        DeviceConnectionState.Recovery or DeviceConnectionState.Sideload or DeviceConnectionState.Bootloader =>
            $"The device is in {state.ToString().ToLowerInvariant()} mode. File and app features need Android to be booted normally.",
        DeviceConnectionState.Disconnected => type switch
        {
            DeviceConnectionType.Wireless => "Not connected right now. Connect to it again from the Wi-Fi section; pairing is only needed again if Android forgot this computer.",
            _ => "Not connected right now. Plug it in, unlock it, and make sure USB debugging is on."
        },
        _ => null
    };

    public static IReadOnlyList<DeviceCapability> CapabilitiesFor(DeviceConnectionState state, DeviceConnectionType type, DeviceHardware? hardware)
    {
        var online = state == DeviceConnectionState.Online;
        var api = hardware?.ApiLevel;
        var list = new List<DeviceCapability>
        {
            new("adb-shell", "Run adb commands", online,
                online ? "The device is online and authorized." : $"Needs an online, authorized device (state: {state}).")
        };

        list.Add(api switch
        {
            null => new DeviceCapability("wireless-pairing", "Wireless debugging pairing", false, "Android version not read yet."),
            >= 30 => new DeviceCapability("wireless-pairing", "Wireless debugging pairing", true, $"Android API {api} supports pairing with a code."),
            _ => new DeviceCapability("wireless-pairing", "Wireless debugging pairing", false, $"Needs Android 11 (API 30) or later; this device reports API {api}. Use USB, then switch to Wi-Fi.")
        });

        list.Add(online && type == DeviceConnectionType.Usb
            ? new DeviceCapability("tcpip", "Switch to Wi-Fi (adb tcpip)", true, "Possible while connected over USB.")
            : new DeviceCapability("tcpip", "Switch to Wi-Fi (adb tcpip)", false, "Needs the device connected over USB and online."));

        list.Add(api switch
        {
            null => new DeviceCapability("audio-forwarding", "Audio forwarding (scrcpy)", false, "Android version not read yet."),
            >= 30 => new DeviceCapability("audio-forwarding", "Audio forwarding (scrcpy)", true, "Android 11 or later is required, and this device qualifies."),
            _ => new DeviceCapability("audio-forwarding", "Audio forwarding (scrcpy)", false, $"Needs Android 11 (API 30) or later; this device reports API {api}.")
        });

        return list;
    }
}
