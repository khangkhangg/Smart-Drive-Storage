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
    private AppServices(MiuDatabase db, IScanner scanner, IReadOnlyList<Rule> rules, ProtectedPathGuard guard, IRecycleBin recycleBin,
        IStartupRegistration startup, IPowerState power, ISecretStore secrets, INetworkDrives networkDrives)
    {
        NetworkDrives = networkDrives;
        CloudAccounts = new CloudAccountsRepository(db);
        BackupPlans = new BackupPlansRepository(db);
        BackupManifest = new BackupManifestRepository(db);
        NetworkShares = new NetworkSharesRepository(db);
        SyncJobs = new SyncJobsRepository(db);
        SyncBaseline = new SyncBaselineRepository(db);
        Secrets = secrets;
        Telegram = new TelegramRepository(db);
        RemoteActions = new RemoteActionsRepository(db);
        Startup = startup;
        Power = power;
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
    public IStartupRegistration Startup { get; }
    public IPowerState Power { get; }
    public ISecretStore Secrets { get; }
    public INetworkDrives NetworkDrives { get; }

    /// <summary>A git helper with its own small cache (make one per operation so answers are fresh).</summary>
    public Miu.Dev.Git.GitService NewGit() => new(new ProcessRunner(TimeSpan.FromSeconds(20)));
    public CloudAccountsRepository CloudAccounts { get; }
    public BackupPlansRepository BackupPlans { get; }
    public BackupManifestRepository BackupManifest { get; }

    /// <summary>Tests and demos: use this cloud instead of real providers.</summary>
    public Func<CloudAccount, Miu.Cloud.ICloudStorage>? CloudStorageOverride { get; set; }
    public NetworkSharesRepository NetworkShares { get; }
    public SyncJobsRepository SyncJobs { get; }
    public SyncBaselineRepository SyncBaseline { get; }

    /// <summary>Network folders get a gentler scanner (4 threads) so the NAS and the Wi-Fi aren't flooded.</summary>
    public IScanner ScannerFor(string path) => NetworkService.IsNetworkPath(path) ? new ManagedScanner(4) : Scanner;
    public TelegramRepository Telegram { get; }
    public RemoteActionsRepository RemoteActions { get; }

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
    /// <summary>The "What is this?" knowledge (rules/explain/*.json).</summary>
    public Miu.Core.Explain.Explainer Explainer { get; } =
        Miu.Core.Explain.Explainer.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "rules", "explain"), caseInsensitive: true);
    public ProtectedPathGuard Guard { get; }
    public IRecycleBin Recycler { get; }
    public CleanupCart Cart { get; }
    public bool CaseInsensitivePaths => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    public bool CanClean => Recycler.IsSupported;
    public string RecycleBinName => OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash";

    /// <summary>The database file (null for an in-memory database).</summary>
    public string? DatabasePath { get; private set; }

    /// <summary>Set when the database was damaged at start and restored from a backup.</summary>
    public string? RecoveredFrom { get; private set; }

    /// <summary>Copies the database once a day (newest 3 kept).</summary>
    public void BackupDatabaseIfDue()
    {
        if (DatabasePath is { } p) DatabaseCare.BackupIfDue(Database, p, DateTime.Now);
    }

    public static AppServices Create(string databasePath, IRecycleBin? recycleBin = null, IStartupRegistration? startup = null, IPowerState? power = null,
        ISecretStore? secrets = null, INetworkDrives? networkDrives = null)
    {
        var (db, recovered) = databasePath == ":memory:" ? (new MiuDatabase(databasePath), null) : DatabaseCare.OpenWithRecovery(databasePath);
        db.Migrate();
        var rules = RuleLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "rules", "builtin"));
        return new AppServices(db, new ManagedScanner(), rules, ProtectedPathGuard.ForCurrentOs(), recycleBin ?? DefaultRecycleBin(),
            startup ?? DefaultStartup(), power ?? DefaultPower(), secrets ?? DefaultSecrets(), networkDrives ?? DefaultNetworkDrives())
        {
            DatabasePath = databasePath == ":memory:" ? null : databasePath,
            RecoveredFrom = recovered,
        };
    }

    private static ISecretStore DefaultSecrets()
    {
        if (OperatingSystem.IsWindows()) return new WindowsCredentialStore();
        if (OperatingSystem.IsMacOS()) return new Miu.Platform.MacOS.MacKeychain();
        var file = new FileSecretStore(Path.GetDirectoryName(MiuDatabase.DefaultPath)!);
        return OperatingSystem.IsLinux() ? new LinuxKeyring(file) : file;
    }

    private static INetworkDrives DefaultNetworkDrives() =>
        OperatingSystem.IsWindows()
            ? new WindowsNetworkDrives()
            : new UnsupportedNetworkDrives("On this system, connect shares with your file manager (for example smb://nas/photos). Miu can then scan and sync them like any folder.");

    private static IStartupRegistration DefaultStartup()
    {
        if (OperatingSystem.IsWindows()) return new WindowsRunKey();
        if (OperatingSystem.IsLinux()) return new LinuxAutostart();
        if (OperatingSystem.IsMacOS()) return new Miu.Platform.MacOS.MacLaunchAgent();
        return new NoStartupRegistration();
    }

    private static IPowerState DefaultPower()
    {
        if (OperatingSystem.IsWindows()) return new WindowsPowerState();
        if (OperatingSystem.IsLinux()) return new LinuxPowerState();
        if (OperatingSystem.IsMacOS()) return new Miu.Platform.MacOS.MacPowerState();
        return new AlwaysPlugged();
    }

    private static IRecycleBin DefaultRecycleBin()
    {
        if (OperatingSystem.IsWindows()) return new WindowsRecycleBin();
        if (OperatingSystem.IsLinux()) return new FreedesktopTrash();
        if (OperatingSystem.IsMacOS()) return new Miu.Platform.MacOS.MacTrash();
        return new UnsupportedRecycleBin();
    }

    public void Dispose() => Database.Dispose();
}
