using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyAndroidPhone.Core.Transfers;
using MyAndroidPhone.Desktop.Helpers;
using MyAndroidPhone.Desktop.Services;

namespace MyAndroidPhone.Desktop.ViewModels;

public sealed class TransferItemViewModel
{
    public TransferItemViewModel(TransferSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    public TransferSnapshot Snapshot { get; }

    public Guid Id => Snapshot.Id;

    public string Title => Snapshot.Title;

    public string DirectionGlyph => Snapshot.Request.Direction == TransferDirection.Upload ? "" : "";

    public string StateText => Snapshot.State switch
    {
        TransferState.Queued => "Waiting",
        TransferState.Running => "Transferring",
        TransferState.Completed => "Completed",
        TransferState.CompletedWithErrors => "Completed with errors",
        TransferState.Failed => "Failed",
        _ => "Cancelled"
    };

    public bool IsRunning => Snapshot.State == TransferState.Running;

    public bool IsQueued => Snapshot.State == TransferState.Queued;

    public bool CanCancel => Snapshot.State is TransferState.Queued or TransferState.Running;

    public bool CanRetry => Snapshot.IsFinished && Snapshot.State != TransferState.Completed;

    public bool CanOpenFolder => Snapshot.Request.Direction == TransferDirection.Download && Snapshot.IsFinished && Snapshot.FilesDone > 0;

    public bool HasFailures => Snapshot.Failures.Count > 0;

    /// <summary>The bar is determinate only when the total byte count is known and non-zero.</summary>
    public bool IsIndeterminate => IsQueued || (IsRunning && (Snapshot.BytesTotal is null or 0));

    public double Percent => Snapshot.BytesTotal is > 0 ? Math.Min(100, Snapshot.BytesDone * 100.0 / Snapshot.BytesTotal.Value) : Snapshot.IsFinished ? 100 : 0;

    public string Destination => Snapshot.Request.Direction == TransferDirection.Download
        ? "To " + Snapshot.Request.DestinationDirectory
        : "To " + Snapshot.Request.DestinationDirectory + " on " + Snapshot.Request.DeviceName;

    public string Detail
    {
        get
        {
            var s = Snapshot;
            var parts = new List<string>();
            if (s.IsFinished)
            {
                parts.Add(s.Message ?? string.Empty);
                parts.Add(Formatting.Bytes(s.BytesDone));
                parts.Add(Formatting.Duration(s.Elapsed));
            }
            else if (s.State == TransferState.Running)
            {
                parts.Add(s.BytesTotal is null ? $"{s.FilesDone} file(s) done" : $"{Formatting.Bytes(s.BytesDone)} of {Formatting.Bytes(s.BytesTotal)}");
                if (s.FilesTotal is { } total)
                {
                    parts.Add($"{s.FilesDone + s.FilesSkipped + s.FilesFailed} of {total} files");
                }

                var speed = Formatting.Speed(s.BytesPerSecond);
                if (speed.Length > 0)
                {
                    parts.Add(speed);
                }

                parts.Add(Formatting.Duration(s.Elapsed));
                if (!string.IsNullOrEmpty(s.CurrentItem))
                {
                    parts.Add(s.CurrentItem);
                }
            }
            else
            {
                parts.Add(s.Message ?? string.Empty);
            }

            return string.Join("  -  ", parts.Where(p => p.Length > 0));
        }
    }

    public string FailureText => string.Join(Environment.NewLine, Snapshot.Failures.Take(20).Select(f => $"{Path.GetFileName(f.Path.TrimEnd('/', '\\'))}: {f.Message}"))
        + (Snapshot.Failures.Count > 20 ? Environment.NewLine + $"... and {Snapshot.Failures.Count - 20} more" : string.Empty);
}

/// <summary>Shows every transfer: queued, running and finished, with real byte counts and per-file failure reasons.</summary>
public sealed partial class TransferCenterViewModel : ObservableObject, IDisposable
{
    private readonly ITransferService _transfers;
    private readonly IUiDispatcher _ui;
    private readonly IFolderLauncher _folders;
    private readonly INotificationService _notifications;

    public TransferCenterViewModel(ITransferService transfers, IUiDispatcher ui, IFolderLauncher folders, INotificationService notifications)
    {
        _transfers = transfers;
        _ui = ui;
        _folders = folders;
        _notifications = notifications;
        _transfers.Changed += OnChanged;
        Rebuild();
    }

    public ObservableCollection<TransferItemViewModel> Items { get; } = new();

    [ObservableProperty]
    private string _summary = string.Empty;

    public bool IsEmpty => Items.Count == 0;

    private void OnChanged(object? sender, EventArgs e) => _ui.Post(Rebuild);

    private void Rebuild()
    {
        var snapshots = _transfers.Transfers;
        Items.Clear();
        foreach (var snapshot in snapshots)
        {
            Items.Add(new TransferItemViewModel(snapshot));
        }

        var active = snapshots.Count(s => !s.IsFinished);
        Summary = snapshots.Count == 0 ? "No transfers yet." : active > 0 ? $"{active} active or waiting, {snapshots.Count - active} finished." : $"{snapshots.Count} finished.";
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void Cancel(TransferItemViewModel? item)
    {
        if (item is not null)
        {
            _transfers.Cancel(item.Id);
        }
    }

    [RelayCommand]
    private void Retry(TransferItemViewModel? item)
    {
        if (item is not null && _transfers.Retry(item.Id) is null)
        {
            _notifications.Post(NotificationSeverity.Warning, "Cannot retry", "Only transfers that failed or were cancelled can be retried.");
        }
    }

    [RelayCommand]
    private async Task OpenFolderAsync(TransferItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (!await _folders.OpenFolderAsync(item.Snapshot.Request.DestinationDirectory))
        {
            _notifications.Post(NotificationSeverity.Warning, "Could not open the folder", item.Snapshot.Request.DestinationDirectory);
        }
    }

    [RelayCommand]
    private void ClearFinished() => _transfers.ClearFinished();

    public void Dispose() => _transfers.Changed -= OnChanged;
}
