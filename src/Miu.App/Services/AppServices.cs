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
    public ProtectedPathGuard Guard { get; }
    public IRecycleBin Recycler { get; }
    public CleanupCart Cart { get; }
    public bool CaseInsensitivePaths => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    public bool CanClean => Recycler.IsSupported;
    public string RecycleBinName => OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash";

    public static AppServices Create(string databasePath, IRecycleBin? recycleBin = null, IStartupRegistration? startup = null, IPowerState? power = null,
        ISecretStore? secrets = null, INetworkDrives? networkDrives = null)
    {
        var db = new MiuDatabase(databasePath);
        db.Migrate();
        var rules = RuleLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "rules", "builtin"));
        return new AppServices(db, new ManagedScanner(), rules, ProtectedPathGuard.ForCurrentOs(), recycleBin ?? DefaultRecycleBin(),
            startup ?? DefaultStartup(), power ?? DefaultPower(), secrets ?? DefaultSecrets(), networkDrives ?? DefaultNetworkDrives());
    }

    private static ISecretStore DefaultSecrets() =>
        OperatingSystem.IsWindows()
            ? new WindowsCredentialStore()
            : new FileSecretStore(Path.GetDirectoryName(MiuDatabase.DefaultPath)!);

    private static INetworkDrives DefaultNetworkDrives() =>
        OperatingSystem.IsWindows()
            ? new WindowsNetworkDrives()
            : new UnsupportedNetworkDrives("On this system, connect shares with your file manager (for example smb://nas/photos). Miu can then scan and sync them like any folder.");

    private static IStartupRegistration DefaultStartup()
    {
        if (OperatingSystem.IsWindows()) return new WindowsRunKey();
        if (OperatingSystem.IsLinux()) return new LinuxAutostart();
        return new NoStartupRegistration();
    }

    private static IPowerState DefaultPower()
    {
        if (OperatingSystem.IsWindows()) return new WindowsPowerState();
        if (OperatingSystem.IsLinux()) return new LinuxPowerState();
        return new AlwaysPlugged();
    }

    private static IRecycleBin DefaultRecycleBin()
    {
        if (OperatingSystem.IsWindows()) return new WindowsRecycleBin();
        if (OperatingSystem.IsLinux()) return new FreedesktopTrash();
        return new UnsupportedRecycleBin(); // macOS arrives in Phase 9
    }

    public void Dispose() => Database.Dispose();
}
