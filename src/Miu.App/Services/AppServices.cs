using Miu.Core.Rules;
using Miu.Data;
using Miu.Platform.Abstractions;
using Miu.Platform.Portable;

namespace Miu.App.Services;

/// <summary>Composition root: everything the UI needs, created once.</summary>
public sealed class AppServices : IDisposable
{
    private AppServices(MiuDatabase db, IScanner scanner, IReadOnlyList<Rule> rules, ProtectedPathGuard guard)
    {
        Database = db;
        Settings = new SettingsRepository(db);
        Scanner = scanner;
        Rules = rules;
        Guard = guard;
    }

    public MiuDatabase Database { get; }
    public SettingsRepository Settings { get; }
    public IScanner Scanner { get; }
    public IReadOnlyList<Rule> Rules { get; }
    public ProtectedPathGuard Guard { get; }
    public bool CaseInsensitivePaths => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static AppServices Create(string databasePath)
    {
        var db = new MiuDatabase(databasePath);
        db.Migrate();
        var rules = RuleLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "rules", "builtin"));
        return new AppServices(db, new ManagedScanner(), rules, ProtectedPathGuard.ForCurrentOs());
    }

    public void Dispose() => Database.Dispose();
}
