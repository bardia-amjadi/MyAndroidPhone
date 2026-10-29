using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyAndroidPhone.Desktop.ViewModels;

namespace MyAndroidPhone.Desktop.Views;

/// <summary>Displays one <see cref="ToolItemViewModel"/>; shared by the Tool Manager and the setup wizard.</summary>
public sealed partial class ToolCard : UserControl
{
    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(nameof(Item), typeof(ToolItemViewModel), typeof(ToolCard),
            new PropertyMetadata(null, (d, _) => ((ToolCard)d).Bindings?.Update()));

    public ToolCard()
    {
        InitializeComponent();
    }

    public ToolItemViewModel Item
    {
        get => (ToolItemViewModel)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }
}
