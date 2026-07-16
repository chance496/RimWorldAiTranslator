# Security Policy

RimWorld AI Translator is an unofficial community tool. It is not developed, endorsed, or supported by Ludeon Studios. RimWorld and related names are the property of their respective owners.

## Supported versions

| Version | Status |
|---|---|
| `v1.1.0` | Current public version. Security fixes are provided on a best-effort basis. |
| `v1.0.0` | Existing public artifact. It is immutable; a security fix must be delivered as a distinct successor rather than by replacing its tag or asset. |
| Older versions | No security-update commitment. |

There is no guaranteed response or repair service level. Reports are handled on a best-effort basis. The absence of a response must not be interpreted as permission to publish credentials, personal information, private source text, or exploit details.

## Report a vulnerability privately

Do not include an API key, authorization header, private translation text, user path, log, diagnostic bundle, project file, or personal information in a public issue.

Use the repository's private vulnerability-reporting form if it is available:

`https://github.com/chance496/RimWorldAiTranslator/security/advisories/new`

The availability of that external form has not been verified by this local RC audit. If it is unavailable, use a public channel only to ask the maintainer for a private contact method, without disclosing the vulnerability or any sensitive material. Do not upload a proof of concept or diagnostic archive until a private channel and the minimum necessary data have been agreed.

A useful private report contains the affected version, a minimal synthetic reproduction, expected and actual behavior, and impact. Replace real paths, keys, mod text, and account details with clearly synthetic placeholders.

## Security-update policy

- The project does not silently replace the existing `v1.0.0` tag, release, or asset.
- Security fixes are prepared and verified in a new version before publication.
- This application is self-contained and carries .NET runtime code. Installing a newer system-wide .NET runtime does not update the runtime embedded in the application. A runtime security update requires a newly rebuilt and verified application package.
- The package pins .NET runtime packs at `8.0.28`. Runtime advisories and support status must be reviewed again for every future package.
- Unsigned builds can trigger Microsoft SmartScreen. The application has no Authenticode signature; a warning is not proof of malware or proof of safety.

See [PRIVACY.md](PRIVACY.md) for data handling and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for the runtime and asset inventory.
