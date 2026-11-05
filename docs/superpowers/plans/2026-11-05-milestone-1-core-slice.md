# Milestone 1 — Core Slice Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A runnable Avalonia app that scans a folder or drive, shows sizes in a drill-down list and a treemap, puts a safety verdict with its "why" on every item, and lists detected dev projects with their rebuildable size.

**Architecture:** OS-independent libraries (`Miu.Core`, `Miu.Dev`, `Miu.Data`, `Miu.Platform.Abstractions`, `Miu.Platform.Portable`) plus the Avalonia UI in `Miu.App`. Everything in this milestone builds and runs on Windows, macOS and Linux. Windows-only speed-ups (MFT scanning, Recycle Bin, Credential Manager) come in later milestones, behind the interfaces defined here.

**Tech Stack:** .NET 10, C#, Avalonia 11.3, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite, xUnit, FluentAssertions 7.x (Apache-2.0; do not use 8.x, which changed license).

**Spec:** `docs/superpowers/specs/2026-11-05-miu-smart-storage-design.md`

**Spec features covered:** F0.1, F0.2, F0.3 (shell), F0.4 (DI), F0.5, F1.1, F1.2 (portable scanner), F1.4, F1.5 (basic), F1.6 (drill-down list), F1.7 (treemap), F1.10, F2.1, F2.2, F2.3 (dev and system packs, partial), F2D.1, F2D.2, F2D.4 (read-only view).

## Global Constraints
- Target `net10.0` for every project. Nullable enabled and `TreatWarningsAsErrors=true` via `Directory.Build.props`.
- `Miu.Core`, `Miu.Dev`, `Miu.Data` and `Miu.Platform.*` must not reference Avalonia.
- **Nothing in this milestone deletes files.** Verdicts are shown only; delete arrives with the Recycle Bin task (F2.7).
- The protected-path guard can never be overridden by any rule (spec §5.2/§5.3: Protected tier first).
- Verdict names and badges: Protected "Do not delete", Keep "Keep", Review "Review first", Safe "Safe to delete" (spec §5.2).
- Idle buckets: active < 14 d, cooling 14–90 d, idle > 90 d, abandoned > 365 d (spec §5.11).
- Folders the scanner never descends into: reparse points (junctions, symlinks).

## Review Focus
1. **Junk folders containing project markers** (`node_modules/**/package.json`): these must not be detected as projects, otherwise a single app shows up as thousands of "projects". → test in Task 6.
2. **Access-denied folders during a scan** (e.g. `/root`, `C:\System Volume Information`): the scan must skip them and finish, not throw. → test in Task 3.
3. **Symlink pointing into a protected path**: the guard must treat it as protected. → test in Task 4.
4. **Case differences on Windows** (`c:\windows` vs `C:\Windows`): the guard and globs must be case-insensitive on Windows and case-sensitive elsewhere. → test in Task 4 and Task 5.
5. **`dist`/`build`/`bin` folders in a project of the wrong stack** (e.g. `build/` holding source scripts in a Python repo): these must not be marked Safe. → test in Task 5.

---

### Task 1: Solution scaffold and CI (F0.1, F0.2)

**Files:**
- Create: `Miu.sln`, `Directory.Build.props`, `.gitignore`, `global.json`
- Create: `src/Miu.Core/Miu.Core.csproj`, `src/Miu.Dev/Miu.Dev.csproj`, `src/Miu.Data/Miu.Data.csproj`, `src/Miu.Platform.Abstractions/…csproj`, `src/Miu.Platform.Portable/…csproj`, `src/Miu.App/Miu.App.csproj`
- Create: `tests/Miu.Core.Tests`, `tests/Miu.Dev.Tests`, `tests/Miu.Data.Tests`, `tests/Miu.Platform.Tests`
- Create: `.github/workflows/ci.yml`

**Interfaces:** Produces the project graph: Abstractions ← Core ← Dev; Abstractions ← Portable; Core ← Data; App → all of them.

- [ ] Step 1: Create the projects with `dotnet new classlib|xunit`, plus `Directory.Build.props` (net10.0, Nullable, ImplicitUsings, TreatWarningsAsErrors, LangVersion latest) and `global.json` pinning SDK 10.0.x with `rollForward: latestFeature`.
- [ ] Step 2: CI runs `dotnet build` and `dotnet test` on a matrix of `ubuntu-latest` and `windows-latest`.
- [ ] Step 3: Run `dotnet build Miu.sln`. Expected: Build succeeded, 0 warnings.
- [ ] Step 4: Commit `chore: scaffold solution and CI`.

### Task 2: ScanTree and Categorizer (F1.1, F1.4)

**Files:**
- Create: `src/Miu.Core/Scanning/ScanTree.cs`, `src/Miu.Core/Scanning/FileCategory.cs`, `src/Miu.Core/Scanning/Categorizer.cs`
- Test: `tests/Miu.Core.Tests/Scanning/ScanTreeTests.cs`, `tests/Miu.Core.Tests/Scanning/CategorizerTests.cs`

**Interfaces (Produces):**
```csharp
public sealed class ScanTree {
  public ScanTree(string rootPath);
  public int Root { get; }                       // always 0
  public string RootPath { get; }
  public int Count { get; }
  public int Add(int parentId, string name, bool isDir, long size, DateTime modifiedUtc); // thread-safe
  public void ComputeAggregates();               // fills dir Size/FileCount/NewestModifiedUtc bottom-up
  public string Name(int id); public bool IsDir(int id); public long Size(int id);
  public int FileCount(int id); public DateTime ModifiedUtc(int id); public DateTime NewestModifiedUtc(int id);
  public int Parent(int id);                     // -1 for root
  public IReadOnlyList<int> Children(int id);    // sorted by Size desc after ComputeAggregates
  public string GetFullPath(int id);
  public int? FindChild(int parentId, string name);
}
public enum FileCategory { Video, Image, Audio, Archive, Installer, Document, Code, Game, AppDataCache, System, Other }
public static class Categorizer { public static FileCategory Categorize(string name, bool isDir, string fullPath); }
```
Storage: parallel `List<>` columns (or a struct list) plus a first-child/next-sibling index; a lock inside `Add`.

- [ ] Step 1: Write the failing tests:
  - `Aggregates_sum_file_sizes_up_the_tree`: root → a(dir) → f1 (100 bytes), f2 (50); root → f3 (10). Assert `Size(root)==160`, `FileCount(root)==3`, `Size(a)==150`.
  - `Children_sorted_by_size_desc`.
  - `GetFullPath_joins_with_platform_separator`.
  - `NewestModifiedUtc_is_max_of_descendants`.
  - `Add_is_thread_safe`: `Parallel.For` adds 100k files; `FileCount(root)==100000`.
  - Categorizer theory: `movie.MKV`→Video, `setup.msi`→Installer, `a.zip`→Archive, `x.ts`→Code, `photo.HEIC`→Image, path containing `\AppData\Local\Temp\` or `/.cache/`→AppDataCache, `pagefile.sys`→System, `x.unknown`→Other.
- [ ] Step 2: Run `dotnet test tests/Miu.Core.Tests`. Expected: FAIL (types missing).
- [ ] Step 3: Implement `ScanTree` and `Categorizer`.
- [ ] Step 4: Run the tests. Expected: PASS.
- [ ] Step 5: Commit `feat(core): scan tree and categorizer`.

### Task 3: IScanner and the portable ManagedScanner (F1.2 portable)

**Files:**
- Create: `src/Miu.Platform.Abstractions/IScanner.cs`, `src/Miu.Platform.Portable/ManagedScanner.cs`
- Test: `tests/Miu.Platform.Tests/ManagedScannerTests.cs`

**Interfaces:**
- Consumes: `ScanTree` (Task 2). Abstractions references Core for the `ScanTree` type.
- Produces:
```csharp
public sealed record ScanProgress(long Files, long Directories, long Bytes, string CurrentPath);
public interface IScanner { Task<ScanTree> ScanAsync(string rootPath, IProgress<ScanProgress>? progress, CancellationToken ct); }
public sealed class ManagedScanner : IScanner { public ManagedScanner(int maxParallelism = 0); }
```
Approach: `DirectoryInfo.EnumerateFileSystemInfos` with `EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = 0 }`. Skip recursion into entries with `FileAttributes.ReparsePoint`. Recurse into subdirectories with `Parallel.ForEach`. Report progress at most every 100 ms. Call `ComputeAggregates()` at the end.

- [ ] Step 1: Write the failing tests on a temp fixture (a/b/c.txt = 1000 bytes, a/d.bin = 500, e.txt = 20):
  - `Scans_sizes_and_counts`
  - `Skips_symlinked_directories` (create a dir symlink to the fixture's parent and assert it is not traversed)
  - `Skips_unreadable_directory_without_throwing` (Unix: `chmod 000`, skipped when running as root; Windows: skip)
  - `Honors_cancellation` (pre-cancelled token → `OperationCanceledException`)
  - `Reports_progress`
- [ ] Step 2: Run them. Expected: FAIL.
- [ ] Step 3: Implement.
- [ ] Step 4: Run them. Expected: PASS.
- [ ] Step 5: Commit `feat(platform): portable parallel scanner`.

### Task 4: Protected-path guard (F2.1)

**Files:**
- Create: `src/Miu.Core/Rules/ProtectedPathGuard.cs`
- Test: `tests/Miu.Core.Tests/Rules/ProtectedPathGuardTests.cs`

**Interfaces (Produces):**
```csharp
public sealed class ProtectedPathGuard {
  public ProtectedPathGuard(IEnumerable<string> protectedTrees, IEnumerable<string> protectedExact, bool caseInsensitive);
  public bool IsProtected(string path);          // tree root or below; exact path; or an ancestor of any of them
  public string? Explain(string path);           // human reason, or null
  public static ProtectedPathGuard ForWindows(string systemDrive, string windir, string programFiles, string programFilesX86, string programData, string usersRoot, string userProfile);
  public static ProtectedPathGuard ForUnix(string home);
  public static ProtectedPathGuard ForCurrentOs();
}
```
- Windows trees: windir, both Program Files, `ProgramData\Microsoft`, `<sys>\System Volume Information`, `<sys>\$Recycle.Bin`, `<sys>\Recovery`, and pagefile.sys, hiberfil.sys and swapfile.sys at the drive root. Windows exact: the drive root, usersRoot, userProfile.
- Unix trees: `/bin /boot /dev /etc /lib /lib64 /proc /sbin /sys /usr /var/lib /System /Library /Applications`. Unix exact: `/`, `/home`, `/Users`, home.
- Normalization: strip the `\\?\` prefix, `Path.GetFullPath`, trim trailing separators. If the path exists and is a link, also check `ResolveLinkTarget(returnFinalTarget: true)`.

- [ ] Step 1: Write the failing tests:
  - Windows (pure strings): `C:\Windows\System32\x.dll`→true; `c:\WINDOWS`→true; `C:\`→true; `C:\Users\Khang`→true; `C:\Users\Khang\Downloads`→false; `C:\Users`→true; `\\?\C:\Program Files\App`→true; `C:\Windowsold\x`→false (prefix trap).
  - Unix: `/usr/lib/x`→true; `/home/u`→true (home exact); `/home/u/code/node_modules`→false; `/`→true; `/USR/lib`→false (case-sensitive).
  - `Symlink_into_protected_tree_is_protected` (Unix only: link in temp → `/usr`).
- [ ] Step 2: Run them. Expected: FAIL.
- [ ] Step 3: Implement.
- [ ] Step 4: Run them. Expected: PASS.
- [ ] Step 5: Commit `feat(core): protected path guard`.

### Task 5: Rules engine, glob matching and built-in packs (F2.2, F2.3 partial, F2D.2)

**Files:**
- Create: `src/Miu.Core/Rules/{Verdict.cs, Rule.cs, Glob.cs, ItemFacts.cs, RuleEngine.cs, RuleLoader.cs}`
- Create: `rules/builtin/dev.json`, `rules/builtin/system.json` (copied to output as `rules/builtin/*.json`)
- Test: `tests/Miu.Core.Tests/Rules/{GlobTests.cs, RuleEngineTests.cs, BuiltinRulesTests.cs}`

**Interfaces:**
- Consumes: `ProtectedPathGuard` (Task 4).
- Produces:
```csharp
public enum Verdict { Safe, Review, Keep, Protected }
public enum RuleSource { Protected, User, Learned, Builtin }
public sealed record RuleMatch(string? Path = null, string[]? Names = null, string[]? Ext = null, bool? IsDir = null);
public sealed record RuleConditions(int? OlderThanDays = null, long? MinSizeMB = null, int? ProjectIdleDays = null, string[]? RequiresSiblingMarker = null);
public sealed record Rule(string Id, RuleMatch Match, RuleConditions Conditions, Verdict Verdict, string Reason, RuleSource Source = RuleSource.Builtin, int Priority = 500, bool Enabled = true, string? RebuildHint = null);
public sealed record ItemFacts(string FullPath, string Name, bool IsDir, long Size, DateTime ModifiedUtc, DateTime NowUtc, int? ProjectIdleDays, Func<string, bool> SiblingExists);
public sealed record VerdictResult(Verdict Verdict, string? RuleId, string Reason, double Confidence, string? RebuildHint);
public static class Glob { public static bool IsMatch(string pattern, string text, bool caseInsensitive); } // * ? ** ; both separators accepted
public sealed class RuleEngine {
  public RuleEngine(IEnumerable<Rule> rules, ProtectedPathGuard guard, bool caseInsensitive);
  public VerdictResult Evaluate(ItemFacts facts);
}
public static class RuleLoader { public static IReadOnlyList<Rule> LoadJson(string json); public static IReadOnlyList<Rule> LoadDirectory(string dir); }
```
Evaluation:
1. If the guard says protected → `Protected` (RuleId `protected`, reason from `Explain`, confidence 1).
2. Otherwise collect enabled rules whose Match and Conditions all hold. Order by tier (User/Learned before Builtin), then Priority desc, then specificity (literal characters in Path + number of non-null conditions) desc. The first rule wins.
3. No match → `Review`, reason "No rule matched: review before deleting", confidence 0.5.

`ProjectIdleDays` holds when `facts.ProjectIdleDays >= value`. `RequiresSiblingMarker` holds when any marker satisfies `SiblingExists` (a marker may be a glob such as `*.csproj`).

`dev.json` follows spec §5.11. Rules for the same folder name pair up: a high-priority Safe rule with `ProjectIdleDays: 14` plus a lower-priority Review rule ("Active project: will rebuild on next run"). Markers by folder: `dist|build|out|.next|.nuxt|.svelte-kit|.turbo|.parcel-cache|.vite|storybook-static|coverage|node_modules`→`package.json`; `target`→`Cargo.toml|pom.xml`; `bin|obj`→`*.csproj|*.fsproj|*.sln`; `.venv|venv`→`pyvenv.cfg` (a child marker; implement as `SiblingExists("venv/pyvenv.cfg")`, resolved relative to the item's parent); `__pycache__|.pytest_cache|.mypy_cache|.ruff_cache|.tox` need no marker; `.gradle`→`build.gradle*|settings.gradle*`; `.terraform`→`*.tf`. Every dev rule carries a `RebuildHint` (e.g. "npm install", "cargo build").

`system.json`: temp folders (`**/AppData/Local/Temp/**`, `/tmp/**`) Safe after 1 day; `**/*.dmp` Safe after 7 days; `**/Downloads/*.{exe,msi}` Review with reason "Old installer"; `**/.cache/thumbnails/**` Safe.

- [ ] Step 1: Write the failing tests:
  - Glob: `**/node_modules` matches `C:\a\b\node_modules` (case-insensitive) and `/a/node_modules`; `*.dmp` does not match across separators; `/a/**` matches `/a/b/c`.
  - `Protected_beats_any_rule` (a Safe rule matching `/usr/**` is still Protected).
  - `User_tier_beats_builtin`.
  - `Higher_priority_wins_within_tier`.
  - `No_match_is_review`.
  - `ProjectIdleDays_condition`.
  - `RequiresSiblingMarker_glob`.
  - Built-in: `Every_builtin_rule_loads`; `Idle_node_modules_is_safe_with_hint` (idle 30, sibling package.json → Safe, hint "npm install"); `Active_node_modules_is_review`; `Build_folder_without_package_json_is_review` (Review Focus 5); `Bin_folder_in_dotnet_project_is_safe_when_idle`.
- [ ] Step 2: Run them. Expected: FAIL.
- [ ] Step 3: Implement. Cache compiled globs as `Regex` in a `ConcurrentDictionary`.
- [ ] Step 4: Run them. Expected: PASS.
- [ ] Step 5: Commit `feat(core): rules engine with builtin dev and system packs`.

### Task 6: Project detector (F2D.1)

**Files:**
- Create: `src/Miu.Dev/{ProjectDetector.cs, DetectedProject.cs, IdleBucket.cs, DevJunkCatalog.cs}`
- Test: `tests/Miu.Dev.Tests/ProjectDetectorTests.cs`

**Interfaces:**
- Consumes: `ScanTree` (Task 2).
- Produces:
```csharp
public enum IdleBucket { Active, Cooling, Idle, Abandoned }
public sealed record DetectedProject(int NodeId, string Path, string Name, IReadOnlyList<string> Stacks, DateTime LastActivityUtc, int IdleDays, IdleBucket Bucket, long TotalBytes, long RebuildableBytes);
public static class DevJunkCatalog { public static bool IsJunkFolder(string name, IReadOnlyCollection<string> stacks); public static IReadOnlySet<string> AllJunkNames { get; } }
public static class ProjectDetector {
  public static IReadOnlyList<DetectedProject> Detect(ScanTree tree, DateTime nowUtc);   // sorted by RebuildableBytes desc
  public static IdleBucket BucketFor(int idleDays);
}
```
Rules:
- Markers come from spec §5.11, mapped to the stacks Node, Rust, Python, Go, .NET, JVM, Ruby, PHP, Flutter, Docker, Git.
- Never descend into any `AllJunkNames` folder or `.git` while searching for projects (Review Focus 1).
- Nested projects are allowed, e.g. a monorepo `apps/web`.
- Last activity = max(`NewestModifiedUtc` of non-junk, non-`.git` children, mtime of `.git/logs/HEAD` if present).
- Rebuildable = sum of the sizes of direct-child folders where `IsJunkFolder(name, stacks)` is true.

- [ ] Step 1: Write the failing tests using hand-built `ScanTree`s:
  - `Detects_node_project_with_rebuildable_size`
  - `Ignores_package_json_inside_node_modules`
  - `Detects_nested_monorepo_projects`
  - `Last_activity_ignores_node_modules_mtime` (node_modules newer than src → src time is used)
  - `Build_folder_only_rebuildable_for_node`
  - `BucketFor` boundaries: 13→Active, 14→Cooling, 90→Cooling, 91→Idle, 366→Abandoned
- [ ] Step 2: Run them. Expected: FAIL.
- [ ] Step 3: Implement.
- [ ] Step 4: Run them. Expected: PASS.
- [ ] Step 5: Commit `feat(dev): project detector`.

### Task 7: SQLite database and settings (F0.5)

**Files:**
- Create: `src/Miu.Data/{MiuDatabase.cs, SettingsRepository.cs}`, `src/Miu.Data/Migrations/001_init.sql` (embedded resource)
- Test: `tests/Miu.Data.Tests/{MigrationTests.cs, SettingsRepositoryTests.cs}`

**Interfaces (Produces):**
```csharp
public sealed class MiuDatabase : IDisposable {
  public MiuDatabase(string filePath);          // ":memory:" allowed for tests
  public SqliteConnection Connection { get; }
  public int SchemaVersion { get; }
  public void Migrate();                         // applies embedded NNN_*.sql in order, records them in schema_version
}
public sealed class SettingsRepository {
  public SettingsRepository(MiuDatabase db);
  public string? Get(string key); public void Set(string key, string value);
  public bool GetBool(string key, bool fallback); public void SetBool(string key, bool value);
}
public static class SettingKeys { public const string AutoCleanAllowed = "automation.autoCleanAllowed"; public const string RunInBackground = "agent.runInBackground"; public const string Theme = "ui.theme"; }
```
`001_init.sql` creates every table in spec §6, with WAL mode turned on.

- [ ] Step 1: Write the failing tests:
  - `Migrate_on_empty_db_creates_all_tables` (assert the 17 table names from §6 exist)
  - `Migrate_is_idempotent`
  - `Settings_roundtrip`
  - `AutoCleanAllowed_defaults_false`
- [ ] Step 2: Run them. Expected: FAIL.
- [ ] Step 3: Implement.
- [ ] Step 4: Run them. Expected: PASS.
- [ ] Step 5: Commit `feat(data): sqlite schema, migrations, settings`.

### Task 8: Squarified treemap layout (F1.7, algorithm part)

**Files:**
- Create: `src/Miu.Core/Layout/Squarify.cs`
- Test: `tests/Miu.Core.Tests/Layout/SquarifyTests.cs`

**Interfaces (Produces):**
```csharp
public readonly record struct LayoutRect(double X, double Y, double Width, double Height);
public static class Squarify {
  public static IReadOnlyList<LayoutRect> Layout(IReadOnlyList<double> values, LayoutRect bounds); // values sorted desc; output order matches input
}
```
Algorithm: Bruls, Huizing and van Wijk's squarified treemap. Values ≤ 0 get zero-size rects.

- [ ] Step 1: Write the failing tests:
  - `Areas_proportional_to_values` (each area equals value/total × bounds area, within 0.01)
  - `Rects_inside_bounds_and_non_overlapping`
  - `Aspect_ratios_reasonable` (the classic 6,6,4,3,2,2,1 on 6×4 gives a worst aspect ratio ≤ 3)
  - `Zero_values_get_empty_rects`
- [ ] Step 2: Run them. Expected: FAIL.
- [ ] Step 3: Implement.
- [ ] Step 4: Run them. Expected: PASS.
- [ ] Step 5: Commit `feat(core): squarified treemap layout`.

### Task 9: Avalonia app shell — Home, Analyze, Dev Projects (F0.3, F0.4, F1.5, F1.6, F1.7 UI, F1.10, F2D.4 read-only)

**Files:**
- Create: `src/Miu.App/{Program.cs, App.axaml(.cs), ViewLocator.cs}`, `Services/{AppServices.cs, VerdictService.cs, ScanSession.cs}`
- Create: `ViewModels/{MainWindowViewModel.cs, HomeViewModel.cs, AnalyzeViewModel.cs, DevProjectsViewModel.cs, NodeItemViewModel.cs}`
- Create: `Views/{MainWindow.axaml, HomeView.axaml, AnalyzeView.axaml, DevProjectsView.axaml}`, `Controls/{TreemapControl.cs, VerdictBadge.axaml}`, `Styles/Theme.axaml`
- Test: `tests/Miu.App.Tests/` (Avalonia.Headless.XUnit): `AppSmokeTests.cs`

**Interfaces:**
- Consumes: `IScanner`/`ManagedScanner`, `ScanTree`, `RuleEngine`/`RuleLoader`/`ProtectedPathGuard.ForCurrentOs()`, `ProjectDetector`, `Squarify`, `MiuDatabase`/`SettingsRepository`.
- Produces:
  - `ScanSession`: holds the current `ScanTree`, the `RuleEngine` and the project list. It exposes `Task ScanAsync(string path, IProgress<ScanProgress>)` and `VerdictResult VerdictFor(int nodeId)`, where `ProjectIdleDays` comes from the nearest enclosing `DetectedProject`, and `SiblingExists` checks the scan tree's siblings.

UI follows the mockups (Fluent, light/dark):
- **Sidebar:** Home · Analyze · Dev Projects · Rules (placeholder) · Settings (placeholder).
- **Home:** drive cards from `DriveInfo.GetDrives()` filtered to `IsReady` and `DriveType` ∈ {Fixed, Network, Removable} with `TotalSize > 1 GB`, each with a usage bar. Also a "Scan a folder…" button (folder picker).
- **Analyze:**
  - Breadcrumb and Up button.
  - A list of children with name, size bar, size, a category dot and a verdict badge. Double-click to drill down.
  - Treemap of the current folder's children; click selects, double-click drills down.
  - Details pane: path, size, files, modified date, verdict with reason, rule id and rebuild hint, plus Open in file manager and Copy path.
- **Dev Projects:** a table with name, stacks, last activity (in plain words), idle bucket chip, total and rebuildable sizes, sorted by rebuildable size, with a summary line "N projects · X GB rebuildable · M idle > 90 days".
- **Sizes:** `SizeFormatter.Format(long bytes)` → "1.2 GB" (lives in Miu.Core, with a test).

- [ ] Step 1: Write a failing headless smoke test: `MainWindow_opens_and_scans_temp_folder` creates a fixture, runs `AnalyzeViewModel.ScanCommand` and asserts the children count and that the root size text is non-empty.
- [ ] Step 2: Run it. Expected: FAIL.
- [ ] Step 3: Implement the views and ViewModels (CommunityToolkit.Mvvm `[ObservableProperty]`, `[RelayCommand]`; compiled bindings).
- [ ] Step 4: Run it. Expected: PASS. Then render headless screenshots of Home, Analyze and Dev Projects into `docs/screenshots/` and check them visually.
- [ ] Step 5: Commit `feat(app): avalonia shell with home, analyze, treemap, dev projects`.

### Task 10: Docs and checklist

- [ ] Update the README (what Miu is, how to build and run, screenshots) and tick the covered features in spec §14.
- [ ] Commit, push, and update the PR description.
