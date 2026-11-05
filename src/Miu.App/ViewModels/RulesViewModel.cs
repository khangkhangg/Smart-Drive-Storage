using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miu.App.Services;
using Miu.Core.Rules;

namespace Miu.App.ViewModels;

public partial class SuggestionRow(RulesViewModel owner, long id, Miu.Core.Learning.RuleSuggestion s) : ObservableObject
{
    public string Title => (s.Rule.Verdict == Verdict.Keep ? "Keep " : "Safe to delete: ") + RuleDescriber.Describe(s.Rule);
    public string Evidence => s.Evidence;
    public string ConfidenceText => $"confidence {s.Confidence * 100:0}%";

    [RelayCommand] private void TurnOn() => owner.Accept(id);
    [RelayCommand] private void NotNow() => owner.Reject(id);
}

public partial class RuleRow : ObservableObject
{
    private readonly RulesViewModel _owner;

    public RuleRow(RulesViewModel owner, Rule rule)
    {
        _owner = owner;
        Rule = rule;
        enabled = rule.Enabled;
    }

    public Rule Rule { get; }
    public string Pattern => RuleDescriber.Describe(Rule);
    public string Reason => Rule.Reason;
    public string SourceText => Rule.Source switch { RuleSource.User => "Yours", RuleSource.Learned => "Learned", _ => "Built-in" };
    public string VerdictText => Palette.ForVerdict(Rule.Verdict).Text;
    public IBrush VerdictBg => Palette.ForVerdict(Rule.Verdict).Bg;
    public IBrush VerdictFg => Palette.ForVerdict(Rule.Verdict).Fg;
    public bool CanEdit => Rule.Source is RuleSource.User or RuleSource.Learned;

    [ObservableProperty] private bool enabled;

    partial void OnEnabledChanged(bool value)
    {
        if (CanEdit) _owner.SetEnabled(Rule.Id, value);
    }

    [RelayCommand] private void Delete() => _owner.Delete(Rule.Id);
}

/// <summary>The Rules page: what decides each verdict, what Miu learned, and what it suggests.</summary>
public partial class RulesViewModel(MainWindowViewModel shell) : ObservableObject
{
    public ObservableCollection<SuggestionRow> Suggestions { get; } = [];
    public ObservableCollection<RuleRow> Rows { get; } = [];
    public IReadOnlyList<string> Filters { get; } = ["All", "Learned", "Yours", "Built-in"];

    [ObservableProperty] private int builtInCount;
    [ObservableProperty] private int learnedCount;
    [ObservableProperty] private int yourCount;
    [ObservableProperty] private int pendingCount;
    [ObservableProperty] private string filter = "All";
    [ObservableProperty] private string message = "";

    public bool HasSuggestions => Suggestions.Count > 0;

    partial void OnFilterChanged(string value) => LoadRows();

    public void Load()
    {
        var mine = shell.Services.RulesStore.All();
        BuiltInCount = shell.Services.Rules.Count;
        LearnedCount = mine.Count(r => r.Source == RuleSource.Learned);
        YourCount = mine.Count(r => r.Source == RuleSource.User);
        Suggestions.Clear();
        foreach (var (id, s) in shell.Services.Suggestions.Pending()) Suggestions.Add(new SuggestionRow(this, id, s));
        PendingCount = Suggestions.Count;
        OnPropertyChanged(nameof(HasSuggestions));
        LoadRows();
        shell.Home.RefreshRuleCounts();
    }

    private void LoadRows()
    {
        Rows.Clear();
        var mine = shell.Services.RulesStore.All();
        IEnumerable<Rule> all = Filter switch
        {
            "Learned" => mine.Where(r => r.Source == RuleSource.Learned),
            "Yours" => mine.Where(r => r.Source == RuleSource.User),
            "Built-in" => shell.Services.Rules,
            _ => [.. mine, .. shell.Services.Rules],
        };
        foreach (var r in all) Rows.Add(new RuleRow(this, r));
    }

    public void Accept(long id)
    {
        shell.Learning.Accept(id, shell.Analyze.Session);
        AfterRulesChanged("Rule turned on. Miu applied it to the current scan.");
    }

    public void Reject(long id)
    {
        shell.Learning.Reject(id);
        Load();
    }

    public void SetEnabled(string id, bool enabled)
    {
        shell.Services.RulesStore.SetEnabled(id, enabled);
        shell.Services.Actions.Add("rule-changed", [new Miu.Data.ActionItem((enabled ? "Turned on: " : "Turned off: ") + Describe(id), id, 0)], "rules");
        AfterRulesChanged(enabled ? "Rule turned on." : "Rule turned off.", reload: false);
    }

    public void Delete(string id)
    {
        var what = Describe(id);
        shell.Services.RulesStore.Delete(id);
        shell.Services.Actions.Add("rule-changed", [new Miu.Data.ActionItem("Deleted: " + what, id, 0)], "rules");
        AfterRulesChanged("Rule deleted.");
    }

    private string Describe(string id) => shell.Services.RulesStore.All().FirstOrDefault(r => r.Id == id) is { } r ? r.Reason : id;

    public string ExportJson() => shell.Learning.ExportJson();

    public void Import(string json)
    {
        var r = shell.Learning.ImportJson(json);
        AfterRulesChanged(r.Error ?? $"Imported {r.Imported} {(r.Imported == 1 ? "rule" : "rules")}." +
            (r.Skipped > 0 ? $" Skipped {r.Skipped} that were incomplete or protected." : "") +
            " Imported Safe rules are off until you turn them on.");
    }

    private void AfterRulesChanged(string message, bool reload = true)
    {
        shell.ApplyRuleChanges();
        if (reload) Load();
        else shell.Home.RefreshRuleCounts();
        Message = message;
    }
}
