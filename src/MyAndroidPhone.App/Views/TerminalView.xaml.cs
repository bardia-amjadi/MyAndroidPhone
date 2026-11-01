using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MyAndroidPhone.Android;
using MyAndroidPhone.Desktop.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace MyAndroidPhone.Desktop.Views;

/// <summary>Terminal window: colored output, a command line with history and Tab completion, and a right-click menu.</summary>
public sealed partial class TerminalView : UserControl
{
    public TerminalView(TerminalViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        ViewModel.LinesAppended += OnLinesAppended;
        Loaded += (_, _) => CommandBox.Focus(FocusState.Programmatic);
        Unloaded += (_, _) =>
        {
            ViewModel.LinesAppended -= OnLinesAppended;
            ViewModel.Dispose();
        };
    }

    public TerminalViewModel ViewModel { get; }

    private void OnLinesAppended(object? sender, EventArgs e)
    {
        if (Screen.Items.Count > 0)
        {
            Screen.ScrollIntoView(Screen.Items[^1]);
        }
    }

    private async void OnCommandKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                ViewModel.RunCommand.Execute(null);
                break;

            case VirtualKey.Up:
                e.Handled = true;
                SetInput(ViewModel.HistoryStep(-1));
                break;

            case VirtualKey.Down:
                e.Handled = true;
                SetInput(ViewModel.HistoryStep(1));
                break;

            case VirtualKey.C when ctrl && ViewModel.IsRunning && CommandBox.SelectionLength == 0:
                e.Handled = true;
                ViewModel.StopCommand.Execute(null);
                break;

            case VirtualKey.L when ctrl:
                e.Handled = true;
                ViewModel.ClearCommand.Execute(null);
                break;

            case VirtualKey.Tab:
                e.Handled = true;
                await CompleteAsync();
                break;
        }
    }

    private void SetInput(string? text)
    {
        if (text is null)
        {
            return;
        }

        ViewModel.Input = text;
        CommandBox.SelectionStart = text.Length;
    }

    private async Task CompleteAsync()
    {
        var line = ViewModel.Input;
        var completion = await ViewModel.CompleteAsync(line);
        if (completion.Options.Count == 0 || completion.ReplaceStart > line.Length)
        {
            return;
        }

        var head = line[..completion.ReplaceStart];
        if (completion.Options.Count == 1)
        {
            var option = completion.Options[0];
            SetInput(head + option + (option.EndsWith('/') || option.EndsWith("/\"") ? string.Empty : " "));
            return;
        }

        // Several matches: complete the common beginning and list the choices.
        var common = CommonPrefix(completion.Options);
        if (common.Length > line.Length - completion.ReplaceStart)
        {
            SetInput(head + common);
        }

        ViewModel.Write(TerminalLineKind.Hint, string.Join("   ", completion.Options.Take(40)) + (completion.Options.Count > 40 ? "   ..." : string.Empty));
    }

    private static string CommonPrefix(IReadOnlyList<string> options)
    {
        var prefix = options[0];
        foreach (var option in options.Skip(1))
        {
            var length = 0;
            while (length < prefix.Length && length < option.Length && char.ToLowerInvariant(prefix[length]) == char.ToLowerInvariant(option[length]))
            {
                length++;
            }

            prefix = prefix[..length];
        }

        return prefix;
    }

    private void OnScreenKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl && e.Key == VirtualKey.C)
        {
            e.Handled = true;
            CopyLines(Screen.SelectedItems.OfType<TerminalLineItem>());
        }
        else if (ctrl && e.Key == VirtualKey.A)
        {
            e.Handled = true;
            Screen.SelectAll();
        }
    }

    // ----- right-click menu -----

    private void OnCopySelection(object sender, RoutedEventArgs e) => CopyLines(Screen.SelectedItems.OfType<TerminalLineItem>());

    private void OnCopyAll(object sender, RoutedEventArgs e) => CopyLines(ViewModel.Lines);

    private void OnSelectAll(object sender, RoutedEventArgs e) => Screen.SelectAll();

    private void OnClear(object sender, RoutedEventArgs e) => ViewModel.ClearCommand.Execute(null);

    private void OnStop(object sender, RoutedEventArgs e) => ViewModel.StopCommand.Execute(null);

    private void OnShowLogs(object sender, RoutedEventArgs e) => ViewModel.RunText("show logs");

    private void OnOpenSessionLog(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SessionLogName.Length > 0)
        {
            ViewModel.RunText("open /.system/logs/" + ViewModel.SessionLogName);
        }
    }

    private async void OnPaste(object sender, RoutedEventArgs e)
    {
        var content = Clipboard.GetContent();
        if (content.Contains(StandardDataFormats.Text))
        {
            var text = (await content.GetTextAsync()).Replace("\r", " ").Replace("\n", " ");
            ViewModel.Input += text;
            CommandBox.Focus(FocusState.Programmatic);
            CommandBox.SelectionStart = ViewModel.Input.Length;
        }
    }

    private static void CopyLines(IEnumerable<TerminalLineItem> lines)
    {
        var text = string.Join(Environment.NewLine, lines.Select(l => l.PlainText));
        if (text.Length == 0)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}
