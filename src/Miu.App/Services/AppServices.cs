using Miu.Core.Cleanup;
using Miu.Core.Rules;
using Miu.Data;
using Miu.Dev.Docker;
using Miu.Platform.Abstractions;
using Miu.Platform.Portable;
using Miu.Platform.Windows;

namespace Miu.App.Services;

/// <summary>Composition root: everything the UI needs, created once.</summary>
public sealed class AppServices : IDisposable
{
    private AppServices(MiuDatabase db, IScanner scanner, IReadOnlyList<Rule> rules, ProtectedPathGuard guard, IRecycleBin recycleBin)
    {
        Database = db;
        Settings = new SettingsRepository(db);
        Actions = new ActionsRepository(db);
        Decisions = new DecisionsRepository(db);
        RulesStore = new RulesRepository(db);
        Suggestions = new SuggestionsRepository(db);
        Watches = new WatchesRepository(db);
        JobRuns = new JobRunsRepository(db);
        Snapshots = new SnapshotsRepository(db);
        SpaceSamples = new SpaceSamplesRepository(db);
        Scanner = scanner;
        Rules = rules;
        Guard = guard;
        // Everything that deletes goes through the guard, at deletion time.
        Recycler = new SafeRecycler(recycleBin, guard);
        Cart = new CleanupCart(PathNormalizer.Current);
        Docker = new DockerService(new ProcessRunner(TimeSpan.FromMinutes(10)));
    }

    public DockerService Docker { get; }

    public MiuDatabase Database { get; }
    public SettingsRepository Settings { get; }
    public ActionsRepository Actions { get; }
    public DecisionsRepository Decisions { get; }
    /// <summary>Rules you made or accepted. Built-in rules are in <see cref="Rules"/>.</summary>
    public RulesRepository RulesStore { get; }
    public SuggestionsRepository Suggestions { get; }
    public WatchesRepository Watches { get; }
    public JobRunsRepository JobRuns { get; }
    public SnapshotsRepository Snapshots { get; }
    public SpaceSamplesRepository SpaceSamples { get; }

    /// <summary>Built-in rules plus your own and learned ones (the guard still comes first).</summary>
    public IReadOnlyList<Rule> AllRules() => [.. Rules, .. RulesStore.All()];
    public IScanner Scanner { get; }
    public IReadOnlyList<Rule> Rules { get; }
    public ProtectedPathGuard Guard { get; }
    public IRecycleBin Recycler { get; }
    public CleanupCart Cart { get; }
    public bool CaseInsensitivePaths => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    public bool CanClean => Recycler.IsSupported;
    public string RecycleBinName => OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash";

    public static AppServices Create(string databasePath, IRecycleBin? recycleBin = null)
    {
        var db = new MiuDatabase(databasePath);
        db.Migrate();
        var rules = RuleLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "rules", "builtin"));
        return new AppServices(db, new ManagedScanner(), rules, ProtectedPathGuard.ForCurrentOs(), recycleBin ?? DefaultRecycleBin());
    }

    private static IRecycleBin DefaultRecycleBin()
    {
        if (OperatingSystem.IsWindows()) return new WindowsRecycleBin();
        if (OperatingSystem.IsLinux()) return new FreedesktopTrash();
        return new UnsupportedRecycleBin(); // macOS arrives in Phase 9
    }

    public void Dispose() => Database.Dispose();
}
