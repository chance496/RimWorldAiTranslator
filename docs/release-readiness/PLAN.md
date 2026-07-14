# Public Release Execution Plan

## Objective and fixed boundaries

- Current user-authorized integration override (2026-07-14): after the compatibility/startup repair passes its relevant gates, commit and normally push `main`, verify candidate containment, then delete `origin/codex/csharp-migration`. PR, tag, Release, asset, metadata, Actions dispatch and deployment remain prohibited.

- Golden Master: `4c7d11b49126ba3987e9d49bd16944d4376ba0bc`.
- Candidate basis: `891d135bb9b37e7d56dd4c29336bb20d277841bc` plus the preserved pre-existing working tree and evidence-backed local Phase 04-10 fixes.
- Goal: a local self-contained `win-x64` C# build with active product/build/test/package PowerShell 0 and Golden behavior/data/UI compatibility, integrated into `main` under the current user authorization.
- Out of scope/prohibited: PR, tag, Release, asset, metadata, Actions dispatch, upload beyond the authorized source push, paid or real provider API, real user/Workshop/RMK-subscription mutation, signing, display/system setting changes, restart and public distribution. Existing `v1.0.0` is immutable.
- The user authorized one computer shutdown only after genuine completion or terminal stop. It is the final operational action, not a quality gate.

## Ordered phase plan

| Phase | Required result | Status |
|---|---|---|
| 0-2 Prepare/inventory/plan | Read governance, preserve dirty tree, establish isolated Golden/current workspaces and evidence-first gap plan | PASS |
| 3 Baseline/parity | Golden tests/UI and feature/UI/gap matrices | PASS |
| 4 C# migration/PowerShell zero | C# replacements, architecture separation, active/exact-package script count 0 | PASS |
| 5 Data/translation | Read-only and round-trip JSON/XML/XLSX, fault rollback, canonical fake-provider parity | PASS |
| 6 UI/accessibility | Matched screens/states, interaction/accessibility/keyboard/close evidence | PASS automated; manual host matrix retained |
| 7 Security/privacy | Secret/log/HTTP/path/XML/XLSX/ZIP/process/supply-chain audit | PASS automated; SEC-009 retained |
| 8 Reliability/performance/tests | Failure/cancel/restart/kill/race and 5,000-row performance; repeated full suite | PASS measured automated subset / BLOCKED completeness |
| 9 Local RC/docs | Reproducible self-contained local package, smoke, notices, manual checklist | PASS automated; Phase 09 artifact later superseded |
| 10 Independent final audit | At least 20% feature sample, all critical boundaries, post-fix full suite/package smoke/performance/security and final report | PASS automated / BLOCKED final |

## Phase 10 closure

1. Read `10_INDEPENDENT_AUDIT_AND_FINAL_GATE.md` immediately before work. **DONE**
2. Independently sample 22/54 feature rows and challenge storage, Apply/RMK, key/log, path/XML/XLSX/ZIP, cancel/restart, PowerShell and package boundaries. **DONE**
3. Repair the three sampled P1 findings (mutable preview plan, incomplete recovery-notice presentation and incomplete native per-entry central-directory rejection), then close the independently discovered identical-source safety, PID-reuse ancestry and transient exact-cleanup defects. **DONE**
4. Rerun focused tests, strict Release, the full 82-test regression, five UI performance runs, exact-package benchmark, three isolated package builds/smokes, final package UI challenges and allowlist/hash/Defender/secret/runtime/v1/residue audits. **DONE**
5. Bind the frozen package-build input, exact package and accepted UI evidence, synchronize readiness documents and issue the required `PASS`/`FAIL`/`BLOCKED` report. **DONE; overall `BLOCKED`**
6. Schedule the user-authorized shutdown only after the final report and last consistency checks are complete. **READY; this is the final operational action**

## Final automated evidence

- Frozen package-build input snapshot v5 captured before final reporting: 250 files, combined SHA-256 `C279F6F6C87634D95035C3D17821770DE0703649CCD33A3A55B123695683A006`.
- Post-package scope audit: subsequent differences are confined to release-readiness governance/reporting documents; product, test, tooling, resources, version, solution, project and package-bound drift is 0. Evidence: `phase10/final-post-package-governance-drift-v5.txt`.
- Exact RC ZIP: 66,254,006 bytes, SHA-256 `2302E87747F55348EA5EF96E2D352686ADFD943BB82954C9DA1F789553BBB7C1`.
- Final run-3 manifest: 2,652 bytes, SHA-256 `E9F8EAE6007C4351125AD47A89B6BE32BB1357354A2BA1924DAFC18D4381C707`; normalized manifest SHA-256 `F61FF7E69E8D564AD32544C902057730EE57B567BA50FA4B63A0C58FA9990B77`.
- EXE: 163,800,354 bytes, SHA-256 `9EEB6A80DD51DCAFD31D2516CFB6F1C70257CEDD94783C360ED58FEA3FE919C8`.
- One separate post-PID-fix tooling self-test passed 18/18. Three clean runs each passed eight-project strict 0/0, 82/82, glossary self-test, cold/warm/duplicate smoke and cleanup, with byte-identical ZIP/EXE/14 files.
- Five post-fix UI reports and all 12 automated performance/package-size checks pass. Defender exact ZIP/extract threat count is 0 at scan time. Active/exact-package PowerShell count and package credential hits are 0.

## Final blockers and ownership

| Blocker | Why it cannot be closed locally | Owner/next action |
|---|---|---|
| SEC-009 glossary rights and affected parity | No redistribution grant; excluded asset removes bundled suggestions and glossary-bearing request parity | Rights holder/user legal authority: license or replace, then rerun parity/package gates |
| Anonymized existing project/review reopen | Requires a disposable anonymized copy and human confirmation of translations, notes, statuses and history across edit/save/close/reopen | User/independent tester; MRC-004 |
| Clean PC/no SDK and SmartScreen | Requires a separate supported Windows host and human observation | User/independent tester |
| Actual Excel | Requires supported Microsoft Excel and disposable anonymized workbook | User/independent tester |
| 150/200%, mixed DPI, Narrator/NVDA/OS high contrast | Requires display/assistive environment changes prohibited in this run | User/independent tester |
| Native dialogs, dummy-key reveal/copy and visual acceptance | Requires physical/human judgment | User |
| Equivalent Golden process performance and project/Apply/RMK cancellation/forced-exit breadth | Current measurements deliberately do not equate non-equivalent process boundaries or cover every packaged mutation window | Independent synthetic automation/tester |
| Phase 10 minimum end-to-end manual flows | Offline/mock translation, copied Apply/rollback, RMK copy import/export and visible error/cancel/no-stall behavior require human workflow confirmation | User/independent tester |

## Completion contract

- Final `PASS` requires every mandatory automated and required authority/environment/human gate.
- `FAIL` means a locally executable mandatory gate still fails.
- `BLOCKED` means locally executable gates pass but a named authority/environment/human condition remains. The current expected and evidence-backed final verdict is **BLOCKED**.
- No publication operation is authorized by completion of this plan.
