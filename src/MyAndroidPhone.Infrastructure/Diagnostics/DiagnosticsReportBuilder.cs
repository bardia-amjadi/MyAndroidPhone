using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using MyAndroidPhone.Core.Devices;
using MyAndroidPhone.Core.Logging;
using MyAndroidPhone.Core.Platform;
using MyAndroidPhone.Core.Settings;
using MyAndroidPhone.Core.Tools;
using MyAndroidPhone.Infrastructure.Logging;
using MyAndroidPhone.Infrastructure.Storage;

namespace MyAndroidPhone.Infrastructure.Diagnostics;

/// <summary>
/// Builds the exportable diagnostics text. It contains application, OS and tool facts plus recent errors only:
/// no device contents, no file listings, no secrets (the whole report is redacted before it is returned).
/// </summary>
public sealed class DiagnosticsReportBuilder(
    ISystemCompatibilityService compatibility,
    IToolService tools,
    IRecentLogSource recentLog,
    ISettingsService settings,
    IDeviceService devices,
    AppPaths paths)
{
    public static string ApplicationVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";

    private static string MaskSerial(string serial) =>
        serial.Length <= 4 ? "****" : serial[..2] + "***" + serial[^2..];

    public string Build()
    {
        var report = compatibility.Check();
        var builder = new StringBuilder();

        builder.AppendLine("My Android Phone diagnostics");
        builder.AppendLine($"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine();

        builder.AppendLine("== Application ==");
        builder.AppendLine($"Version: {ApplicationVersion}");
        builder.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        builder.AppendLine($"Data folder: {paths.Root}");
        builder.AppendLine($"Logs folder: {paths.LogsDirectory}");
        builder.AppendLine();

        builder.AppendLine("== System ==");
        builder.AppendLine($"OS: {report.OsDescription} ({report.OsVersion})");
        builder.AppendLine($"Architecture: {report.Architecture}");
        builder.AppendLine($"Supported: {(report.IsSupported ? "yes" : "NO")}");
        foreach (var issue in report.Issues)
        {
            builder.AppendLine($"  Issue: {issue}");
        }

        foreach (var warning in report.Warnings)
        {
            builder.AppendLine($"  Warning: {warning}");
        }

        builder.AppendLine();

        builder.AppendLine("== Tools ==");
        foreach (var tool in tools.Installations)
        {
            builder.AppendLine($"{tool.Definition.DisplayName}: {tool.Status}");
            if (tool.Version is not null)
            {
                builder.AppendLine($"  Version: {tool.Version}");
            }

            if (tool.ExecutablePath is not null)
            {
                builder.AppendLine($"  Path: {tool.ExecutablePath} (found via {tool.Source})");
            }

            if (!string.IsNullOrEmpty(tool.Message))
            {
                builder.AppendLine($"  Note: {tool.Message}");
            }
        }

        builder.AppendLine();

        var current = settings.Current;
        builder.AppendLine("== Settings ==");
        builder.AppendLine($"Theme: {current.Theme}");
        builder.AppendLine($"Log detail: {current.LogVerbosity}, size limit {current.LogFileSizeMegabytes} MB, {current.LogRetainedFiles} files kept");
        builder.AppendLine();

        builder.AppendLine("== Devices ==");
        builder.AppendLine($"Status: {devices.StatusMessage}");
        foreach (var device in devices.Devices)
        {
            // Serials identify a physical device, so only a masked form is exported.
            var hw = device.Hardware;
            builder.Append($"{MaskSerial(device.Serial)}: {device.State}, {device.ConnectionType}");
            if (hw is not null)
            {
                builder.Append($", {hw.Manufacturer} {hw.Model}, Android {hw.AndroidVersion} (API {hw.ApiLevel?.ToString() ?? "?"})");
                if (hw.ScreenWidth is not null)
                {
                    builder.Append($", {hw.ScreenWidth}x{hw.ScreenHeight} @ {hw.DensityDpi} dpi");
                }

                if (hw.Battery?.Level is not null)
                {
                    builder.Append($", battery {hw.Battery.Level}%");
                }
            }

            builder.AppendLine();
            foreach (var capability in device.Capabilities.Where(c => !c.Supported))
            {
                builder.AppendLine($"    Unavailable: {capability.Name} - {capability.Reason}");
            }
        }

        builder.AppendLine();

        builder.AppendLine("== Recent errors and warnings ==");
        var problems = recentLog.GetRecentLines()
            .Where(line => line.Contains("[ERR]", StringComparison.Ordinal)
                           || line.Contains("[CRT]", StringComparison.Ordinal)
                           || line.Contains("[WRN]", StringComparison.Ordinal))
            .TakeLast(50)
            .ToList();
        if (problems.Count == 0)
        {
            builder.AppendLine("None recorded in this session.");
        }
        else
        {
            foreach (var line in problems)
            {
                builder.AppendLine(line);
            }
        }

        return LogRedactor.Redact(builder.ToString());
    }
}
