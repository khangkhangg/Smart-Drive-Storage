# Milestone 3 — Types, Largest, Duplicates & Learning Rules

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:**
- Analyze gets the mockup's view switch: **Folders | Types | Largest | Duplicates**.
- Miu **learns from the user**: Keep / Always-OK buttons with reasons, implicit signals from cleaning and undo, suggested rules with evidence, and a real **Rules** page with import/export.

**Architecture:**
- Pure logic lives in `Miu.Core`: `TypeBreakdown`, `LargestFiles`, `Learning/*`, and the new rule match `Under`.
- Hashing lives in `Miu.Platform.Portable/DuplicateFinder` (needs file IO).
- Persistence lives in `Miu.Data`: `DecisionsRepository`, `RulesRepository`, `SuggestionsRepository`.
- `AppServices.CurrentRules()` = built-in rules + enabled user/learned rules. `ScanSession.ReplaceRules()` re-evaluates the scan after rules change.

**Spec:** `docs/superpowers/specs/2026-11-05-miu-smart-storage-design.md`. Features: F1.8, F2.10, F3.1, F3.2, F3.3 (clean and undo signals), F3.4, F3.5, F3.6, F3.8.

## Global Constraints
- Learned and user rules can never override the protected-path guard. The engine already checks it first.
- A suggested rule only takes effect after the user taps **Turn on**. Spec §5.4: ≥ 3 consistent decisions, no contradictions.
- Duplicates are never Safe. Every copy except the one kept is offered as **Review**, with the reason "Duplicate of …". Files inside `.git`, `node_modules`, virtual environments and `site-packages` are not scanned for duplicates.
- Duplicate hashing: group by size (≥ 1 MB by default), then a partial hash (first and last 64 KB), then a full `XxHash128`. It runs in the background and can be cancelled.

## Review Focus
1. **Two different files with the same size and the same first and last 64 KB:** the full hash must separate them. → test.
2. **A file changes or disappears while it is being hashed:** it is skipped, with no crash. → test.
3. **A learned rule from one folder must not leak to a sibling with a similar name** (`/a/Clients` vs `/a/Clients2`): `Under` matches whole path segments. → test.
4. **A decision that contradicts a pending suggestion** stops that suggestion from appearing again. → test.
5. **Importing a rules file with Protected rules, broken JSON or duplicate ids:** protected rules are dropped, nothing breaks, and ids are made unique. → test.

### Task 1: TypeBreakdown + LargestFiles (Core)
- `TypeBreakdown.Compute(ScanTree, int nodeId) -> IReadOnlyList<(FileCategory Category, long Bytes, int Files)>`, sorted by bytes in descending order.
- `LargestFiles.Find(ScanTree, int nodeId, int count) -> IReadOnlyList<int>`, with nodes sorted by size in descending order.
- Tests: `Breakdown_sums_by_category`, `Largest_returns_top_n_files_only`.

### Task 2: DuplicateFinder (Portable)
- `DuplicateFinder.FindAsync(ScanTree, int nodeId, long minBytes, IProgress<int>?, CancellationToken) -> IReadOnlyList<DuplicateGroup>`.
- `DuplicateGroup(long Size, IReadOnlyList<int> Nodes, int KeepNode)`. Wasted bytes = Size × (Count − 1).
- The kept copy is the one with the oldest modification time; on a tie, the shortest path.
- Tests:
  - `Finds_identical_files`
  - `Same_size_different_content_not_grouped`
  - `Same_head_and_tail_different_middle_not_grouped`
  - `Skips_vanished_files`
  - `Ignores_node_modules_and_git`
  - `Keeps_oldest`

### Task 3: Rule `Under` match
- Add `RuleMatch.Under` (string?): it matches the path itself or anything inside it, comparing whole segments with the engine's case rules.
- Tests: `Under_matches_self_and_children`, `Under_does_not_match_sibling_prefix`.

### Task 4: Learning (Core)
- `DecisionAction { Keep, Delete }`.
- `Decision(string Path, bool IsDir, string Name, string? Ext, string ParentPath, DecisionAction Action, string? Reason, DateTime TsUtc)`.
- `PatternMiner.Mine(IReadOnlyList<Decision>, ISet<string> knownKeys, PathStyle) -> IReadOnlyList<RuleSuggestion>`.
- `RuleSuggestion(string Key, Rule Rule, string Evidence, double Confidence)`.
- Keys, checked from narrowest to broadest; decisions used by one suggestion are not reused by a broader one:
  1. extension inside a folder
  2. a folder's direct children
  3. a folder name anywhere
  4. an extension anywhere (needs ≥ 5)
- Learned rules: Keep → verdict `Keep`; Delete → verdict `Safe`. Priority 700, `Source=Learned`.
- Tests:
  - `Three_consistent_deletes_in_folder_suggest_rule`
  - `Contradiction_blocks`
  - `Known_keys_not_repeated`
  - `Narrow_key_wins`
  - `Folder_name_anywhere`
  - `Two_decisions_are_not_enough`

### Task 5: Data repositories
- **DecisionsRepository:** `Add(Decision)`, `All()`.
- **RulesRepository:** `Upsert(Rule)`, `All()`, `SetEnabled(id, bool)`, `Delete(id)`.
- **SuggestionsRepository:** `Add(RuleSuggestion)`, `Pending()`, `SetStatus(id, accepted|rejected)`, `KnownKeys()`.
- Migration 003 adds `key TEXT` to `rule_suggestions`. The rule JSON uses `RuleLoader.JsonOptions`.
- Tests: roundtrip tests for each repository.

### Task 6: LearningService (App)
- `Teach(NodeItemViewModel item, DecisionAction action, string? reason)` records the decision. Keep also adds a user rule `Under=path` with verdict Keep. Always-OK adds a user rule `Under=path` with verdict Safe. Then it mines and refreshes the session.
- `RecordCleaned(paths)` records Delete decisions for each cleaned item. `RecordRestored(paths)` records Keep decisions with the reason "Restored after cleaning". Both then mine.
- `Accept(id)` turns the suggestion into a learned rule and refreshes. `Reject(id)` marks it rejected.
- `ImportJson(string) -> int imported` and `ExportJson() -> string` (user and learned rules only).
- Tests:
  - `Keep_creates_rule_and_changes_verdict`
  - `Cleaning_three_similar_suggests_rule`
  - `Accept_applies_rule_to_scan`
  - `Import_drops_protected_and_dedupes_ids`
  - `Bad_json_is_reported`

### Task 7: UI
- **Analyze:** a view switch (Folders | Types | Largest | Duplicates).
  - Types: category bars.
  - Largest: list of files.
  - Duplicates: "Find duplicates" with progress, then groups with the kept copy marked, plus "Add copies to cart" per group and for all.
- **Details pane:** a "Teach Miu" box with "Keep — never suggest", "Always OK to delete" and reason chips (as in the mockup).
- **Rules page:** stats, Suggested rules (Turn on / Not now), a filterable table (All / Learned / Yours / Built-in), toggles, delete, Import / Export.
- **Home:** the rules card shows real counts and links to the suggestions.
- Headless tests for each, plus new screenshots.

### Task 8: Docs, review, push.
