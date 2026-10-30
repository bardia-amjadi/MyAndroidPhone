using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using MyAndroidPhone.Core.Features;
using MyAndroidPhone.Core.Tools;
using MyAndroidPhone.Desktop.Views;
using Windows.Foundation;

namespace MyAndroidPhone.Desktop.Services;

public static class ModuleIds
{
    public const string DeviceCenter = "device-center";
    public const string ToolManager = "tool-manager";
    public const string Settings = "settings";
    public const string Diagnostics = "diagnostics";
}

/// <summary>
/// The features that exist in this build. Dock order slots reserved for later modules: File Explorer 10, Run 20,
/// App Manager 40, Device Center 60, Transfer Center 70. They are registered only when they are implemented.
/// </summary>
public static class BuiltInFeatures
{
    public static IFeatureRegistry CreateRegistry()
    {
        var registry = new FeatureRegistry();

        registry.Register(new FeatureDescriptor(
            ModuleIds.DeviceCenter,
            "Device Center",
            "See connected Android devices, connect over USB or Wi-Fi, and name them.",
            "\uE8EA",
            60,
            Array.Empty<ToolId>(),
            Array.Empty<string>(),
            new[] { "List devices", "Pair and connect wirelessly", "Switch USB device to Wi-Fi", "Rename and recolor devices" }));

        registry.Register(new FeatureDescriptor(
            ModuleIds.ToolManager,
            "Tool Manager",
            "Detect, verify and select the external tools the app uses (adb, scrcpy).",
            "",
            30,
            Array.Empty<ToolId>(),
            Array.Empty<string>(),
            new[] { "Detect tools", "Verify versions", "Select executable", "Open tool folder" }));

        registry.Register(new FeatureDescriptor(
            ModuleIds.Settings,
            "Settings",
            "Application settings: theme, Dock, logging and data.",
            "",
            50,
            Array.Empty<ToolId>(),
            Array.Empty<string>(),
            new[] { "Change theme", "Configure logging", "Reset settings" }));

        registry.Register(new FeatureDescriptor(
            ModuleIds.Diagnostics,
            "Logs and Diagnostics",
            "System and tool information, recent log entries and an exportable report.",
            "",
            80,
            Array.Empty<ToolId>(),
            Array.Empty<string>(),
            new[] { "View recent log", "Export diagnostics report", "Open logs folder" }));

        return registry;
    }
}

/// <summary>Maps a feature id to the view that implements it. Registering a feature means adding one entry here.</summary>
public sealed class ModuleViewFactory
{
    private readonly Dictionary<string, (Func<FrameworkElement> Create, Size PreferredSize)> _entries;

    public ModuleViewFactory(IServiceProvider services)
    {
        _entries = new Dictionary<string, (Func<FrameworkElement>, Size)>
        {
            [ModuleIds.DeviceCenter] = (() => services.GetRequiredService<DeviceCenterView>(), new Size(940, 640)),
            [ModuleIds.ToolManager] = (() => services.GetRequiredService<ToolManagerView>(), new Size(780, 580)),
            [ModuleIds.Settings] = (() => services.GetRequiredService<SettingsView>(), new Size(680, 600)),
            [ModuleIds.Diagnostics] = (() => services.GetRequiredService<DiagnosticsView>(), new Size(860, 600))
        };
    }

    public bool CanCreate(string id) => _entries.ContainsKey(id);

    public FrameworkElement Create(string id) => _entries[id].Create();

    public Size PreferredSize(string id) => _entries.TryGetValue(id, out var entry) ? entry.PreferredSize : new Size(720, 520);
}
