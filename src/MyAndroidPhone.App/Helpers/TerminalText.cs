using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using MyAndroidPhone.Android;
using MyAndroidPhone.Desktop.ViewModels;
using Windows.UI;

namespace MyAndroidPhone.Desktop.Helpers;

/// <summary>
/// Turns one terminal line into colored text. Used as an attached property so that a list item template can stay a plain
/// TextBlock: <c>h:TerminalText.Line="{x:Bind}"</c>.
/// </summary>
public static class TerminalText
{
    public static readonly DependencyProperty LineProperty = DependencyProperty.RegisterAttached(
        "Line", typeof(TerminalLineItem), typeof(TerminalText), new PropertyMetadata(null, OnLineChanged));

    public static TerminalLineItem? GetLine(DependencyObject element) => (TerminalLineItem?)element.GetValue(LineProperty);

    public static void SetLine(DependencyObject element, TerminalLineItem? value) => element.SetValue(LineProperty, value);

    private static readonly Dictionary<uint, SolidColorBrush> Brushes = new();

    private static readonly Color Text = Color.FromArgb(255, 230, 237, 243);
    private static readonly Color Green = Color.FromArgb(255, 126, 231, 135);
    private static readonly Color Blue = Color.FromArgb(255, 121, 192, 255);
    private static readonly Color Purple = Color.FromArgb(255, 210, 168, 255);
    private static readonly Color Orange = Color.FromArgb(255, 255, 166, 87);
    private static readonly Color LightBlue = Color.FromArgb(255, 165, 214, 255);
    private static readonly Color Red = Color.FromArgb(255, 255, 123, 114);
    private static readonly Color Yellow = Color.FromArgb(255, 227, 179, 65);
    private static readonly Color Grey = Color.FromArgb(255, 139, 148, 158);
    private static readonly Color Accent = Color.FromArgb(255, 88, 166, 255);

    private static void OnLineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock block)
        {
            Render(block, e.NewValue as TerminalLineItem);
        }
    }

    private static SolidColorBrush Brush(Color color)
    {
        var key = (uint)(color.A << 24 | color.R << 16 | color.G << 8 | color.B);
        if (!Brushes.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush(color);
            Brushes[key] = brush;
        }

        return brush;
    }

    private static Run Piece(string text, Color color, bool bold = false) =>
        new() { Text = text, Foreground = Brush(color), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };

    private static void Render(TextBlock block, TerminalLineItem? line)
    {
        block.Inlines.Clear();
        if (line is null)
        {
            return;
        }

        switch (line.Kind)
        {
            case TerminalLineKind.Command:
                block.Inlines.Add(Piece(line.Prefix + " ", Accent, bold: true));
                foreach (var token in line.Tokens)
                {
                    block.Inlines.Add(Piece(token.Text, token.Kind switch
                    {
                        TokenKind.Command => Green,
                        TokenKind.Subcommand => Blue,
                        TokenKind.Flag => Purple,
                        TokenKind.Path => Orange,
                        TokenKind.String => LightBlue,
                        TokenKind.Number => Blue,
                        _ => Text
                    }, bold: token.Kind == TokenKind.Command));
                }

                break;

            case TerminalLineKind.Output:
                foreach (var segment in OutputHighlighter.Highlight(line.Text))
                {
                    block.Inlines.Add(Piece(segment.Text, segment.Kind switch
                    {
                        OutputKind.Path => Orange,
                        OutputKind.Number => Blue,
                        OutputKind.Address => Purple,
                        OutputKind.Error => Red,
                        OutputKind.Success => Green,
                        OutputKind.Warning => Yellow,
                        OutputKind.Dim => Grey,
                        _ => Text
                    }));
                }

                break;

            default:
                block.Inlines.Add(Piece(line.Text, line.Kind switch
                {
                    TerminalLineKind.Error => Red,
                    TerminalLineKind.Success => Green,
                    TerminalLineKind.Warning => Yellow,
                    TerminalLineKind.Info => Blue,
                    TerminalLineKind.Directory => Blue,
                    TerminalLineKind.Hint => Grey,
                    _ => Text
                }, bold: line.Kind == TerminalLineKind.Directory));
                break;
        }
    }
}
