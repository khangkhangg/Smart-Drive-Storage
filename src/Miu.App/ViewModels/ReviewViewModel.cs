using Miu.App.Services;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miu.Core.Cleanup;
using Miu.Core.Formatting;
using Miu.Core.Rules;

namespace Miu.App.ViewModels;

public partial class ReviewRow : ObservableObject
{
    private readonly Action _changed;

    public ReviewRow(CartItem item, Action changed)
    {
        Item = item;
        _changed = changed;
        isChecked = item.Verdict == Verdict.Safe;
    }

    public CartItem Item { get; }
    public string Path => Item.Path;
    public string Name => Item.Name;
    public string SizeText => SizeFormatter.Format(Item.Bytes);
    public string Reason => Item.Reason;
    public bool NeedsAttention => Item.Verdict != Verdict.Safe;
    public string VerdictText => Palette.ForVerdict(Item.Verdict).Text;
    public IBrush VerdictBg => Palette.ForVerdict(Item.Verdict).Bg;
    public IBrush VerdictFg => Palette.ForVerdict(Item.Verdict).Fg;

    [ObservableProperty] private bool isChecked;

    partial void OnIsCheckedChanged(bool value) => _changed();
}

/// <summary>The "Review &amp; Clean" sheet: last look before items move to the Recycle Bin.</summary>
public partial class ReviewViewModel : ObservableObject
{
    private readonly MainWindowViewModel _shell;
    private readonly string _origin;

    public ReviewViewModel(MainWindowViewModel shell, IReadOnlyList<CartItem> items, string title, string origin)
    {
        _shell = shell;
        _origin = origin;
        Title = title;
        Rows = items.OrderBy(i => i.Verdict != Verdict.Safe).ThenByDescending(i => i.Bytes).Select(i => new ReviewRow(i, Recalculate)).ToList();
        Recalculate();
    }

    public string Title { get; }
    public IReadOnlyList<ReviewRow> Rows { get; }
    public string BinName => _shell.Services.RecycleBinName;
    public bool CanClean => _shell.Services.CanClean;
    public string Subtitle => CanClean
        ? $"Checked items move to the {BinName}. You can undo this from the Activity page."
        : "Cleaning isn't available on this system yet (coming to macOS soon).";
    public bool HasAttentionRows => Rows.Any(r => r.NeedsAttention);

    /// <summary>The first connected cloud account, used by "Back up first".</summary>
    public Miu.Data.CloudAccount? BackupAccount => _shell.Services.CloudAccounts.All().FirstOrDefault();
    public bool CanBackUpFirst => BackupAccount is not null;
    public string BackUpFirstText => BackupAccount is { } a
        ? $"Back up to {a.DisplayName} first (Miu cleans only after every file is uploaded and checked)"
        : "Back up first: connect a cloud account in Cloud & Network to use this";

    // ---------- Progress while cleaning ----------
    private CancellationTokenSource? _stop;
    private DateTime _currentSince;
    private string _currentName = "";
    private readonly Avalonia.Threading.DispatcherTimer _slowTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>After this long on one item, say that Windows may be waiting for an answer (a "delete permanently?" dialog).</summary>
    public static TimeSpan SlowAfter { get; set; } = TimeSpan.FromSeconds(20);

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle))] private bool isWorking;
    [ObservableProperty] private double progressValue;
    [ObservableProperty] private string progressText = "";
    [ObservableProperty] private string slowText = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(StopCommand))] private bool isStopping;
    public bool IsIdle => !IsWorking;

    private bool CanStop() => IsWorking && !IsStopping;

    /// <summary>"Stop after this item": the item being moved finishes; the rest are left as they are.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        IsStopping = true;
        _stop?.Cancel();
        ProgressText = $"Stopping after {_currentName}…";
    }

    /// <summary>Shows one progress update (called on the UI thread).</summary>
    public void Show(CleanProgress p)
    {
        if (p.Current != _currentName) { _currentName = p.Current; _currentSince = DateTime.UtcNow; SlowText = ""; }
        if (p.Phase == "checking")
        {
            ProgressValue = 0;
            ProgressText = $"Checking {p.Done + 1} of {p.Total} with your Keep rules and git · {p.Current}";
            return;
        }
        ProgressValue = p.TotalBytes > 0 ? 100.0 * p.BytesDone / p.TotalBytes : 100.0 * p.Done / Math.Max(1, p.Total);
        if (IsStopping) return;
        ProgressText = p.Current.Length == 0
            ? $"Done · {SizeFormatter.Format(p.BytesDone)} moved"
            : $"Moving {p.Done + 1} of {p.Total} · {p.Current} ({SizeFormatter.Format(p.CurrentBytes)}) · {SizeFormatter.Format(p.BytesDone)} of {SizeFormatter.Format(p.TotalBytes)} done";
    }

    public void CheckSlow()
    {
        if (!IsWorking || _currentName.Length == 0 || DateTime.UtcNow - _currentSince < SlowAfter) return;
        SlowText = OperatingSystem.IsWindows()
            ? $"Still working on {_currentName}. If it's too big for the {BinName}, Windows asks whether to delete it permanently: that window may be behind Miu."
            : $"Still working on {_currentName}. Big folders can take a while.";
    }

    [ObservableProperty] private bool backUpFirst;
    [ObservableProperty] private string backupStatus = "";
    [ObservableProperty] private string buttonText = "";
    [ObservableProperty] private string totalText = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CleanCommand))] private bool isBusy;

    private bool CanRunClean() => !IsBusy && CanClean && Rows.Any(r => r.IsChecked);

    [RelayCommand(CanExecute = nameof(CanRunClean))]
    private async Task Clean()
    {
        IsBusy = true;
        try
        {
            var items = Rows.Where(r => r.IsChecked).Select(r => r.Item).ToList();
            if (BackUpFirst && BackupAccount is { } account)
            {
                var progress = new Progress<BackupProgress>(p => BackupStatus = $"Backing up {p.Done + 1} of {p.Total}: {p.Current}");
                string? problem;
                try { problem = await _shell.Automation.Backup.BackUpBeforeCleaningAsync(account, items.Select(i => i.Path).ToList(), progress, default); }
                catch (Exception ex) { problem = "Backup failed: " + ex.Message + " Nothing was cleaned."; } // any failure: clean nothing
                if (problem is not null) { BackupStatus = problem; return; }
                BackupStatus = "Backed up and checked.";
            }
            _stop = new CancellationTokenSource();
            IsStopping = false;
            IsWorking = true;
            _currentName = "";
            _slowTimer.Tick -= OnSlowTick;
            _slowTimer.Tick += OnSlowTick;
            _slowTimer.Start();
            await _shell.CleanAsync(items, _origin, new Progress<CleanProgress>(Show), _stop.Token);
        }
        finally
        {
            _slowTimer.Stop();
            IsWorking = false;
            IsBusy = false;
        }
    }

    private void OnSlowTick(object? sender, EventArgs e) => CheckSlow();

    [RelayCommand]
    private void Cancel() => _shell.CloseReview();

    [RelayCommand]
    private void CheckAll()
    {
        foreach (var r in Rows) r.IsChecked = true;
    }

    private void Recalculate()
    {
        var chosen = Rows.Where(r => r.IsChecked).ToList();
        var bytes = SizeFormatter.Format(chosen.Sum(r => r.Item.Bytes));
        TotalText = $"{chosen.Count} of {Rows.Count} selected · {bytes}";
        ButtonText = chosen.Count == 0 ? "Select items to clean" : $"Move {chosen.Count} {(chosen.Count == 1 ? "item" : "items")} ({bytes}) to {BinName}";
        CleanCommand.NotifyCanExecuteChanged();
    }
}
