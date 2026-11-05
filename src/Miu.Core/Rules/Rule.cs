namespace Miu.Core.Rules;

public enum Verdict { Safe, Review, Keep, Protected }

public enum RuleSource { Protected, User, Learned, Builtin }

/// <summary>What a rule applies to. All non-null parts must match.</summary>
/// <param name="Under">An exact folder: matches that folder itself and everything inside it.</param>
/// <param name="Inside">An exact folder: matches only what is inside it, never the folder itself.</param>
/// <param name="ChildOf">An exact folder: matches only its direct children.</param>
public sealed record RuleMatch(string? Path = null, string[]? Names = null, string[]? Ext = null, bool? IsDir = null,
    string? Under = null, string? Inside = null, string? ChildOf = null)
{
    /// <summary>True when the match names no location, name, type or pattern at all (it would match everything).</summary>
    public bool IsEmpty => Path is null && Names is not { Length: > 0 } && Ext is not { Length: > 0 } && Under is null && Inside is null && ChildOf is null;
}

/// <summary>Extra conditions. All non-null parts must hold.</summary>
public sealed record RuleConditions(
    int? OlderThanDays = null,
    long? MinSizeMB = null,
    int? ProjectIdleDays = null,
    string[]? RequiresSiblingMarker = null,
    string[]? RequiresChildMarker = null,
    bool? RequiresGitIgnored = null);

public sealed record Rule(
    string Id,
    RuleMatch Match,
    RuleConditions Conditions,
    Verdict Verdict,
    string Reason,
    RuleSource Source = RuleSource.Builtin,
    int Priority = 500,
    bool Enabled = true,
    string? RebuildHint = null);

/// <summary>Everything the engine needs to know about one file or folder.</summary>
/// <param name="ModifiedUtc">For folders, pass the newest modification time inside it.</param>
/// <param name="SiblingExists">Glob test against names next to the item (in its parent folder).</param>
/// <param name="ChildExists">Glob test against names directly inside the item (folders only).</param>
/// <param name="IsGitIgnored">Whether the project's .gitignore lists the item (evidence it is generated output).</param>
/// <param name="IsLink">True when the item is a symlink/junction; only then does the guard resolve its target.</param>
public sealed record ItemFacts(
    string FullPath,
    string Name,
    bool IsDir,
    long Size,
    DateTime ModifiedUtc,
    DateTime NowUtc,
    int? ProjectIdleDays,
    Func<string, bool> SiblingExists,
    Func<string, bool>? ChildExists = null,
    Func<bool>? IsGitIgnored = null,
    bool IsLink = false);

public sealed record VerdictResult(Verdict Verdict, string? RuleId, string Reason, double Confidence, string? RebuildHint);
