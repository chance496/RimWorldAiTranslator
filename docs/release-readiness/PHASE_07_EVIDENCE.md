# Phase 07 Security and Privacy Evidence

## Status and scope

- Scope: the settled local C# working tree after Phase 07 endpoint, credential, review-store, rollback, resource-boundary, native archive and active-content hardening.
- Phase 07 technical result: **PASS for the current source**. The latest post-output-cap/leading-ampersand Release console run passed 71/71 with FAIL/SKIP 0/0, exit 0, in 23.028 seconds. All four Phase 07 groups passed.
- Latest compile evidence, run after that console regression: all eight solution projects built in Release with Recommended analyzers and warnings-as-errors, with 0 warnings and 0 errors.
- Latest independent diff re-audit found 0 P0 and 0 P1 issues in the settled Phase 07 changes.
- Overall RC/public-distribution result is not PASS: SEC-009 and SEC-010 remain OPEN, the Phase 04 ZIP predates Phase 07, and the settled package and Phase 10 independent binding do not yet exist.
- No real user data, API key, paid API, real provider, external upload, Git staging/commit or public GitHub mutation was used for this evidence.

## Commands and results

| Command or probe | Result | Evidence meaning | Limitation |
|---|---|---|---|
| `dotnet build RimWorldAiTranslator.sln -c Release --no-restore -p:TreatWarningsAsErrors=true -p:AnalysisMode=Recommended` | exit 0; eight projects; 0 warnings; 0 errors | The settled current source compiles under the configured strict analyzer policy | Compile success alone is not a security or package verdict |
| `dotnet tests\RimWorldAiTranslator.Tests\bin\Release\net8.0\RimWorldAiTranslator.Tests.dll` | exit 0; 71/71 passed; FAIL/SKIP 0/0; 23.028 s | Latest post-output-cap/leading-ampersand full console regression | The top-level count is not an assertion count and is not final-package identity evidence |
| Console groups `Phase07.SecurityHardening`, `Phase07.ResourceBoundaries`, `Phase07.NativeArchiveAndWriteBoundary`, `Phase07.NativeActiveContentAndLimits` | all PASS in the 71/71 run | All four Phase 07 security/resource groups executed on the settled Release assemblies | Synthetic fixtures cannot prove behavior against a compromised kernel/administrator |
| Independent latest-diff adversarial re-audit | P0: 0; P1: 0 | No unresolved critical/high-priority defect was found in the settled Phase 07 diff | This is source-diff evidence, not clean-PC or exact-package evidence |
| UI harness mode `--provider-url-security` with a unique local data root and explicit error output | exit 0; no error file | Visible correction warning, blocked unsafe drafts, safe save, backup-cleanup lifecycle, local-only path behavior and log privacy passed through the Windows UI | No real provider or network endpoint was contacted |
| UI harness mode `--slow-logger-drain` | exit 0; no error file | UI remained responsive while persistent logging was blocked and close waited for the drain release | Synthetic writer; not a failing physical disk |
| Glossary tool `self-test` | `self-test: PASS` | Current glossary tool synthetic generation/validation path passed | Does not resolve redistribution rights for the bundled derived glossary |
| Strong credential-pattern source scan excluding `bin/`, `obj/`, `artifacts/` and `.git/` | four expected synthetic test files; 0 actual credentials | Current source scan found only deliberate regression literals | Final publish/ZIP scan remains a Phase 09/10 gate |
| Active-file scan for `*.ps1` | 0 active PowerShell files | Current working filesystem has no PowerShell implementation/build/runtime dependency | Historical Git/index and stale ZIP evidence are separate; the Phase 04 ZIP is not current RC evidence |
| Project-file scan plus `dotnet list RimWorldAiTranslator.sln package --include-transitive` | 0 `PackageReference`; all eight projects reported no direct/transitive package | Application NuGet dependency inventory is empty | .NET runtime, Windows APIs, project references and repository native code remain dependencies |
| `dotnet list ... package --vulnerable --config NuGet.config` | failed: `Attempted to divide by zero` | No usable vulnerability result | Cleared package sources make this **inconclusive**, not PASS |
| `dotnet list ... package --deprecated --config NuGet.config` | failed: `Attempted to divide by zero` | No usable deprecation result | Cleared package sources make this **inconclusive**, not PASS |
| `dotnet --info` | SDK 8.0.422; MSBuild 17.11.48; host 10.0.1; installed .NET/WindowsDesktop 8.0.28 | Local toolchain/runtime inventory | Inventory is not an advisory or license audit |

`global.json` pins SDK 8.0.422, uses `rollForward: latestPatch`, and disallows prerelease SDKs. `NuGet.config` clears every package source; that explains the unusable vulnerable/deprecated queries but does not establish that the self-contained runtime is advisory-free.

## Directly verified Phase 07 behavior

### `Phase07.SecurityHardening`

- Source-language discovery/extraction rejects selected-root, child-root, `Languages\English` and `Defs` reparse escapes; aggregate file/byte/record/character limits and cancellation are exercised.
- Direct and review-only translation refuse lexical/canonical Workshop output roots. Dry-run and pre/mid-scan cancellation remain write-free.
- Review decision reads are treated as recovery-capable writes. Current, previous, Activity, Apply, RMK and direct repository consumers reject Workshop or app-external review roots before probing; backup-only denied stores remain unchanged with no primary or corrupt recovery leaf. A valid backup inside the trusted application reviews root is recovered with the backup preserved and a recovery notice.
- `AtomicJsonStore` oversized read/write rejection and custom glossary input beyond 16 MiB are exercised.
- Persistent logging removes raw/prefixed/named/quoted credentials, qualifier variants, JWT/AWS/DeepL/Google-style tokens, absolute paths and structural control/bidi characters. Ambiguous ampersand-delimited credential tails, including a leading-ampersand value, are removed while unrelated query fields survive. Messages are deterministically capped at 16,384 UTF-16 characters without splitting a surrogate pair.
- Comparison CSV cells beginning with spreadsheet formula markers are neutralized.
- Provider endpoints require HTTPS, reject fragments and credential material in user-info/host/path/query/fragment after bounded recursive decoding, and accept only the query allowlist `api-version`, `format=json`, and `version` with strict values. Redirects and cookies are disabled.
- Settings reject credential material before write, scan primary and backup including duplicate JSON property shadows, preserve safe unknown fields, and keep safe Boolean/null requirement metadata such as `requiresPassword=true` and `credentialReference=null`. Exact credential-named Boolean properties still fail closed.
- Oversized provider input produces zero transport calls; configured whole-run request budgets stop retries exactly; Google uses the bounded fake handler without real network access.
- The RMK Builder confirmed plan rejects a same-length hash/content swap; reserved output names are rejected.

### `Phase07.ResourceBoundaries`

- Review comparison loading enforces candidate-file, 128 MiB document, depth, row, raw-token, per-row, aggregate string and per-string budgets before accepting decisions.
- Legacy comparison retention enforces 512 MiB aggregate and 1,000,000-row caps without silently discarding unmatched decisions.
- Steam root/library discovery and RMK workspace discovery enforce candidate/container limits and isolate per-container enumeration failures.
- Stored network review, project, glossary and RMK roots are rejected before filesystem probing.
- Immediate language-directory discovery, existing-language XML traversal and aggregate file/byte/record/character limits are exercised at and beyond their configured bounds.

### `Phase07.NativeArchiveAndWriteBoundary`

- Trusted read boundaries create no source file or missing directory, pin the source leaf, and block source write/delete/move while held.
- Snapshot and translation-output preflight enforce per-leaf/aggregate byte limits, cancellation cleanup and a 16,384-distinct-target maximum before the transaction action.
- Rollback distinguishes File, Missing, Directory and Unstable states. Removed/replaced/recreated directories do not become successful file rollback; directory reparse targets are rejected before mutation.
- Synchronous, asynchronous, same-content/different-identity and LanguageData concurrent saves are preserved instead of overwritten; required recovery snapshots remain when automatic rollback cannot safely win.
- Apply/RMK target and `.bak` leaves reject hard links and reparse points. Parent directory delete/move, absent-leaf reparse injection and outside-sentinel attacks fail closed with no boundary-guard residue.
- Prepared bytes, existing/absent target, target backup and post-commit evidence are guarded with identity plus content-hash CAS. Concurrent saves are preserved, recovery paths are reported where needed, and successful target/backup evidence remains pinned through the boundary.
- RMK stale-output detection, language walker limits, resource budgets and review comparison source-evidence pinning are exercised.

### `Phase07.NativeActiveContentAndLimits`

- XLSX external/escaping relationships, active parts/content types, OLE/macro-sheet relationships, formulas in cells, validation, defined names, tables, VML and relocated/content-type-only SpreadsheetML parts are rejected.
- Custom XML DTD and external stylesheet processing instructions are rejected without changing the workbook or leaving a temporary output. Passive custom XML merely named `formula` remains accepted.
- Rejected workbook updates preserve original bytes and leave no `tmp-*` output.
- Excel row 1,048,576 remains readable, row 1,048,577 and append beyond the final row fail closed.
- Writer cell, aggregate-character and estimated-size limits, and native source XML raw-entry, node-count and per-value text limits are exercised directly.

## Trusted review-decision recovery boundary

`review-decisions.json` reads can restore a valid `.bak`, so they are not treated as read-only operations. The settled path is:

1. Canonicalize the review root, reject network and lexical/canonical Workshop roots, and for the application require strict containment under its configured reviews root.
2. Acquire a trusted write boundary over `review-decisions.json` and its deterministic `.bak` before checking existence or parsing either leaf.
3. Reject reparse/device/multi-link leaves and pin directory plus file identity/content.
4. On backup recovery, copy and validate the pinned backup, preserve an existing corrupt primary, and commit the replacement with CAS without replacing the valid backup.
5. Wire the configured application reviews root into workspace load/inheritance/save, Activity, local Apply and RMK export.

The backup-only Workshop/outside-app fixtures prove zero primary creation and unchanged backup bytes for every reader category. The trusted in-root fixture proves recovery and notice emission without destroying the valid backup.

## Transaction and rollback boundary

- `PathSafety.AcquireTrustedWriteBoundary` holds canonical directory handles without delete sharing and transient delete-on-close guards for every target-parent chain.
- Existing target and backup leaves are regular, single-link files whose identity and SHA-256 are pinned; absent leaves are also tracked so later reparse/file injection is detected.
- `FileSnapshotJournal` records File/Missing/Directory/Unstable state, caps targets at 16,384, bounds aggregate snapshot/fingerprint work, and compares current identity/content before rollback.
- Rollback never overwrites an identity-distinct concurrent save merely because bytes match. If a concurrent writer wins, its content stays in place and the original/recovery evidence is retained and surfaced.

These controls reduce local race windows but do not defeat an administrator, malicious kernel/filter driver, or process already able to tamper inside the application.

## RMK Builder execution boundary

The Builder starts as one exact canonical `LoadFoldersBuilder.exe`, with its length and SHA-256 rechecked while locked. It is created suspended, assigned to a kill-on-close Windows job before its primary thread resumes, inherits only redirected standard streams, receives a filtered environment, has bounded stdout/stderr and a 120-second timeout, and runs inside a rollback transaction for `LoadFolders.xml` and `ModList.tsv`.

This is containment, **not a sandbox**. The child still runs as the current user and may access that user's files and network. Only the selected EXE identity/hash is pinned; adjacent DLL/config content and the rest of the work clone are not individually authenticated. The user must trust the complete RMK work clone before execution.

## Secret and privacy scan

- Actual private key/API credential found: **0**.
- The four expected matches were:
  - `tests/RimWorldAiTranslator.Tests/Phase07SecurityTests.cs`;
  - `tests/RimWorldAiTranslator.Tests/Program.cs`;
  - `tests/RimWorldAiTranslator.Tests/SafetyIntegrationTests.cs`;
  - `tests/RimWorldAiTranslator.UiHarness/Program.cs`.
- All matched values are deliberate synthetic regression literals. No real key, authorization header or user source was used.
- The scan excluded generated/evidence/Git trees; the settled publish/ZIP must be scanned again.

## Dependency, runtime and advisory boundary

- Active PowerShell files: 0.
- Direct `PackageReference`: 0; transitive application package list: 0 across eight projects.
- The product still depends on the .NET SDK/runtime, Windows APIs, project references and repository native source.
- The vulnerable/deprecated package commands are inconclusive because package sources are cleared. They are not vulnerability/deprecation PASS evidence.
- A separate authoritative .NET 8.0.28 runtime advisory review and exact final-package inventory remain required.

## OPEN blockers and pending final gates

- **SEC-009 — legal blocker:** `glossary.generated.ko.json` contains RimWorld official-core/DLC-derived text without recorded redistribution authorization. Obtain and record permission/compatible terms or remove/replace it before public distribution.
- **SEC-010 — supply-chain blocker:** a self-contained build redistributes .NET runtime components, but the package has no reviewed runtime/third-party notices file. Generate, review, include and verify authoritative notices.
- The Phase 04 ZIP is stale and must not be called the current RC. Rebuild the local RC only from the settled later source during Phase 09.
- Re-run source/publish/ZIP secret, privacy, PowerShell-zero, manifest, license and advisory checks on the exact Phase 09 output; perform Defender and clean-PC/manual checks where required.
- Bind the settled source, assemblies, package and final evidence through the Phase 10 independent adversarial audit.
