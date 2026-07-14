# Security and Privacy Model

- Scope: frozen 250-file local C# package-build input snapshot v5, SHA-256 `C279F6F6C87634D95035C3D17821770DE0703649CCD33A3A55B123695683A006`, and exact Phase 10 `v1.0.1-rc.1` ZIP (66,254,006 bytes, SHA-256 `2302E87747F55348EA5EF96E2D352686ADFD943BB82954C9DA1F789553BBB7C1`); an artifact-excluded post-package audit separately proves subsequent reporting drift is governance-only. Public distribution is out of scope.
- Phase status: all locally executable Phase 10 automated security gates are **PASS** after six P1 repairs (three sampled and three later local-gap/package findings). SEC-010 is resolved for this exact manifest-bound package. Overall remains **BLOCKED** by SEC-009 and the explicit external/manual gates.
- Latest evidence: strict eight-project Release 0 warnings/errors; post-fix 82/82; one separate post-PID-fix tooling self-test 18/18; three distinct clean roots each 0/0, 82/82, glossary self-test and package smoke; byte-identical ZIP/EXE/14 payload files. The EXE is 163,800,354 bytes, SHA-256 `9EEB6A80DD51DCAFD31D2516CFB6F1C70257CEDD94783C360ED58FEA3FE919C8`; final run-3 manifest SHA-256 is `E9F8EAE6007C4351125AD47A89B6BE32BB1357354A2BA1924DAFC18D4381C707`, and the normalized manifest SHA-256 is `F61FF7E69E8D564AD32544C902057730EE57B567BA50FA4B63A0C58FA9990B77`.
- Evidence boundary: exact `final-v5-archive-audit.txt`, credential/PowerShell, Defender, runtime, v1 immutability and `final-v5-residue-audit.txt` checks pass. No real provider, real API key or user data was used. The exact ZIP is unsigned; the Defender result is point-in-time and does not replace SmartScreen, clean-PC or human checks. The 268-entry pre-local-gap visual corpus and current local-gap/Korean-path/package probes are separately bound by `final-ui-evidence-binding-v5.txt`.

## Assets

- User projects and translations: project metadata, extracted source text, existing and candidate translations, review status, notes, source history, and apply/export decisions.
- API keys and credentials: provider keys entered in the Settings UI and held as managed strings in the current process.
- Original mod/RMK data: local mods, Workshop subscriptions, RMK subscriptions, and the user-selected RMK work clone.
- Local state: `%LOCALAPPDATA%\RimWorldAiTranslator` settings, project indexes, review runs, logs, temporary checkpoints, and backups.
- User-selected exports: local diagnostic ZIPs, quality reports, `Languages\Korean` output, and RMK XML/XLSX output.
- Product and supply chain: the application executable, native component, .NET self-contained runtime, rules, documentation and licenses/notices. The official-derived generated glossary is a source-side asset deliberately excluded from the exact RC pending rights/parity resolution.

## Actors and assumptions

- The interactive local user is trusted to choose a mod, provider, endpoint, apply target, RMK work clone, and export destination.
- Mod XML, Workshop/RMK content, custom glossary files, stored JSON, XLSX/ZIP packages, provider responses, and custom endpoints are untrusted.
- Provider operators and network intermediaries are outside the application trust boundary. HTTPS protects transport but does not make the selected provider trustworthy.
- Malware, an administrator, a debugger, a process dump, or another process already able to read this process can recover managed-memory secrets; memory-hardening against a compromised host is out of scope.
- A custom endpoint is intentionally not host-allowlisted. The user must establish the endpoint operator, privacy terms, retention, jurisdiction, and billing before use.

## Trust boundaries

| Boundary | Trusted side | Untrusted side | Data crossing | Current controls | Verification |
|---|---|---|---|---|---|
| UI to application services | Typed application state | User-entered keys, endpoint, paths, prompts, review text | Provider settings, in-memory keys, selected roots and actions | Validation, masked key editor, confirmation for destructive/apply actions | Provider-security UI harness exited 0 with no error file; unsafe stored/draft endpoints were blocked, correction remained visible until primary and backup were clean, safe unknown fields persisted and stored UNC paths were not probed |
| Managed code to local file system | App-owned storage and transactional writers | Stored JSON, mod XML, glossary, RMK XLSX/ZIP, reparse points and races | Source/review data, settings, logs, backups and outputs | Canonical trusted roots, ownership markers, reparse/hardlink checks, bounded/streaming reads, CAS atomic replace, backup/rollback, and directory write-boundary locks | All four Phase 07 groups verify read budgets, recovery-capable review reads, directory replacement, leaf races, rollback states and no-outside-write sentinels |
| Managed code to native parser | Validated call contract | XML and XLSX/ZIP structure | Def/LanguageData rows and RMK history | DTD/resolver disabled; depth/node/archive/entry/ratio/active-content/formula/output limits fail closed | `Phase07.NativeArchiveAndWriteBoundary` and `Phase07.NativeActiveContentAndLimits` pass, including rejected-update byte preservation and no temporary output |
| Application to configured provider | Request builder and bounded response parser | Remote HTTPS service and response | API key, source batch, identifiers/context, selected glossary and prompt settings | HTTPS; strict query grammar; credential-bearing URL rejection; redirects/cookies disabled; timeout/retry/input/response budgets and cancellation | Security group, fake handlers and provider UI harness verify encoded credential variants, unchanged storage on reject, zero transport for invalid/oversized input and whole-run attempt caps |
| Application to Google translation endpoint | Token protector and bounded response parser | Google endpoint and response | Source text chunks in an HTTPS GET query | HTTPS, token placeholders, timeout/retry/response/request budgets, cancellation | Fake-handler regression verifies the shared whole-run attempt budget without a real request |
| Application to RMK Builder | Confirmed RMK work-clone plan | User-supplied `LoadFoldersBuilder.exe` and adjacent clone content | `-build`, bounded stdout/stderr, generated files | Canonical EXE identity/length/SHA-256 pin; suspended start; kill-on-close job before resume; standard-stream-only inheritance; filtered environment; 120-second timeout; transaction | Hash-swap, job-assignment, descendant-containment and process-transaction fixtures pass; bounded output/time controls are source-confirmed; this is not a sandbox |
| Application to Windows Explorer | Fixed Windows `explorer.exe` | User-selected existing folder | One path through `ArgumentList` | Absolute OS executable, `UseShellExecute=false`, no command-string composition | Source-inspected |
| Application to diagnostic ZIP | Whitelisted aggregate builder | Runtime state and selected output path | Aggregate settings/project/review/error/runtime/integrity metadata | Explicit user action, fixed entry set, privacy checks, temporary workspace cleanup, no upload | Synthetic diagnostic privacy coverage passes; Phase 10 must not confuse this user-created diagnostic ZIP with the product package ZIP |

## Entry and write surfaces

| Surface | Input/source | Current maximum or limit | Validation and write effect | Verification |
|---|---|---|---|---|
| Settings and app-store JSON | App data root | `AtomicJsonStore` default 128 MiB per store; semantic version/schema checks | Atomic temporary write, validation, replacement and `.bak`; credential material and duplicate/case-shadowed properties in primary/backup fail closed while safe unknown Boolean/null metadata round-trips | Security group and UI harness verify rejection/no-write/correction lifecycle, backup scanning, safe unknown-field preservation and exact credential-named Boolean rejection |
| Review-decision JSON recovery | Canonical local review root, optionally constrained beneath the trusted app reviews root | `AtomicJsonStore` bound; one primary and one `.bak` leaf | Recovery-capable reads first validate the root, hold a trusted write boundary over both leaves, copy and validate pinned backup bytes, commit with CAS and preserve the valid backup | Backup-only Workshop/outside-app cases leave primary/backup unchanged across workspace, previous, Activity, repository, Apply and RMK; trusted in-root recovery succeeds with notice and unchanged backup |
| Provider endpoint | Built-in profile or user-entered custom URL | URI parser; public UI/save/runtime require HTTPS | Forbid fragments and credential-bearing user-info/host/path/query/fragment after at most three decode passes; only `api-version`, `format=json`, and `version` query names are accepted with strict values | Security group, runtime/fake-handler checks and UI harness verify grammar, encoded variants, unchanged files and correction persistence through primary/backup cleanup |
| API key input | Settings text editor | UI text, parsed one key per line | Not serialized; copied into per-provider UI drafts and `AppServices.ApiKeys` for the current process | Source and exact-package zero/privacy audits used synthetic markers only; managed-memory lifetime remains SEC-006 and manual UI behavior remains unverified |
| Custom glossary | User-selected TXT/TSV/CSV/JSON | 16 MiB, 100,000 terms, 8,192 characters per field, JSON depth 64 | Read-only import; selected terms are placed in the provider system prompt | Security/resource groups cover file/term/field/depth limits; glossary builder self-test passes |
| General LanguageData XML | Mod/review/apply targets | 128 MiB pre-read; XML parser: 128 Mi characters, depth 256, 1,000,000 nodes | DTD and resolver disabled; apply uses validated atomic write and rollback | Security/resource/native groups cover pre-read, character, depth/node and output limits |
| Def XML and RMK XLSX/ZIP | Mod/RMK input | Workbook 256 MiB; 4,096 entries; per-entry 256 MiB; aggregate 512 MiB; 100:1 ratio; XML depth 256; XML nodes 2,000,000; Def traversal depth 256 | Rejects dangerous/duplicate names, split/Zip64 metadata, external/escaping relationships, active parts/content types and formula surfaces before read/update | Both native Phase 07 groups pass, including OLE/macro/VML/formula/custom-XML attacks, Excel's last valid row and rejected-update preservation |
| Provider response | Configured provider/Google | 4 MiB default; 180-second default timeout | Streaming bounded read, strict UTF-8/JSON shape and exact response-ID validation | Fake-response regressions cover byte, attempt, timeout, cancellation and malformed-response boundaries |
| Provider requests | Translation run | Four retries per operation by default; current source caps a run at 2,000 attempts and individual input by character/token limits | Backoff, key rotation/budgets, cancellation; failed multi-entry batches may be split and resent | Configured two-attempt stop and oversized-entry zero-transport fixture directly verified |
| Apply/RMK directory write boundary | Local apply and RMK export target root plus every target-parent chain | One transient guard per protected directory for the operation lifetime | Exclusive delete-on-close guard file, directory handle opened without delete sharing, canonical path and physical identity recheck; blocks directory Delete/Move until transaction completes | Apply and RMK hooks verified Delete/Move rejection, outside sentinel preservation, successful write and zero leftover guards |
| Transaction snapshot/rollback | Every distinct output target plus its backup | 16,384 snapshot/translation targets, preflighted before mutation | File/Missing/Directory/Unstable snapshots; identity+SHA-256 CAS for prepared bytes, target, backup and post-commit evidence; reparse/hardlink rejection; concurrent winner and recovery snapshots preserved | Archive/write-boundary group verifies directory removal/replacement/recreation, same-content/different-identity writers, absent-leaf reparse, target/backup/prepared/post-commit races and sync/async/LanguageData rollback |
| Local apply | User-selected local mod | Count determined by dry-run preview | Current source reread, source-change/status/token checks, explicit confirmation, app-owned target boundary, transactional rollback | Full regression plus Phase 07 directory/recovery boundary fixtures pass |
| RMK export | Confirmed work clone | Count determined by dry-run preview | Subscription is read-only; only confirmed work clone is written; XML/XLSX transaction and rollback | Full regression plus both native Phase 07 groups and review-recovery fixtures pass |
| Persistent log | App-owned log directory | Queue 4,096 lines; message 16,384 UTF-16 characters | Daily append-only file after credential/path redaction, control/bidi flattening and deterministic surrogate-safe truncation | Synthetic redaction covers raw/named/quoted/qualified/token-shaped and ambiguous/leading-ampersand values while preserving unrelated query fields; slow-writer and UI drain harnesses exit 0; retention gap SEC-008 remains |
| Diagnostic ZIP | User-selected `.zip` | Six fixed JSON entries; at most 50 review folders and 20,000 recent lines are summarized | Local-only creation; no raw logs/text/keys/absolute paths | Synthetic privacy coverage passes; no real user content or key was used, and no external upload occurs |

## Provider data disclosure

### OpenAI-compatible and custom providers

The application sends data only after the user accepts the translation preflight and starts AI translation. Dry-run and source-only analysis return before provider construction. A `Custom` profile or an endpoint with a different origin receives an additional default-No confirmation. Each HTTPS POST contains:

- the API key in the `Authorization: Bearer` header;
- the configured model and generation/response-format options;
- a fixed system prompt, selected built-in/custom glossary terms, and any additional user instructions;
- for each selected source entry: internal request `id`, localization `key`, `kind`, `defType`, `field`, and original `text`.

The request does not intentionally include a whole mod archive, absolute local paths, review notes, or unrelated project rows. Retries and binary batch splitting can send the same source again. Provider responses are parsed locally and candidate translations are stored in the local review run.

### Google fallback

When no API key is available, each source value is token-protected, split into chunks, and sent to the configured Google translation endpoint as an HTTPS GET with query parameters:

`client=gtx&sl=auto&tl=ko&dt=t&q=<source chunk>`

Google receives the source text in the URL query, not a request body. The app does not attach glossary terms, localization keys, Def context, review notes, or an API key to this request. A failed chunk can be sent again up to the configured retry count (default four attempts), and therefore Google and its infrastructure may retain repeated query URLs under their own policies.

### Custom endpoint responsibility

- The application accepts a user-entered HTTPS host; it does not prove ownership, reputation, privacy terms, geographic processing location, retention, model behavior, or price.
- Automatic redirects and cookies are disabled, and endpoint credentials are rejected, but DNS/proxy routing and the endpoint operator remain outside the trust boundary.
- The translation preflight shows the destination host. A `Custom` profile or origin change also receives a default-No warning about source/context/glossary/key transfer. A same-origin path edit to a built-in endpoint does not receive that second warning, so the user must still verify the full URL in Settings.
- Before starting translation, the user must verify the full endpoint and provider/model, confirm that sending the listed source/context/glossary is permitted, and understand the provider's privacy, retention, rate-limit, and billing rules.

## RMK Builder containment boundary

The RMK Builder process is deliberately contained but is **not sandboxed**. The app pins the canonical selected executable's identity, length and SHA-256, starts it suspended, assigns a kill-on-close job before resume, inherits only standard streams, filters the child environment, bounds captured output, enforces a 120-second timeout and transactionally validates/rolls back `LoadFolders.xml` and `ModList.tsv`.

The executable still runs with the interactive user's filesystem and network authority. Only that EXE is authenticated; adjacent DLL/config files and the rest of the selected work clone are not individually authenticated. The user must trust the complete RMK work clone before allowing Builder execution.

## Privacy disclosure

- Telemetry and automatic upload: none is implemented. The app does not automatically upload logs, diagnostics, projects, or usage analytics. Provider requests occur only when the user starts AI translation.
- API-key persistence and lifetime: keys are not written to settings, project/review files, logs, or diagnostic ZIPs. They remain as immutable managed strings in the active Settings input/draft and process-level provider dictionary until replaced/removed or the process exits. Hiding the editor after 15 seconds only masks the display; it does not erase managed memory. Secure zeroization is not implemented.
- Local review data: source, translation, notes, status, and history are intentionally stored under `%LOCALAPPDATA%\RimWorldAiTranslator\reviews` until the user deletes them.
- Log contents and location: `%LOCALAPPDATA%\RimWorldAiTranslator\logs\RimWorldAiTranslator-YYYYMMDD.log`. Logs contain local time, level, operation/status summaries, counts, selected project/mod display names in some events, and bounded error category/type information. Credentials and absolute Windows paths are redacted, control/line-separator characters are flattened, and long messages are truncated.
- Log retention: no 14-day or other automatic retention cleanup is implemented. Daily log files remain until the user deletes them.
- Diagnostic bundle contents: six aggregate JSON files covering manifest/runtime/read-error types, settings/provider configuration categories, project/language counts, review status/origin counts, error-category counts, and product file presence/size/hash/version.
- Diagnostic exclusions: source and translation text, localization keys, API keys/auth headers, raw logs, full provider URL/host, RMK path, absolute local paths, project names, and review notes are not intentionally included. The ZIP is saved locally to the path chosen by the user and is never automatically uploaded or automatically deleted.

## Deletion procedure

Close the application before manual deletion so the current log and save queues cannot recreate files.

- Settings: delete `%LOCALAPPDATA%\RimWorldAiTranslator\settings.json` and `settings.json.bak`. The app uses defaults when those files are absent and may create a settings file only after an accepted settings write; the exact cold-start smoke did not force-create one. API keys were not stored there.
- Reviews: use the in-app project deletion command to remove that project's app-owned review folders while preserving the source mod, or delete `%LOCALAPPDATA%\RimWorldAiTranslator\reviews` after closing the app. Project deletion updates the active project store but its previous record/name can remain in `projects.json.bak` and the success log; use the full local reset for complete removal.
- Logs: delete individual files or the entire `%LOCALAPPDATA%\RimWorldAiTranslator\logs` directory after closing the app.
- Full local reset: after closing the app, delete `%LOCALAPPDATA%\RimWorldAiTranslator`. This does not remove diagnostic ZIPs/quality reports saved elsewhere, translations already applied to a local mod, or exports written to an RMK work clone; those must be removed separately.

## Threats and residual risk

| ID | Threat | Likelihood | Impact | Mitigation | Verification | Residual risk |
|---|---|---:|---:|---|---|---|
| T-01 | API key persists in settings/logs/diagnostics | Low | High | Explicit settings model excludes keys; primary/backup extension data is scanned; logger redacts; diagnostics use an aggregate allowlist | Safe Boolean/null metadata survives, exact credential fields and duplicate shadows fail closed, UI correction persists through backup cleanup, and the source scan finds no actual secret | Keys remain readable in current-process memory |
| T-02 | Malicious/incorrect endpoint receives source or key | Medium | High | Explicit configuration, HTTPS, strict endpoint grammar, credential-bearing URL rejection, redirect/cookie disablement, privacy/cost disclosure | Encoded credential variants, fragment/query rules, zero-transport rejects and UI/runtime/save paths are directly verified | No host allowlist; user/provider trust decision remains |
| T-03 | Provider retry amplifies disclosure/cost | Medium | Medium | Per-operation retry, backoff, key budget, cancellation, current run-wide attempt cap | Exact two-attempt stop verified | Retries/splits intentionally resend data |
| T-04 | Oversized/malformed local input exhausts memory/CPU | Medium | High | Bounded/streaming JSON, glossary, XML, artifact, discovery, diagnostic and XLSX/ZIP surfaces plus structural and aggregate limits | `Phase07.SecurityHardening`, `Phase07.ResourceBoundaries` and both native groups directly cover individual/aggregate/pre-allocation/output limits | Limit values still permit substantial but bounded work |
| T-05 | Path traversal/reparse or directory replacement writes outside target | Low | Critical | Canonical/physical path checks, root ownership, reparse/hardlink rejection, exclusive directory guard/handle, preview/confirmation and CAS rollback | Apply/RMK Delete/Move, absent-leaf reparse, directory replacement and outside-sentinel fixtures pass with no residue | TOCTOU cannot be reduced to zero on a fully compromised local host |
| T-06 | XXE/entity expansion | Low | High | DTD prohibited, resolver null, entity/document/depth/node limits | Managed/native excessive-depth, DTD and stylesheet rejection regressions pass in post-fix 82/82 and three clean final v5 package runs | No parser exemption claimed |
| T-07 | ZIP Slip/bomb/external or active XLSX content | Medium | High | Entry/count/size/ratio/central-directory/split/Zip64 checks; external/escaping relationships and active/content-type/formula surfaces rejected | Native groups plus eight Phase 10 per-entry Zip64/split/malformed-extra/count/trailing fixtures cover reader/update and byte-preservation boundaries | Passive package metadata remains data and must stay within the documented allowlist |
| T-08 | Provider error/log injection leaks or forges records | Medium | Medium | Structured safe summaries, credential/path redaction, control/bidi flattening and 16,384-character cap | Synthetic raw/named/quoted/qualified and ambiguous/leading-ampersand values, CR/LF/control/bidi and surrogate-safe truncation pass; slow logger drain is responsive | Project display names and operation metadata are intentionally logged; retention is manual |
| T-09 | Diagnostic ZIP leaks raw user content | Low | High | Fixed aggregate schema, no raw logs/text, privacy scan, explicit local save | Synthetic diagnostic privacy coverage passes; no real data/key was used | OS/runtime/culture and aggregate counts remain identifying metadata |
| T-10 | RMK child process runs attacker code | Low | High | Canonical EXE identity/length/hash pin, suspended start, kill-on-close job before resume, standard-stream-only inheritance, filtered environment, bounded output/time and transactional generated-file validation | Hash-swap, failed-job-assignment, descendant-containment and process-transaction fixtures pass; bounded output/time controls are source-confirmed | Not a sandbox: the EXE runs with user file/network authority and adjacent clone content is not individually authenticated |
| T-11 | Retained local files expose activity/history | Medium | Medium | Local-only storage and documented deletion | Documentation complete; no automatic log retention | User must delete logs/reviews/exports manually |
| T-12 | Unlicensed official-derived content or incomplete runtime notices are redistributed | High for glossary until resolved | High/legal | Exclude the official-derived glossary; include and manifest-bind exact .NET notices; do not publicly distribute until SEC-009/parity is resolved | SEC-010 PASS for exact RC; SEC-009 OPEN with glossary excluded | Technical controls can prevent disputed bytes from shipping but cannot establish rights or restore omitted Golden behavior |
| T-13 | A recovery-capable review read writes under Workshop or outside app storage | Low | High | Canonical local/trusted reviews-root validation and a held primary/backup write boundary before backup recovery | Backup-only denied-root fixtures across all consumers produce no primary/corrupt output; trusted recovery preserves backup and emits notice | A compromised administrator/kernel remains outside the threat model |
| T-14 | Rollback overwrites a concurrent save or restores an unsafe directory/leaf | Low | High | File/Missing/Directory/Unstable states, 16,384-target preflight, identity+hash CAS, reparse/hardlink checks and explicit recovery preservation | Same-content/different-identity, target/backup/prepared/post-commit and directory-state races pass | Recovery artifacts may require explicit user action when a legitimate concurrent save wins |

## Supply-chain and legal inventory

- `dotnet list RimWorldAiTranslator.sln package --include-transitive` reported no direct or transitive framework packages for all eight projects. The application still depends on project references, the .NET SDK/runtime, Windows platform libraries, and repository native code.
- Current active PowerShell file count is 0 and raw `<PackageReference>` count is 0.
- The local package is self-contained/single-file and therefore redistributes .NET runtime components even though they are not separate NuGet application dependencies.
- The exact 14-file package includes the project `LICENSE`, `THIRD_PARTY_NOTICES.md`, two .NET third-party notice files and two .NET license files; all are bound by the final manifest.
- `glossary.generated.ko.json` identifies 3,833 observations as `official-core` or `official-dlc-*` and contains RimWorld-derived source/Korean text. Redistribution rights or a parity-preserving independently licensed replacement have not been established. Exclusion from the exact RC is implemented, but it does not restore bundled suggestions or glossary-bearing request parity.
- The official-derived glossary is excluded from the exact RC. SEC-010 is resolved for that package, but SEC-009 rights and the resulting bundled-suggestion/request-parity gap remain OPEN; the RC must not be described as overall PASS or publicly distributable.
- Current toolchain inventory: SDK 8.0.422, MSBuild 17.11.48, host 10.0.1, and installed `Microsoft.NETCore.App`/`Microsoft.WindowsDesktop.App` 8.0.28; `global.json` pins 8.0.422 with `latestPatch` and disallows prerelease SDKs.
- `NuGet.config` clears all package sources. `dotnet list ... --vulnerable` fails with `Attempted to divide by zero` on the empty graph and is recorded N/A, not PASS. Official Microsoft servicing/support evidence identifies .NET 8.0.28 as the current supported .NET 8 release through 2026-11-10. The Phase 10 package/license scan is complete; Phase 04 and Phase 09 ZIPs are historical evidence.
