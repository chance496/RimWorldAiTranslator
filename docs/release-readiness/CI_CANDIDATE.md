# PowerShell-free local CI candidate

## Status and authority boundary

- Candidate workflow: `.github/workflows/ci.yml`.
- It is limited to `pull_request` and `workflow_dispatch`, with repository permission `contents: read`.
- It has not been pushed, dispatched, or run. Creating this file does not authorize a remote workflow run.
- It contains no release, tag, push, merge, deployment, asset upload, artifact upload, external message, or paid-provider operation.
- Both jobs use the Windows command shell. No PowerShell shell or inline PowerShell is present.

## Fixed action identities

- `actions/checkout` is pinned to `11bd71901bbe5b1630ceea73d27597364c9af683` (`v4.2.2`). Credentials are not persisted.
- `actions/setup-dotnet` is pinned to `d4c94342e560b34958eacfc5d055d21461ed1c5d` (`v4.3.1`) and consumes `global.json`.
- The workflow never follows a moving action tag.

## Verify job

The verify job mirrors the local, offline quality path:

1. restore with the repository `NuGet.config`;
2. build the full solution in Release with Recommended analyzers and warnings as errors;
3. verify the SDK format policy without restoring;
4. report the direct/transitive, vulnerable and deprecated package inventories, fail if any command fails, and fail if a `PackageReference` appears;
5. compare strong credential-pattern matches with the four reviewed synthetic test files;
6. run the complete console regression in three separate processes;
7. run the release-tooling and glossary-tooling self-tests;
8. run the 5,000-row benchmark with five iterations.

The application dependency graph currently has no `PackageReference`; the cleared-source vulnerability command fails on that empty graph and is N/A, not PASS. The embedded runtime is audited separately: official Microsoft servicing/support evidence identifies .NET 8.0.28 as the current supported .NET 8 release through 2026-11-10, and SEC-010 notices are manifest-bound for the exact Phase 10 RC.

## Package job and trust boundary

The package job creates the same exact RC artifact but never uploads it. A clean hosted image does not guarantee the required runtime-package cache, so a temporary class-library project downloads only the three exact `8.0.28` packages from NuGet.org into the standard runner cache:

- `Microsoft.NETCore.App.Runtime.win-x64` `8.0.28`;
- `Microsoft.WindowsDesktop.App.Runtime.win-x64` `8.0.28`;
- `Microsoft.AspNetCore.App.Runtime.win-x64` `8.0.28`.

The temporary seed project is outside the repository and is not an application dependency. The C# orchestrator then independently requires the pinned raw SHA-512, NuGet content hash, Microsoft author signature and repository countersignature before copying those packages into its run-owned offline feed. A wrong, missing, unsigned or changed package fails closed. No fallback version, ambient source, cache action or package/artifact upload is allowed.

After verification, the orchestrator creates a run-owned source snapshot, restores and builds from the verified offline feed, reruns its mandatory regression, publishes the self-contained single-file `win-x64` app, performs packaged-GUI smoke testing, installs the ZIP and manifest transactionally, and checks the active source plus exact ZIP for PowerShell residue.

## Local equivalence and evidence boundary

The workflow is a review candidate, not execution evidence. Authoritative Phase 10 evidence and the final local RC were produced by commands in this local session and are not inferred from this unexecuted workflow. A later workflow result would still require its runner image, action identities, downloaded runtime identities and exact source commit to be bound before it could support a release verdict.
