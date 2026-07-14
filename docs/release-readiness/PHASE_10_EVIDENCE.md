# Phase 10 Independent Audit Evidence

## Scope and verdict

- Golden Master: `4c7d11b49126ba3987e9d49bd16944d4376ba0bc`.
- Candidate basis: HEAD `891d135bb9b37e7d56dd4c29336bb20d277841bc` plus the preserved local working tree and explicit Phase 04-10 fixes.
- Frozen package-build input snapshot v5 captured before final reporting: 250 files, combined SHA-256 `C279F6F6C87634D95035C3D17821770DE0703649CCD33A3A55B123695683A006`. The artifact-excluded post-package audit records governance-only reporting changes and zero product/test/tool/package-bound drift.
- Automated Phase 10 gate: **PASS after remediation**.
- Overall final verdict: **BLOCKED**.
- Public distribution approval: **NOT GRANTED**.

## Independent sample

The risk-weighted sample covered 22 of 54 feature rows (40.7%): `LCH-001`,
`PROJ-004`, `PROJ-005`, `EXT-001`, `EXT-002`, `EXT-004`, `TRN-002`, `TRN-003`,
`TRN-004`, `TRN-006`, `REV-002`, `REV-003`, `REV-004`, `REV-006`, `APP-002`,
`APP-003`, `APP-004`, `RMK-003`, `RMK-005`, `STATE-001`, `STATE-002`, and
`PKG-003`. It also rechecked 100% of the required storage/backup/rollback,
Apply/DryRun/RMK, key/log, path/XXE/ZIP, cancel/restart, PowerShell-zero and package
manifest/EXE/child-process boundaries.

### Findings and repairs

1. **ISSUE-102 / P1 — preview plan drift.** Apply/RMK could execute a newly
   calculated plan after the user confirmed a preview; local Apply also failed to
   increment `SafeCandidateRows` and the UI omitted exclusion details. The repair
   computes a deterministic SHA-256 over roots/options/counts/exclusions and exact
   decision/source/output/workbook baselines, passes it from preview into execution,
   rejects drift before writing and revalidates output baselines under the write
   boundary. Eight focused cases prove unchanged success and decision/source/output/
   workbook drift failure before mutation.
2. **ISSUE-103 / P1 — recovery notice truth/privacy.** The UI could lose the exact
   corrupt-copy filename and some action paths could leave notices queued. The repair
   centrally drains all action completions and presents exact bounded target/corrupt
   leaf names without a parent path. A dedicated probe covers single, multiple,
   no-copy and control-character cases.
3. **ISSUE-104 / P1 — native archive entry metadata.** Aggregate archive checks did
   not reject every per-entry Zip64 sentinel/extra, split-disk, malformed-extra,
   count or trailing inconsistency. Eight hostile fixtures now challenge reader and
   writer-update paths; every rejection preserves source bytes and leaves no backup
   or temporary output.
4. **ISSUE-106 / P1 — identical-source bulk safety.** User confirmation alone could
   propagate an unsafe source translation or write to a structurally unsafe target.
   The repair requires both the source and every target to pass the common
   `ReviewSafety` policy and preserves unsafe/unrelated state and history.
5. **ISSUE-107 / P1 tooling — PID-reuse ancestry.** A stale numeric Toolhelp parent
   PID could classify an unrelated older system process as an application descendant.
   The repair validates every parent-child edge with process creation identity and
   fails the package gate closed when identity lookup fails.
6. **ISSUE-108 / P1 reliability — transient exact-cleanup rename.** A run-owned RMK
   stage quarantine rename could fail once with Win32 5 and preserve the owned root.
   The repair retries only errors 5/32/33, at most eight times, while revalidating the
   pinned parent/root identity and quarantine absence before every attempt.

No P0 finding remained. The first focused compile/probe exposed an unavailable
hex-conversion API and a wording assertion; both were fixed without weakening the
contract. Superseded failure logs remain in the evidence root.

## Build, regression and flakiness

| Gate | Result | Evidence |
|---|---|---|
| Post-fix strict Release | eight projects, warnings 0, errors 0 | `phase10/final-v5-strict-release-build.log` |
| Post-fix standalone suite | 82/82 | `phase10/final-v5-full-suite.log` |
| Final tooling self-test | 18/18 in one separate post-PID-fix run | `phase10/package-process-identity-tooling-self-test-rerun.log` |
| Clean package run 1 | strict 0/0; 82/82; glossary; smoke; cleanup | `phase10/package-final-v5-final-run-1.log` |
| Clean package run 2 | strict 0/0; 82/82; glossary; smoke; cleanup | `phase10/package-final-v5-final-run-2.log` |
| Clean package run 3 | strict 0/0; 82/82; glossary; smoke; cleanup | `phase10/package-final-v5-final-run-3.log` |
| Consecutive full-suite requirement | PASS x3 at the final package boundary | same three logs |
| Flaky tests | 0 observed | same three logs and reproducibility comparison |

The earlier 80/80 and 81/81 logs establish superseded audit checkpoints only. The
final claim is the post-fix 82/82 result repeated in all three isolated package roots.

## Exact local RC

- ZIP: `dist/RimWorldAiTranslator-v1.0.1-rc.1-win-x64.zip`
  - size: 66,254,006 bytes
  - SHA-256: `2302E87747F55348EA5EF96E2D352686ADFD943BB82954C9DA1F789553BBB7C1`
- Manifest: `dist/RimWorldAiTranslator-v1.0.1-rc.1-win-x64.manifest.json`
  - size: 2,652 bytes
  - final run-3 SHA-256: `E9F8EAE6007C4351125AD47A89B6BE32BB1357354A2BA1924DAFC18D4381C707`
  - normalized SHA-256: `F61FF7E69E8D564AD32544C902057730EE57B567BA50FA4B63A0C58FA9990B77`
- EXE:
  - size: 163,800,354 bytes
  - SHA-256: `9EEB6A80DD51DCAFD31D2516CFB6F1C70257CEDD94783C360ED58FEA3FE919C8`
- Payload: exactly 14 top-level allowlisted files, 164,119,892 extracted bytes.
- Identity: Windows GUI x64; FileVersion `1.0.1.0`; ProductVersion
  `1.0.1-rc.1`; `asInvoker`; `uiAccess=false`; PerMonitorV2; Authenticode
  `NotSigned`.

All three runs produced byte-identical ZIP, EXE and 14 payload files. The initial
raw-manifest identity check is retained as a failure because truthful `createdUtc`
differs. After omitting only that field, manifests are identical. Evidence:
`phase10/package-reproducibility-v5-final.json`.

## Package smoke, processes and residue

| Metric | Run 1 | Run 2 | Run 3 |
|---|---:|---:|---:|
| Cold first usable | 1,932.323 ms | 2,004.372 ms | 2,072.266 ms |
| Warm first usable | 638.733 ms | 631.047 ms | 688.868 ms |
| Duplicate dialog | 138.611 ms | 122.699 ms | 129.595 ms |
| Cold/warm exit | 0 / 0 | 0 / 0 | 0 / 0 |
| Duplicate exit | 2 | 2 | 2 |
| Descendants after exit | 0 | 0 | 0 |
| Smoke temp removed | true | true | true |

Final v5 residue status is PASS: product/build process 0, `dist/_package-*` 0,
package-smoke/RwatPkg root 0, transaction journal/backup 0 and coordination file
exclusively openable. The initial final-v5 audit found three exact roots owned by a
failed current-session package attempt; each root/prefix/GUID/reparse/process boundary
was validated before exact removal. A pre-existing synthetic test root was preserved.
See `phase10/final-v5-residue-audit.txt`.

## UI, accessibility and performance

- Historical visual contract: Golden/candidate 15/15, actual-device 125% 20/20,
  75 comparisons and all structural counters 0.
- Historical pre-local-gap evidence index: 268 files; manifest SHA-256
  `CD0BB87BDEFED113DF05CDA1DCE4E7D088ACCC792DB182D6A8873FEF23FF4E6D`;
  aggregate `163DF5A0171645A7651AFB83DF6930C800A0617A806128C9AA75963CB7F20B01`.
- Post-fix current UI: five independent 5,000-row reports PASS; search median
  175.847 ms, status-filter median 68.502 ms and Stop-feedback worst 12.850 ms.
- Recovery notice presentation probe and the in-process local-gap v2 probe: PASS.
- Exact v5 package probes: Korean+space path, startup-fault/recovery and multilingual
  language-dialog cancel/no-write: PASS. `final-ui-evidence-binding-v5.txt` binds these
  probes to the v5 snapshot, exact ZIP and EXE.
- Supplemental exact-v5 packaged idle probe: five 1-second samples after readiness
  remained responsive at 77,262,848-77,287,424 working-set bytes and
  19,906,560-19,910,656 private bytes, handle count 410; normal exit 0 and exact temp
  cleanup passed. Evidence: `phase10/final-v5-packaged-idle-working-set.json`.
- Final benchmark: all 12 automated checks and package-size gate PASS. Top-level
  evidence completeness intentionally remains BLOCKED for non-equivalent/manual
  boundaries.

Actual 150/200% and mixed-DPI, Narrator/NVDA/OS high contrast, native confirmation/
recovery dialog perception and focus, dummy-key reveal/copy, SmartScreen and
subjective Golden visual acceptance remain manual.

## Data and translation compatibility

- Read-only opening, unknown-field preservation, explicit migrations, strict UTF-8,
  semantic backup recovery, atomic validated replacement and rollback are covered.
- XML encoding/newline/comments/order/supplementary Unicode and invalid-control/DTD/
  depth/resource paths are covered.
- XLSX stable rows, unknown columns, styles/comments/passive parts, Required Mods,
  legacy header upgrade, active/formula rejection, failure byte preservation and
  5,000-row read/update/export are covered. Actual Excel open remains manual.
- Review identity uses exact comparison SHA-256 plus canonical target; stale/whitespace
  source changes, keyless v6 and legacy quarantine are covered.
- Exact same-input Golden system/user prompt hashes and semantic canonical request,
  fake response equality, provider status/shape/budget/retry, one/multi/split and
  cancel/resume batches are covered without a real API. Provider-family and batch
  sampling is recorded in `phase10/provider-batch-sampling.md`.
- The official-derived glossary is excluded; exact packaged glossary-bearing default
  request parity remains BLOCKED under SEC-009.

## Security, privacy and supply chain

- Exact archive allowlist, entry paths and every manifest hash: PASS.
- Active source and exact package PowerShell count: 0. The unstaged index still lists
  the two legacy scripts as tracked-deleted; no staging is authorized.
- Credential scan: four source pattern-hit files, all synthetic fixtures; unexpected/actual
  secret 0; package hit 0.
- Dependency graph: zero direct/transitive `PackageReference`. Cleared-source
  `dotnet list --vulnerable` exits with an empty-graph divide-by-zero bug and is N/A,
  not PASS.
- Embedded runtime: official Microsoft servicing/support pages identify .NET 8.0.28
  as the current supported .NET 8 release through 2026-11-10. Four exact runtime
  notices/licenses are manifest-bound. See `phase10/runtime-advisory-verification.md`.
- Defender: exact ZIP and extracted payload threat count 0 with the installed engine
  and signature recorded in `final-v5-defender-scan.txt`; point-in-time only.
- Existing v1.0.0: content and ZIP SHA-256 `928C...351` unchanged.
- Critical/High unresolved technical findings: 0. RMK Builder remains a disclosed
  selected-clone trust residual, not a sandbox. SEC-009 remains a legal/functional
  release blocker.

## Git and external action audit

- Branch `codex/csharp-migration`; HEAD unchanged at
  `891d135bb9b37e7d56dd4c29336bb20d277841bc`; staged changes 0.
- Existing dirty-tree changes were preserved. No reset, restore, clean, staging,
  commit, amend or history rewrite was performed.
- Push, PR, tag, Release, asset, repository metadata, GitHub Actions, external upload,
  paid/real provider API and public deployment: **0 / NO**.
- No real user data, Workshop content, RMK subscription content or real API key was
  used as a fixture.

## Final blockers

1. SEC-009 redistribution authority or independently licensed replacement, followed
   by bundled-suggestion and canonical request/package parity reruns.
2. An anonymized disposable copy of an existing project/review opened, edited,
   saved, closed and reopened with translations, notes, statuses and history checked.
3. Clean supported Windows 10/11 x64 machine without repository or .NET SDK.
4. Actual Microsoft Excel open/save of the disposable synthetic workbook.
5. Actual 150/200% and mixed-DPI monitors; Narrator/NVDA and OS high contrast.
6. Native dialog/default-No/focus, dummy-key reveal/copy, subjective Golden visual
   acceptance and unsigned SmartScreen behavior.
7. Phase 10 minimum manual end-to-end flows: offline/mock translation and review,
   copied-target DryRun/apply/rollback, disposable RMK import/export, and visible
   error/cancel/no-stall behavior.
8. Packaged-process forced-exit/recovery breadth across representative Apply/RMK
   mutation windows using only disposable synthetic targets.
9. Equivalent Golden process/operation performance boundaries and measured
   project/Apply/RMK cancellation completion timing; narrower Core/fake-HTTP samples
   are not substituted for these claims.

These are authority/environment/human or evidence-completeness conditions, so the
correct final result is `BLOCKED`, not `PASS` or `FAIL`. The local RC must not be
publicly distributed.
