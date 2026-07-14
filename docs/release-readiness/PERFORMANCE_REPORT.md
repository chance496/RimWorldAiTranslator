# Phase 10 Performance Report

## Evidence boundary

- Frozen package-build input snapshot v5 captured before final reporting: 250 files, combined SHA-256 `C279F6F6C87634D95035C3D17821770DE0703649CCD33A3A55B123695683A006`; later artifact-excluded drift is governance-only.
- Exact RC ZIP: 66,254,006 bytes, SHA-256 `2302E87747F55348EA5EF96E2D352686ADFD943BB82954C9DA1F789553BBB7C1`.
- Measurements use only 5,000-row synthetic fixtures, fake HTTP and isolated roots. No real API, key or user data was used.
- Golden UI/RMK figures remain reference-only because process and operation boundaries differ. No misleading relative slowdown percentage is reported.

## Five-sample candidate results

| Surface | Median | P95 | Worst/max | Gate |
|---|---:|---:|---:|---|
| WinForms 5,000-row search | 175.847 ms | 184.653 ms | 186.800 ms | PASS; no sample >= 200 ms |
| WinForms status filter | 68.502 ms | 81.341 ms | 82.104 ms | PASS; no sample >= 200 ms |
| WinForms Stop feedback | 11.140 ms | 12.663 ms | 12.850 ms | PASS; worst < 250 ms |
| Active fake-HTTP cancellation | 45.502 ms | 51.523 ms | 53.004 ms | PASS; worst < 2 s |
| XML extraction | 15.650 ms | 20.203 ms | 20.654 ms | PASS |
| Review load | 198.050 ms | 229.329 ms | 236.332 ms | PASS |
| Core key search, 100 samples | 6.964 ms | 8.422 ms | 140.837 ms | PASS supplemental |
| Core status filter, 100 samples | 0.413 ms | 0.584 ms | 0.792 ms | PASS supplemental |
| Project create | 6.635 ms | 14.021 ms | 15.774 ms | PASS |
| Project warm open | 3.580 ms | 3.788 ms | 3.830 ms | PASS |
| Review full save | 169.686 ms | 194.826 ms | 197.910 ms | PASS |
| Apply DryRun | 962.629 ms | 1,006.177 ms | 1,010.935 ms | PASS |
| Apply | 1,356.553 ms | 1,369.139 ms | 1,369.353 ms | PASS |
| Transaction rollback | 17.159 ms | 24.769 ms | 26.580 ms | PASS |
| RMK warm read | 89.457 ms | 100.472 ms | 103.167 ms | PASS |
| RMK warm update | 395.840 ms | 416.919 ms | 421.670 ms | PASS |
| Full in-process RMK export | 2,574.471 ms | 2,725.014 ms | 2,742.699 ms | PASS absolute; Golden boundary differs |

The final benchmark reports all 12 automated gates and the package-size gate as
`PASS`. Its top-level result remains `BLOCKED` by design because it does not claim
clean-PC, human UI/Excel checks, package-process fault injection at every Apply/RMK
mutation window or equivalent Golden process boundaries.

## Memory and lifecycle

- Twenty Core review open/release cycles: stabilized working set `-15.780%`, private memory `-22.555%`; both below the +15% investigation threshold.
- The earlier current MainForm 20-cycle probe remains PASS with working/private/managed growth `+7.844% / +5.267% / +0.217%` and handle/GDI/USER deltas 0.
- Three exact-package runs left zero descendants and zero active job processes after cold/warm/duplicate exit; each reports `tempRootRemoved=true`.
- A supplemental exact-v5 packaged idle probe sampled five responsive seconds after
  readiness: working set 77,262,848-77,287,424 bytes, private memory
  19,906,560-19,910,656 bytes and handle count 410. It exited 0 and removed its exact
  isolated temp root. This closes the packaged-idle measurement omitted by the Core
  benchmark without relabeling benchmark-process memory as application memory.

## Exact package startup and size

| Metric | Run 1 | Run 2 | Run 3 | Result |
|---|---:|---:|---:|---|
| Cold first usable | 1,932.323 ms | 2,004.372 ms | 2,072.266 ms | PASS |
| Warm first usable | 638.733 ms | 631.047 ms | 688.868 ms | PASS |
| Normal cold close-to-exit | 65.824 ms | 67.047 ms | 68.798 ms | PASS, exit 0 |
| Duplicate contender | 138.611 ms | 122.699 ms | 129.595 ms | PASS, exit 2 |

- Publish payload: 14 files, 164,119,892 bytes, below the 160 MiB aggregate cap.
- ZIP: 66,254,006 bytes; exact hash above.
- EXE: 163,800,354 bytes; SHA-256 `9EEB6A80DD51DCAFD31D2516CFB6F1C70257CEDD94783C360ED58FEA3FE919C8`.
- ZIP, EXE and all 14 payload files are byte-identical across all three runs. The out-of-band manifest is identical after omitting only truthful `createdUtc`.

## Verdict and remaining gaps

- Automated performance/size/startup/process gate: **PASS**.
- Overall evidence completeness: **BLOCKED**.
- Remaining completeness blockers: equivalent Golden process/operation boundaries,
  project/Apply/RMK cancellation timing, clean PC/no SDK, actual Excel, actual 150/200%
  and mixed-DPI, assistive technology, native-dialog/user acceptance, and end-user
  packaged process-kill/recovery coverage across representative Apply/RMK mutation
  windows.
- Non-blocking follow-up: a longer packaged soak would strengthen confidence beyond
  the required 20-cycle and repeated package evidence but is not claimed here.

Primary evidence: `artifacts/release-readiness/20260713-195509/phase10/final-v5-package-performance.json`, `final-v5-packaged-idle-working-set.json`, `post-fix-ui-performance/`, `package-final-v5-final-run-1.log`, `package-final-v5-final-run-2.log`, `package-final-v5-final-run-3.log`, and `package-reproducibility-v5-final.json`.
