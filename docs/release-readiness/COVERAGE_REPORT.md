# Phase 10 Semantic Coverage Report

## Evidence boundary

- Frozen package-build input snapshot v5 captured before final reporting: 250 files, combined SHA-256 `C279F6F6C87634D95035C3D17821770DE0703649CCD33A3A55B123695683A006`. An artifact-excluded post-package audit found governance-only reporting drift and zero product/test/tool/package-bound drift.
- Exact local RC: ZIP `2302E877...BBB7C1`, EXE `9EEB6A80...E919C8`.
- Raw line/branch percentage: **not collected**. No percentage is invented.
- Coverage means executed positive, negative, fault, cancel, recovery and boundary scenarios. Historical results are not promoted into current evidence without a current rerun.

## Current execution counts

| Gate | Current evidence | Result |
|---|---|---|
| Strict Release build | eight projects, warnings 0, errors 0 | PASS |
| Standalone post-fix full suite | 82/82, FAIL/SKIP 0 | PASS |
| Final tooling self-test | separate post-PID-fix run, 18/18 | PASS |
| Clean package run 1 | strict 0/0, 82/82, glossary, cold/warm/duplicate smoke | PASS |
| Clean package run 2 | strict 0/0, 82/82, glossary, cold/warm/duplicate smoke | PASS |
| Clean package run 3 | strict 0/0, 82/82, glossary, cold/warm/duplicate smoke | PASS |
| Flaky/reproducibility | three package roots; ZIP/EXE/14 files byte-identical | PASS, flaky 0 |
| UI performance | five independent 5,000-row WinForms reports | PASS 5/5 |
| Feature sample | 22 of 54 rows (40.7%), risk-weighted | PASS after three sampled P1 repairs; three later local-gap/package P1 repairs also re-audited |

The 82 top-level console registrations consist of the settled 80-test Phase 08
registry plus Phase 10 operation-plan and local-gap closure registrations. Composite registrations
exercise multiple injected fault branches and continue after a failure rather than
stopping at the first error.

## Registered coverage groups

| Group | Representative registrations | Current status |
|---|---|---|
| Storage/state/recovery | round-trip, semantic/double corruption, backup single-read, revision concurrency, async rollback, forced-exit recovery | PASS |
| Security/privacy/resources | endpoint/key/log/diagnostic privacy, path/reparse/hardlink, resource limits, active content, native archive boundaries | PASS |
| Project/review/discovery | cleanup, run registration, isolated discovery, exact comparison/target binding, keyless/legacy quarantine, source change | PASS |
| Translation/provider | SourceOnly, Google fallback, canonical prompt, status/drop/shape/retry/budget, split, partial, cancel/resume | PASS |
| Apply/RMK | physical Workshop refusal, plan fingerprint, token safety, stale/identity swap, cancellation/rollback, eligibility/history/workspace/builder | PASS |
| Reliability/time/process | atomic storage faults, artifact boundaries, project cancellation, monotonic limiter, child kill/recovery, single-instance/CAS | PASS |

## Critical branch matrix

| ID | Boundary | Positive | Negative/fault | Result |
|---|---|---|---|---|
| STOR-01 | Atomic validated replace | valid temp/flush/readback/replace | corruption, lock, failure, backup substitution, concurrent winner | PASS |
| STOR-02 | Transaction recovery | forward/rollback completion | repeated kill windows, forged/unknown residue, directory/leaf swap | PASS |
| APP-01 | Local Apply | exact eligible plan | Workshop alias, stale source, unsafe/duplicate, decision/source/output drift, cancel/rollback | PASS |
| RMK-01 | RMK export | exact `bus` work clone and workbook | subscription/branch/alias, ambiguity, workbook drift, builder failure/cancel | PASS |
| XML-01 | XML | valid Unicode/encoding/newline/content | DTD, depth, invalid Unicode, oversized input, forbidden fields/Patches | PASS |
| ZIP-01 | XLSX/ZIP | passive valid package preservation | traversal, duplicate, ratio/size/count, active/formula/external, per-entry Zip64/split/malformed extra/trailing | PASS |
| HTTP-01 | Provider | canonical normal/fake response | unsafe endpoint, redirects/cookies, status/drop/timeout/shape/ID/budget/retry/split | PASS |
| STATE-01 | Recovery notice | exact target/corrupt leaf surfaced | no-copy, multiple, control chars, parent-path disclosure, undrained queue | PASS automated; native perception manual |
| UI-01 | Large review | 5,000-row virtual search/filter/navigation | cancellation feedback, selection/scroll retention, close and startup failures | PASS for tested current modes |
| PROC-01 | Lifecycle | cold/warm/reacquire/normal exit | duplicate contender, timeout/child containment, residue | PASS; clean-PC and separate Windows session manual |
| PKG-01 | Package | allowlist/hash/start/exit/reproducibility | scripts, unsafe paths, wrong hashes, stale source, residual current-run roots | PASS |

## Phase 10 feature sample (22/54)

The independent audit sampled the following high-risk rows: `LCH-001`, `PROJ-004`,
`PROJ-005`, `EXT-001`, `EXT-002`, `EXT-004`, `TRN-002`, `TRN-003`, `TRN-004`,
`TRN-006`, `REV-002`, `REV-003`, `REV-004`, `REV-006`, `APP-002`, `APP-003`,
`APP-004`, `RMK-003`, `RMK-005`, `STATE-001`, `STATE-002`, and `PKG-003`.

- Findings: APP-002 unbound replan, STATE-002 incomplete presentation/drain and the
  archive boundary represented by RMK-003/SEC-003 entry-level metadata.
- Remediation: plan fingerprint, centralized leaf-only recovery presentation, and
  stricter central-directory parser.
- Re-audit: focused tests, standalone 82/82, three 82/82 package runs and exact
  package/security scans pass.
- Subsequent local-gap/package audit also fixed identical-source propagation safety,
  numeric-PID ancestry reuse and transient exact-cleanup rename handling; the final
  v5 suite/package/tooling evidence includes those regressions.
- TRN-003 and REV-006 remain `BLOCKED` by SEC-009; PKG-003 remains
  `NEEDS_EVIDENCE` for clean-PC/no-SDK. These are not false PASS results.

## UI harness and evidence

- Historical visual contract: Golden/candidate 15/15, actual-125 supplemental 20/20 and 75 comparisons.
- Historical pre-local-gap evidence index: 268 accepted artifacts, manifest SHA-256 `CD0BB87BDEFED113DF05CDA1DCE4E7D088ACCC792DB182D6A8873FEF23FF4E6D`.
- Current v5 binding: five 5,000-row interactions, recovery presentation, in-process local-gap v2, Korean-path and exact-package startup/language-dialog probes pass and are bound by `final-ui-evidence-binding-v5.txt`.
- Exact package: three cold/warm/duplicate smokes, exit codes 0/2, descendants 0 and cleanup 3/3.
- Manual exclusions: actual 150/200% and mixed DPI, Narrator/NVDA/OS high contrast, native-dialog perception/focus, key reveal/copy and subjective visual acceptance.

## Provider/batch sampling

The 11 catalog profiles reduce to OpenAI-compatible `JsonSchema`,
OpenAI-compatible `JsonObject`, and Google `PromptOnly` families; SourceOnly is a
zero-transport mode. Representative deterministic construction/transport and
single/multi/split/cancel-resume batches are mapped in
`artifacts/release-readiness/20260713-195509/phase10/provider-batch-sampling.md`.
The exact packaged glossary-bearing default request remains SEC-009-blocked.

## Final coverage verdict

- Locally executable semantic coverage gate: **PASS**.
- Overall final verdict: **BLOCKED** by SEC-009 and the named environment/human checks.
- Evidence-free items are not PASS; raw line/branch coverage, clean-PC, actual Excel and manual UI branches remain explicitly unclaimed.
