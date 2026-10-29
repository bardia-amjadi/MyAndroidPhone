using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MyAndroidPhone.Desktop.Views;

public sealed partial class ModuleFrame : UserControl
{
    public ModuleFrame(string title, string featureId, UIElement content)
    {
        InitializeComponent();
        FeatureId = featureId;
        TitleText.Text = title;
        AutomationProperties.SetName(CloseButton, $"Close {title}");
        Presenter.Content = content;
    }

    public string FeatureId { get; }

    /// <summary>The draggable title bar; the shell attaches pointer handlers to it.</summary>
    public UIElement DragHandle => TitleBar;

    public event EventHandler? CloseRequested;

    /// <summary>Removes the hosted module so it is unloaded (and its view model disposed).</summary>
    public void Detach() => Presenter.Content = null;

    private void OnCloseClicked(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
