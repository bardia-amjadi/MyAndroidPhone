using MyAndroidPhone.Core.Instances;

namespace MyAndroidPhone.Desktop.Services;

/// <summary>
/// The instance whose desktop is open. Modules read the device serial from here so that every module of a desktop
/// controls the same phone, whatever is selected in Device Center.
/// </summary>
public sealed class ActiveInstance
{
    public AndroidInstance? Current { get; private set; }

    public void Set(AndroidInstance? instance) => Current = instance;
}
