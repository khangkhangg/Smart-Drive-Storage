using Avalonia.Media;
using Miu.App.Services;
using Miu.Core.Formatting;
using Miu.Core.Rules;
using Miu.Core.Scanning;
using Rules = Miu.Core.Rules;

namespace Miu.App.ViewModels;

/// <summary>One file or folder row in Analyze, the treemap tile and the details pane.</summary>
public sealed class NodeItemViewModel
{
    private readonly ScanSession _session;
    private VerdictResult? _verdict;

    public NodeItemViewModel(ScanSession session, int id, long parentSize)
    {
        _session = session;
        var t = session.Tree;
        Id = id;
        Name = id == t.Root ? t.RootPath : t.Name(id);
        IsDir = t.IsDir(id);
        Size = t.Size(id);
        Fraction = parentSize > 0 ? (double)Size / parentSize : 0;
        FullPath = t.GetFullPath(id);
        Category = Categorizer.Categorize(Name, IsDir, FullPath);
        FileCount = t.FileCount(id);
        Modified = IsDir ? t.NewestModifiedUtc(id) : t.ModifiedUtc(id);
        SafeInsideBytes = session.SafeBytesUnder(id);
    }

    public long SafeInsideBytes { get; }
    private bool HasRule => Verdict.RuleId is not null;

    /// <summary>Rows show the rule's verdict, or — for folders no rule covers — how much inside is safe to clean.</summary>
    public bool ShowsBadge => HasRule || SafeInsideBytes > 0;
    public string BadgeText => HasRule ? VerdictText : $"{SizeFormatter.Format(SafeInsideBytes)} to clean";
    public IBrush BadgeBg => HasRule ? VerdictBg : Palette.ForVerdict(Rules.Verdict.Safe).Bg;
    public IBrush BadgeFg => HasRule ? VerdictFg : Palette.ForVerdict(Rules.Verdict.Safe).Fg;

    public int Id { get; }
    public string Name { get; }
    public bool IsDir { get; }
    public long Size { get; }
    public double Fraction { get; }
    public string FullPath { get; }
    public FileCategory Category { get; }
    public int FileCount { get; }
    public DateTime Modified { get; }

    public string SizeText => SizeFormatter.Format(Size);
    public string PercentText => Fraction >= 0.005 ? $"{Fraction * 100:0}%" : "<1%";
    public string FilesText => IsDir ? $"{FileCount:N0} {(FileCount == 1 ? "file" : "files")}" : Category.ToString();
    public string ModifiedText => "changed " + HumanTime.Ago(Modified, _session.NowUtc);
    public string KindText => IsDir ? "Folder" : Category switch
    {
        FileCategory.AppDataCache => "Cache file",
        FileCategory.Other => "File",
        var c => c + " file",
    };
    public string Glyph => IsDir ? "M3,6 L10,6 L12,8 L21,8 L21,19 L3,19 Z" : "M6,3 L14,3 L19,8 L19,21 L6,21 Z M14,3 L14,8 L19,8";

    public VerdictResult Verdict => _verdict ??= _session.VerdictFor(Id);
    public string VerdictText => Palette.ForVerdict(Verdict.Verdict).Text;
    public IBrush VerdictBg => Palette.ForVerdict(Verdict.Verdict).Bg;
    public IBrush VerdictFg => Palette.ForVerdict(Verdict.Verdict).Fg;
    public string Reason => !HasRule && SafeInsideBytes > 0
        ? $"No rule covers this folder itself, but {SizeFormatter.Format(SafeInsideBytes)} inside it is safe to remove. Open it to see what."
        : Verdict.Reason;
    public string RuleText => Verdict.RuleId is null ? "No matching rule" : $"Rule: {Verdict.RuleId} · confidence {Verdict.Confidence * 100:0}%";
    public string? RebuildHint => Verdict.RebuildHint;
    public bool HasRebuildHint => !string.IsNullOrEmpty(Verdict.RebuildHint);
    public string RebuildText => $"Rebuild with: {RebuildHint}";

    public IBrush TileFill => !HasRule && Size > 0 && SafeInsideBytes * 4 > Size
        ? Palette.MostlySafeTile
        : Palette.ForTile(Category, IsDir, Verdict.Verdict).Fill;
    public IBrush TileText => Palette.ForTile(Category, IsDir, Verdict.Verdict).Text;
}
