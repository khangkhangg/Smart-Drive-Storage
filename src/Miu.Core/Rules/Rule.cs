namespace Miu.Core.Rules;

public enum Verdict { Safe, Review, Keep, Protected }

public enum RuleSource { Protected, User, Learned, Builtin }

/// <summary>What a rule applies to. All non-null parts must match.</summary>
public sealed record RuleMatch(string? Path = null, string[]? Names = null, string[]? Ext = null, bool? IsDir = null);

/// <summary>Extra conditions. All non-null parts must hold.</summary>
public sealed record RuleConditions(
    int? OlderThanDays = null,
    long? MinSizeMB = null,
    int? ProjectIdleDays = null,
    string[]? RequiresSiblingMarker = null,
    string[]? RequiresChildMarker = null);

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
public sealed record ItemFacts(
    string FullPath,
    string Name,
    bool IsDir,
    long Size,
    DateTime ModifiedUtc,
    DateTime NowUtc,
    int? ProjectIdleDays,
    Func<string, bool> SiblingExists,
    Func<string, bool>? ChildExists = null);

public sealed record VerdictResult(Verdict Verdict, string? RuleId, string Reason, double Confidence, string? RebuildHint);
