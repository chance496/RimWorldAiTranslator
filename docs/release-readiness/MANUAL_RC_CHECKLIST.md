# Manual RC Checklist

Exact candidate: `dist/RimWorldAiTranslator-v1.0.1-rc.1-win-x64.zip`  
Frozen input: 250-file v5 snapshot, SHA-256 `C279F6F6C87634D95035C3D17821770DE0703649CCD33A3A55B123695683A006`  
ZIP: 66,254,006 bytes; SHA-256 `2302E87747F55348EA5EF96E2D352686ADFD943BB82954C9DA1F789553BBB7C1`  
Manifest: 2,652 bytes; final run-3 SHA-256 `E9F8EAE6007C4351125AD47A89B6BE32BB1357354A2BA1924DAFC18D4381C707`; normalized SHA-256 `F61FF7E69E8D564AD32544C902057730EE57B567BA50FA4B63A0C58FA9990B77`  
EXE: 163,800,354 bytes; SHA-256 `9EEB6A80DD51DCAFD31D2516CFB6F1C70257CEDD94783C360ED58FEA3FE919C8`

Automated current-host evidence is distinguished from checks that require another machine or a human. Do not turn a pending row into PASS from screenshots, source tests or the current development host alone.

| ID | Check | Current status | Required disposition |
|---|---|---|---|
| MRC-001 | Exact ZIP extracts to a new Korean-and-space path; EXE starts directly and exits normally | PASS AUTOMATED CURRENT HOST | Reconfirm on clean PC |
| MRC-002 | No console/PowerShell child; zero descendants and zero product processes after exit | PASS AUTOMATED CURRENT HOST | Reconfirm on clean PC |
| MRC-003 | Clean supported Windows 10/11 x64 machine with no repository or .NET SDK | BLOCKED: clean-machine manual test required | User/independent tester |
| MRC-004 | Open an anonymized copy of an existing project/review, verify translations, notes, statuses and history, edit, save, close and reopen | BLOCKED: manual copied-data test required | User with disposable anonymized copy |
| MRC-005 | Open/save the synthetic RMK workbook with actual supported Microsoft Excel and verify no repair warning or content loss | BLOCKED: actual Excel required | User/independent tester |
| MRC-006 | Run at actual 150% and 200% DPI and move between mixed-DPI monitors | BLOCKED: display/multi-monitor manual test required | User; see Phase 06 checklist |
| MRC-007 | Navigate with Narrator or NVDA and actual Windows high contrast; verify names, roles, status announcements and no focus trap | BLOCKED: assistive-technology manual test required | User; see Phase 06 checklist |
| MRC-008 | Keyboard-only workflow and native Apply/delete/recovery/dirty-close/cancel/retry dialogs; verify default-No, ownership and focus return | BLOCKED: physical interaction required | User |
| MRC-009 | With only dummy keys, confirm masked-key reveal timeout, deactivation hiding, right-click/Ctrl+C behavior and safe-field copy | BLOCKED: manual clipboard/privacy check required | User; never use a real key |
| MRC-010 | Observe unsigned SmartScreen behavior and verify the README is sufficient for install, first run and core offline workflow | BLOCKED: clean-PC/user acceptance required | User |
| MRC-011 | Compare the main screens and workflow subjectively with Golden Master | BLOCKED: user visual acceptance required | User |
| MRC-012 | Establish compatible redistribution authority or an independently licensed replacement for the 3,833 official-derived glossary observations, then rerun glossary feature/request parity and all package gates | BLOCKED: user/legal authority required (SEC-009) | Rights holder/user legal decision |
| MRC-013 | Signing identity, final version/tag and public release | OUT OF SCOPE / NOT APPROVED | Requires a separate explicit user authorization |
| MRC-014 | Obtain an authoritative current support/advisory disposition for the exact redistributed .NET 8.0.28 runtime; do not treat the cleared-source `--vulnerable` command failure as PASS | PASS PHASE 10: Microsoft lists 8.0.28 as the current .NET 8 servicing release and support through 2026-11-10; empty application package graph remains N/A | Reverify after any runtime/package change |
| MRC-015 | With synthetic fixtures and no external provider, complete the offline/mock translation flow, review translation/notes/status, search/filter, save, exit and relaunch | BLOCKED: Phase 10 minimum manual end-to-end flow not run | User/independent tester; never use a real key |
| MRC-016 | On a disposable copied target, inspect DryRun counts/exclusions, apply, verify backup, exercise rollback/recovery and confirm the original source remains unchanged | BLOCKED: copied-target manual Apply flow not run | User/independent tester; never target Workshop/subscription content |
| MRC-017 | Import/export a disposable synthetic RMK working clone, reopen its XML/XLSX outputs and verify notes/status/history/Required Mods | BLOCKED: RMK copy end-to-end manual flow not run | User/independent tester |
| MRC-018 | Trigger synthetic errors and cancellation during project load, translation, Apply and RMK work; record cancellation completion timing and verify useful messages, no false completion, visible feedback/no UI stall, restart recovery and zero post-exit processes | BLOCKED: operation-specific error/cancel/timing manual flow not run | User/independent tester |
| MRC-019 | With disposable synthetic targets, force-exit the packaged app at representative Apply/RMK mutation windows and verify exact backup/rollback/restart recovery | BLOCKED: packaged forced-exit breadth not run | Independent tester/automation; preserve all unknown roots |
| MRC-020 | Repeat Golden and candidate performance at genuinely equivalent process/operation boundaries, fixture and host environment, or retain a documented non-equivalence blocker | BLOCKED: equivalent Golden process comparison unavailable | Independent performance tester |

## Safety constraints

- Use only synthetic fixtures or anonymized copies in a disposable local directory.
- Do not use a real API key or permit an external provider request.
- Do not point Apply/RMK export at Workshop or an RMK subscription copy.
- Do not upload the RC or evidence to an online scanner.
- Do not change public GitHub state while completing this checklist.

## Result

- Tester/date/environment: pending
- Overall manual result: **BLOCKED**
- Observed defects: none entered yet
- Public release approval: **NOT GRANTED by this checklist**
