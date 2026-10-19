using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;

namespace MultiTool.Modules.Storage.Cleanup;

/// <summary>What goes into the section's <c>settings.json</c>: which rows were ticked.</summary>
public sealed class CleanupSettings
{
    public List<string> Selected { get; set; } = [];
    public bool Customized { get; set; }
}

/// <summary>One row: a target, its size, whether it is ticked.</summary>
public sealed partial class CleanupRow(CleanupTarget target, bool selected, Action changed) : ObservableObject
{
    public CleanupTarget Target { get; } = target;

    public string Name => Target.Name;

    public string Description => Target.Description;

    [ObservableProperty]
    public partial bool Selected { get; set; } = selected;

    [ObservableProperty]
    public partial string SizeText { get; set; } = "measuring…";

    [ObservableProperty]
    public partial string? Note { get; set; }

    public long Bytes { get; set; }

    partial void OnSelectedChanged(bool value) => changed();
}

/// <summary>Temp files, caches and the recycle bin: measured on open, cleaned on request.</summary>
/// <remarks>
/// Measuring runs once per open, in the background, one row after another; the recycle bin comes last
/// because Windows walks every item to size it. A program whose cache is in use is named on its row and
/// skipped while it runs.
/// </remarks>
public sealed partial class CleanupViewModel : ObservableObject, IDisposable
{
    private readonly IModuleContext _context;
    private readonly bool _loading;
    private CancellationTokenSource? _cancel;
    private bool _measured;

    public CleanupViewModel(IModuleContext context)
    {
        _loading = true;
        _context = context;

        var settings = context.Settings.Load<CleanupSettings>();
        foreach (var target in Cleaner.Targets)
        {
            var selected = settings.Customized ? settings.Selected.Contains(target.Id) : target.Default;
            Rows.Add(new CleanupRow(target, selected, OnRowChanged));
        }

        _loading = false;
        UpdateTotal();
    }

    public event EventHandler<ModuleActivity>? ActivityChanged;

    public ObservableCollection<CleanupRow> Rows { get; } = [];

    [ObservableProperty]
    public partial string TotalText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    /// <summary>Called when the section is shown; measures once.</summary>
    public void EnsureMeasured()
    {
        if (_measured || IsBusy) return;
        _measured = true;
        _ = MeasureAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => MeasureAsync();

    private async Task MeasureAsync()
    {
        if (IsBusy) return;

        Begin("Measuring caches…");
        var cancel = _cancel!.Token;

        try
        {
            foreach (var row in Rows) row.SizeText = "measuring…";

            foreach (var row in Rows)
            {
                var result = await Task.Run(() => Cleaner.Measure(row.Target, cancel), cancel);
                var running = await Task.Run(() => Cleaner.Running(row.Target), cancel);

                row.Bytes = result.Bytes;
                row.SizeText = result.Files == 0 && result.Bytes == 0 ? "empty" : Humanize.Bytes(result.Bytes);
                row.Note = running.Count == 0 ? null : $"In use while {string.Join(", ", running)} runs; skipped until closed.";
                UpdateTotal();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            End();
        }
    }

    [RelayCommand]
    private async Task CleanAsync()
    {
        var rows = Rows.Where(r => r.Selected).ToList();
        if (rows.Count == 0 || IsBusy) return;

        var total = rows.Sum(r => r.Bytes);
        if (!await _context.Shell.ConfirmAsync("Clean up",
                $"{string.Join(", ", rows.Select(r => r.Name))} will be deleted permanently ({Humanize.Bytes(total)}).",
                "Delete", danger: true))
            return;

        Begin("Cleaning up…");
        var cancel = _cancel!.Token;
        long freed = 0;
        var skipped = 0;
        var waiting = new List<string>();

        try
        {
            foreach (var row in rows)
            {
                var running = await Task.Run(() => Cleaner.Running(row.Target), cancel);
                if (running.Count > 0)
                {
                    waiting.AddRange(running);
                    continue;
                }

                row.SizeText = "cleaning…";
                var result = await Task.Run(() => Cleaner.Clean(row.Target, cancel), cancel);
                freed += result.Bytes;
                skipped += result.Skipped;

                var left = await Task.Run(() => Cleaner.Measure(row.Target, cancel), cancel);
                row.Bytes = left.Bytes;
                row.SizeText = left.Bytes == 0 ? "empty" : Humanize.Bytes(left.Bytes);
            }

            var parts = new List<string> { $"{Humanize.Bytes(freed)} freed." };
            if (skipped > 0) parts.Add($"{skipped:N0} files in use stayed.");
            if (waiting.Count > 0) parts.Add($"Skipped while running: {string.Join(", ", waiting.Distinct())}.");
            Message = string.Join(" ", parts);

            _context.Log.Info($"Cleanup: {freed} bytes freed, {skipped} skipped.");
        }
        catch (OperationCanceledException)
        {
            Message = $"Stopped. {Humanize.Bytes(freed)} freed until then.";
        }
        catch (Exception ex)
        {
            _context.Log.Error("Cleanup failed.", ex);
            Message = $"Cleanup failed: {ex.Message}";
        }
        finally
        {
            End();
            UpdateTotal();
        }
    }

    [RelayCommand]
    private void Cancel() => _cancel?.Cancel();

    private void Begin(string activity)
    {
        _cancel?.Dispose();
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(_context.Shutdown);
        IsBusy = true;
        Message = null;
        ActivityChanged?.Invoke(this, new ModuleActivity(true, activity));
    }

    private void End()
    {
        IsBusy = false;
        ActivityChanged?.Invoke(this, ModuleActivity.Idle);
    }

    private void OnRowChanged()
    {
        if (_loading) return;

        _context.Settings.Save(new CleanupSettings
        {
            Selected = [.. Rows.Where(r => r.Selected).Select(r => r.Target.Id)],
            Customized = true,
        });

        UpdateTotal();
    }

    private void UpdateTotal()
    {
        var selected = Rows.Where(r => r.Selected).ToList();
        TotalText = selected.Count == 0 ? "Nothing selected." : $"{Humanize.Bytes(selected.Sum(r => r.Bytes))} selected";
    }

    public void Dispose()
    {
        _cancel?.Cancel();
        _cancel?.Dispose();
        _cancel = null;
    }
}
