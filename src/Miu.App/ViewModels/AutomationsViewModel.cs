using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miu.Core.Automation;
using Miu.Core.Formatting;
using Miu.Data;

namespace Miu.App.ViewModels;

public static class WatchText
{
    public static string Mode(WatchMode m) => m switch
    {
        WatchMode.Suggest => "suggest what to clean",
        WatchMode.AutoCleanSafe => "clean Safe items automatically",
        _ => "just tell me",
    };

    public static string Filters(WatchFilters f) =>
        (f.OlderThanDays is > 0 ? $" older than {f.OlderThanDays} days" : "") + (f.MinSizeMB is > 0 ? $" over {f.MinSizeMB} MB" : "");

    public static string Sentence(Watch w) => $"{w.Schedule.Describe()}, in {w.Path}, {Mode(w.Mode)}{Filters(w.Filters)}";
}

public partial class WatchRow : ObservableObject
{
    private readonly AutomationsViewModel _owner;

    public WatchRow(AutomationsViewModel owner, Watch watch)
    {
        _owner = owner;
        Watch = watch;
        enabled = watch.Enabled;
    }

    public Watch Watch { get; }
    public string Name => Watch.Name;
    public string Sentence => WatchText.Sentence(Watch);
    public string NextRunText => Watch.Schedule.Kind switch
    {
        ScheduleKind.LowSpace => "Checks every 30 minutes",
        ScheduleKind.AtSignIn => "Runs when you sign in",
        _ => Watch.NextRunLocal is { } n ? "Next run " + n.ToString("ddd d MMM, HH:mm", CultureInfo.CurrentCulture) : "Not scheduled yet",
    };
    public string LastResultText => Watch.LastResult is null ? "Hasn't run yet"
        : $"Last run {HumanTime.Ago(Watch.LastRunUtc ?? DateTime.MinValue, DateTime.UtcNow)}: {Watch.LastResult}";

    [ObservableProperty] private bool enabled;

    partial void OnEnabledChanged(bool value) => _owner.SetEnabled(Watch, value);

    [RelayCommand] private Task RunNow() => _owner.RunNowAsync(Watch);
    [RelayCommand] private void Delete() => _owner.Delete(Watch);
    [RelayCommand] private void Edit() => _owner.Editor.Load(Watch);
}

/// <summary>Watched folders: templates, a sentence-style editor, and the list of automations.</summary>
public partial class AutomationsViewModel : ObservableObject
{
    private readonly MainWindowViewModel _shell;

    public AutomationsViewModel(MainWindowViewModel shell)
    {
        _shell = shell;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Templates = WatchTemplates.All(home, Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
        Editor = new WatchEditorViewModel(this, shell);
        Load();
    }

    public IReadOnlyList<Watch> Templates { get; }
    public ObservableCollection<WatchRow> Rows { get; } = [];
    public WatchEditorViewModel Editor { get; }

    [ObservableProperty] private bool isEmpty = true;
    [ObservableProperty] private string message = "";

    public void Load()
    {
        Rows.Clear();
        foreach (var w in _shell.Services.Watches.All()) Rows.Add(new WatchRow(this, w));
        IsEmpty = Rows.Count == 0;
    }

    [RelayCommand]
    private void UseTemplate(Watch? template)
    {
        if (template is null) return;
        Save(template with { Id = 0 });
        Message = $"Added “{template.Name}”. You can change it below.";
    }

    public void Save(Watch w)
    {
        var withNext = w with { NextRunLocal = Scheduler.NextRun(w.Schedule, DateTime.Now) };
        if (w.Id > 0) _shell.Services.Watches.Update(withNext);
        else _shell.Services.Watches.Add(withNext);
        Load();
    }

    public void SetEnabled(Watch w, bool enabled) => _shell.Services.Watches.Update(w with { Enabled = enabled });

    public void Delete(Watch w)
    {
        _shell.Services.Watches.Delete(w.Id);
        Load();
    }

    public async Task RunNowAsync(Watch w)
    {
        Message = $"Running “{w.Name}”…";
        var done = await _shell.Automation.RunAsync(w);
        Message = done.LastResult ?? "";
        Load();
    }
}

/// <summary>"Every [week] on [Sunday] at [10:00], in [folder], [suggest what to clean] older than [30] days."</summary>
public partial class WatchEditorViewModel : ObservableObject
{
    private readonly AutomationsViewModel _owner;
    private readonly MainWindowViewModel _shell;
    private long _editingId;

    public WatchEditorViewModel(AutomationsViewModel owner, MainWindowViewModel shell)
    {
        _owner = owner;
        _shell = shell;
        path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    public IReadOnlyList<string> Frequencies { get; } = ["Every day", "Every week", "Every month", "When I sign in", "When space is low"];
    public IReadOnlyList<string> Actions { get; } = ["Suggest what to clean", "Clean Safe items automatically", "Just tell me"];
    public IReadOnlyList<DayOfWeek> Days { get; } = Enum.GetValues<DayOfWeek>();
    public IReadOnlyList<int> Hours { get; } = Enumerable.Range(0, 24).ToList();

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Preview))] private string name = "My automation";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Preview))] private string path;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Preview), nameof(ShowsDay), nameof(ShowsDayOfMonth), nameof(ShowsTime), nameof(ShowsLowSpace))]
    private string frequency = "Every week";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Preview))] private DayOfWeek day = DayOfWeek.Sunday;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Preview))] private int dayOfMonth = 1;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Preview))] private int hour = 10;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Preview))] private int lowSpacePercent = 10;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Preview), nameof(AutoCleanNote))] private string action = "Suggest what to clean";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Preview))] private int olderThanDays = 30;

    public bool ShowsDay => Frequency == "Every week";
    public bool ShowsDayOfMonth => Frequency == "Every month";
    public bool ShowsTime => Frequency is "Every day" or "Every week" or "Every month";
    public bool ShowsLowSpace => Frequency == "When space is low";

    public string AutoCleanNote => Action == "Clean Safe items automatically" && !_shell.Services.Settings.GetBool(SettingKeys.AutoCleanAllowed, false)
        ? "Automatic cleaning is off. Turn it on in Settings; until then Miu only suggests."
        : "";

    public void RefreshNote() => OnPropertyChanged(nameof(AutoCleanNote));

    private WatchSchedule Schedule() => Frequency switch
    {
        "Every day" => WatchSchedule.Daily(new TimeOnly(Hour, 0)),
        "Every month" => WatchSchedule.Monthly(DayOfMonth, new TimeOnly(Hour, 0)),
        "When I sign in" => WatchSchedule.AtSignIn(),
        "When space is low" => WatchSchedule.LowSpace(LowSpacePercent),
        _ => WatchSchedule.Weekly(Day, new TimeOnly(Hour, 0)),
    };

    private WatchMode Mode() => Action switch
    {
        "Clean Safe items automatically" => WatchMode.AutoCleanSafe,
        "Just tell me" => WatchMode.NotifyOnly,
        _ => WatchMode.Suggest,
    };

    public string Preview
    {
        get
        {
            var when = Schedule().Describe();
            when = char.ToUpperInvariant(when[0]) + when[1..];
            var action = char.ToLowerInvariant(Action[0]) + Action[1..];
            return $"{when}, in {Path}, {action}{(OlderThanDays > 0 ? $" older than {OlderThanDays} days" : "")}.";
        }
    }

    public void Load(Watch w)
    {
        _editingId = w.Id;
        Name = w.Name;
        Path = w.Path;
        Frequency = w.Schedule.Kind switch
        {
            ScheduleKind.Daily => "Every day", ScheduleKind.Monthly => "Every month", ScheduleKind.AtSignIn => "When I sign in",
            ScheduleKind.LowSpace => "When space is low", _ => "Every week",
        };
        Day = w.Schedule.Day;
        DayOfMonth = w.Schedule.DayOfMonth;
        Hour = w.Schedule.Time.Hour;
        LowSpacePercent = w.Schedule.LowSpacePercent;
        Action = w.Mode switch { WatchMode.AutoCleanSafe => "Clean Safe items automatically", WatchMode.NotifyOnly => "Just tell me", _ => "Suggest what to clean" };
        OlderThanDays = w.Filters.OlderThanDays ?? 0;
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Path)) return;
        _owner.Save(new Watch(_editingId, string.IsNullOrWhiteSpace(Name) ? "My automation" : Name.Trim(), Path.Trim(), Mode(), Schedule(),
            new WatchFilters(OlderThanDays: OlderThanDays > 0 ? OlderThanDays : null)));
        _owner.Message = $"Saved “{Name}”.";
        _editingId = 0;
    }
}
