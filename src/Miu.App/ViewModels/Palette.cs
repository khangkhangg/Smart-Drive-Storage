using Avalonia.Media;
using Miu.Core.Rules;
using Miu.Core.Scanning;
using Miu.Dev;

namespace Miu.App.ViewModels;

/// <summary>Fixed colors for categories, verdicts and idle buckets (text pairs meet 4.5:1 contrast).</summary>
public static class Palette
{
    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex)).ToImmutable();

    public static readonly IBrush White = B("#FFFFFF");
    public static readonly IBrush Ink = B("#1B1B1B");
    public static readonly IBrush MostlySafeTile = B("#A7D3A9");

    public static (IBrush Bg, IBrush Fg, string Text) ForVerdict(Verdict v) => v switch
    {
        Verdict.Safe => (B("#E6F2E6"), B("#0E700E"), "Safe to delete"),
        Verdict.Review => (B("#FDF0E1"), B("#8A4600"), "Review first"),
        Verdict.Keep => (B("#FBE9E7"), B("#A4262C"), "Keep"),
        _ => (B("#FBE9E7"), B("#A4262C"), "Do not delete"),
    };

    public static (IBrush Bg, IBrush Fg, string Text) ForBucket(IdleBucket b) => b switch
    {
        IdleBucket.Active => (B("#EEF4FB"), B("#004A90"), "Active"),
        IdleBucket.Cooling => (B("#F0F0F0"), B("#3D3D3D"), "Cooling"),
        IdleBucket.Idle => (B("#FDF0E1"), B("#8A4600"), "Idle"),
        _ => (B("#FBE9E7"), B("#A4262C"), "Abandoned"),
    };

    /// <summary>Treemap tile fill and readable text color.</summary>
    public static (IBrush Fill, IBrush Text) ForTile(FileCategory c, bool isDir, Verdict v)
    {
        if (v == Verdict.Safe) return (B("#5FAF63"), Ink);
        if (isDir) return (B("#7FA7D6"), Ink);
        return c switch
        {
            FileCategory.Video => (B("#0F4C81"), White),
            FileCategory.Image => (B("#2E6FAD"), White),
            FileCategory.Audio => (B("#6C5CA8"), White),
            FileCategory.Archive => (B("#B08D57"), Ink),
            FileCategory.Installer => (B("#D9822B"), Ink),
            FileCategory.Document => (B("#A9C7EA"), Ink),
            FileCategory.Code => (B("#3A7BC8"), White),
            FileCategory.Game => (B("#E0A458"), Ink),
            FileCategory.AppDataCache => (B("#F2C38B"), Ink),
            FileCategory.System => (B("#8A8A8A"), Ink),
            _ => (B("#C9CED6"), Ink),
        };
    }
}
