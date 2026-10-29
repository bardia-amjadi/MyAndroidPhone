using MyAndroidPhone.Core.Tools;

namespace MyAndroidPhone.Core.Features;

/// <summary>
/// Describes a feature module. The desktop shell builds its Dock and menus from these descriptors, so a new
/// feature appears by registering a descriptor (and a view factory in the App project) - the shell is not edited.
/// </summary>
/// <param name="Id">Stable identifier, e.g. "tool-manager".</param>
/// <param name="IconGlyph">Segoe Fluent Icons / Segoe MDL2 Assets glyph.</param>
/// <param name="RequiredTools">Tools that must be usable; otherwise the shell explains what is missing instead of opening the feature.</param>
/// <param name="RequiredDeviceCapabilities">Capability keys a target device must support (checked once the device service exists).</param>
/// <param name="SupportedActions">Human-readable list of actions the feature offers.</param>
public sealed record FeatureDescriptor(
    string Id,
    string DisplayName,
    string Description,
    string IconGlyph,
    int Order,
    IReadOnlyList<ToolId> RequiredTools,
    IReadOnlyList<string> RequiredDeviceCapabilities,
    IReadOnlyList<string> SupportedActions,
    bool ShowInDock = true);

public interface IFeatureRegistry
{
    IReadOnlyList<FeatureDescriptor> All { get; }

    FeatureDescriptor? Find(string id);

    void Register(FeatureDescriptor descriptor);
}

public sealed class FeatureRegistry : IFeatureRegistry
{
    private readonly List<FeatureDescriptor> _features = new();

    public IReadOnlyList<FeatureDescriptor> All =>
        _features.OrderBy(f => f.Order).ThenBy(f => f.DisplayName, StringComparer.Ordinal).ToList();

    public FeatureDescriptor? Find(string id) =>
        _features.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.Ordinal));

    public void Register(FeatureDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(descriptor.Id))
        {
            throw new ArgumentException("A feature needs a non-empty id.", nameof(descriptor));
        }

        if (Find(descriptor.Id) is not null)
        {
            throw new InvalidOperationException($"A feature with id '{descriptor.Id}' is already registered.");
        }

        _features.Add(descriptor);
    }
}
