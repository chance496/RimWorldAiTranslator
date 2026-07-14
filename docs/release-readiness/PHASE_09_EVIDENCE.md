# Phase 09 Local RC Evidence

> Historical checkpoint only. Phase 10 fixes rebuilt the same `dist` RC path, so
> the hashes and 80/80 results below accurately describe the Phase 09 artifact but
> do not identify the current file. Use `PHASE_10_EVIDENCE.md` and `FINAL_REPORT.md`
> for the current 82/82 artifact and final hashes.

## Scope and verdict

- Phase 09 automated local-package gate: **PASS**.
- Overall RC/public-quality verdict at Phase 09: **BLOCKED**.
- Public release approval: **NOT GRANTED**.
- No push, PR, tag, Release, asset change, GitHub Actions run, repository-metadata change, external upload, paid API call, real provider call, real API key, or real user/Workshop/RMK subscription data was used.

The remaining blockers are SEC-009 (official-derived glossary rights and the resulting Golden glossary/request-parity gap), a clean Windows machine without the SDK/repository, actual Microsoft Excel, actual 150%/200% and mixed-DPI behavior, screen reader/OS high contrast/physical keyboard and user visual/SmartScreen acceptance. The cleared-source vulnerable/deprecated commands were inconclusive, so authoritative exact-runtime advisory disposition also remains a Phase 10 gate. Phase 10 is still required.

## Frozen build input

- The package tool captured and verified a 240-file run-owned clean source snapshot before compilation.
- Snapshot manifest: `artifacts/release-readiness/20260713-195509/phase09/package-input-snapshot.tsv`.
- Snapshot SHA-256: `546BE0C220FF382EFFE13EEA230AF222D4E5C2AE00144EA87A912F751481956D`.
- `global.json` pins SDK `8.0.422` with prerelease disabled. The package embeds the verified .NET `8.0.28` self-contained `win-x64` runtime.
- Readiness state, worklog and final-audit documents written after packaging are post-package governance evidence. They are not claimed to be part of the frozen 240-file whole-tree hash. Product code, tests, tools and the 14 allowlisted package inputs must remain unchanged unless the RC is rebuilt and all package gates are rerun.
- `post-package-governance-drift.txt` rehashes the same scope after Phase 09 closeout: 13 changed readiness/worklog files, two added readiness files and zero removals; product code, tests, tools and package-bound top-level inputs changed 0. This is governance-only drift and does not require repackaging.

## Exact local RC

| Artifact | Bytes | SHA-256 |
|---|---:|---|
| `dist/RimWorldAiTranslator-v1.0.1-rc.1-win-x64.zip` | 66,240,659 | `DC132CC15B3654BB7306207DB26684B4021580A037B2E84F400A2105F6F966EF` |
| `dist/RimWorldAiTranslator-v1.0.1-rc.1-win-x64.manifest.json` | 2,652 | `710991677E19244BB3CAF5680070C97F6778EE8E832945D5219D5B3AEB1DC1B5` |
| packaged `RimWorldAiTranslator.exe` | 163,782,434 | `94ABDFE4E3F5D9EDAB75276E745313F6126CF9DC47139F6047D736A52200DE3E` |

The ZIP contains exactly 14 top-level allowlisted files. It contains no PowerShell file, PDB, source, test, fixture, log, diagnostic output, generated official-derived glossary, unsafe path, absolute path, traversal path or case-insensitive duplicate. All archive bytes and per-file hashes match the manifest. Evidence: `final-archive-audit.txt`, `final-archive-files.tsv`, and `final-verify-zero.log`.

## Reproducibility and regression

- Two clean package runs used distinct work-root GUIDs `0652a84486b24fedb0d2179e23337f59` and `52958e97977b40b4875c087364c938d0`.
- Both strict eight-project Release builds completed with 0 warnings and 0 errors.
- Both mandatory console suites passed 80/80 and both glossary self-tests passed.
- The two ZIPs are byte-identical. Their EXEs and all 14 extracted files are byte-identical. The out-of-band manifests truthfully differ in `createdUtc`; their archive identity and file lists are identical.
- Earlier compressed and uncompressed-unmapped attempts are retained as failure evidence. The equal-length uncompressed-unmapped EXEs had 2,655 differing byte positions; bundle parsing explained all of them through generated file-local absolute-path prefixes and their deterministic PE timestamp/MVID/bundle-ID cascades, with zero unexplained positions. Complete-root `PathMap` fixed the root dependence. Internal single-file compression is disabled as a separate final package policy, not claimed as the cause fix. Evidence: `repro-unmapped-root-cause.txt` and `repro-final-summary.txt`.

## Runtime and process evidence

- Both final package runs passed structured cold/warm/duplicate-instance smoke. Cold first-usable times were 2,031.434 ms and 2,054.583 ms; warm times were 630.009 ms and 627.099 ms.
- Cold and warm holders exited 0. Duplicate contenders exited 2 after exact title/body/default-button validation. Every run had zero descendants and zero active job processes after exit; run-owned smoke roots were removed.
- A separate exact-ZIP extraction under `최종 RC 검증 공간\최종 패키지` proved Korean and space path handling. It became usable in 2,013.585 ms, remained responsive through a 3-second idle observation, had zero descendants, closed normally in 55.527 ms, exited 0 and left zero same-EXE processes.
- The Korean-path run used only isolated synthetic data/discovery/profile/temp roots. The documented data root and `logs` directory were used; the application did not force-write a default `settings.json`. Opening/saving a complete project fixture on a genuinely clean machine remains manual.
- Final residue audit reports zero product/build processes, zero `dist/_package-*` work roots and zero package/smoke TEMP roots. Three exact stale smoke roots from failed attempts were verified as non-reparse, unreferenced run-owned GUID roots and removed.

Evidence: `package-smoke-run-1.json`, `package-smoke-run-2.json`, `package-smoke-final-summary.txt`, `final-korean-space-path-smoke.txt`, and `final-residue-audit.txt`.

## Executable identity, security and notices

- Windows x64 GUI subsystem; no console subsystem.
- ProductVersion `1.0.1-rc.1`, FileVersion `1.0.1.0`, AssemblyVersion `1.0.0.0`.
- Embedded manifest is `asInvoker`, `uiAccess=false`, and long-path aware. Windows Forms initialization is generated from `ApplicationHighDpiMode=PerMonitorV2`.
- The reconstructed embedded 9-frame icon is byte-identical to the deterministic project icon, SHA-256 `72504E3EC080D33A1A354A348B32B2E9015D1AF299EBBF5A3772D655013E7B1B`.
- Authenticode status is `NotSigned`; no signer is present. This is disclosed and remains a SmartScreen/user-approval item, not a fabricated signing claim.
- SEC-010 is resolved for this exact RC. The archive contains four exact .NET 8.0.28 runtime notice/license files; `THIRD_PARTY_NOTICES.md` records their package revisions, inputs and hashes; all are manifest-bound.
- SEC-009 remains open. `glossary.generated.ko.json` is excluded because redistribution authority is absent. That avoids shipping the disputed bytes but removes bundled suggestions and changes glossary-bearing requests from Golden, so the sample/user glossary cannot be called parity-equivalent.

Local Microsoft Defender targeted scans used engine `1.1.26060.3008`, product `4.18.26060.3008`, signature `1.455.125.0` dated 2026-07-14 01:23:38 +09:00. Signature update was not requested and remediation was disabled. The exact ZIP and extracted package both returned exit 0 and no threats. This is a point-in-time targeted scan, not a full-system or permanent safety guarantee. Evidence: `final-exe-metadata.txt`, `final-bundle-manifest-summary.txt`, `final-defender-scan.txt`.

## Performance boundary

`final-package-performance.json` binds the exact 14-file extracted package (164,101,972 bytes) and exact ZIP to the Phase 08 5,000-row benchmark. Its 12 automated checks and package-size gate are PASS. Its top-level `result` and `evidenceCompleteness` intentionally remain `BLOCKED` because Core benchmarking does not substitute for separate package-process smoke or clean-machine/manual/non-equivalent boundaries.

## Immutable predecessor

The current release-note tail from `# RimWorld AI Translator v1.0.0` is exact to Golden Master after line-ending normalization. The existing v1.0.0 ZIP remains 66,397,568 bytes, SHA-256 `928C871F29BACD43CD4CE3C377AFED9066355B3F31CDAB08691C1C9BEFE66351`, with a last-write time predating this release-readiness execution. No v1.0.0 tag, Release or asset action was performed. Evidence: `final-v1-immutable-audit.txt`.

## Ordered next action

Read `docs/public-release/10_INDEPENDENT_AUDIT_AND_FINAL_GATE.md` in full immediately before Phase 10, then challenge this exact RC and the stated legal/manual blockers without changing public GitHub state.
