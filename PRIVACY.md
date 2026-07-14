# Privacy Notice

RimWorld AI Translator is an unofficial local desktop tool. It has no application telemetry, usage analytics, crash upload, automatic log upload, or automatic diagnostic upload.

## Local access

The application can read the mod, Workshop, RimWorld, RMK subscription, and RMK work-clone locations selected or discovered for the requested workflow. Workshop and RMK subscription content is treated as read-only. A local mod or RMK work clone is written only after the user chooses the destination and confirms an apply/export operation.

Project state, settings, reviews, logs, caches, and backups are stored under `%LOCALAPPDATA%\RimWorldAiTranslator` by default. User-selected reports and diagnostic bundles are stored at the destination chosen by the user.

## API keys and external transmission

- API keys are kept in managed process memory and are not intentionally written to settings, project files, logs, reviews, or diagnostic bundles.
- A key can remain in the settings editor, a provider draft, and a translation-run copy until it is replaced, cleared, or the process exits. Hiding it after 15 seconds only masks the display; it does not immediately erase or zeroize managed memory.
- When the user starts AI translation, an OpenAI-compatible provider receives the API key, model options, translation instructions, the selected custom glossary and additional instructions, and each selected item's request ID, translation key, kind, Def context, field, and source text. Project names, absolute paths, existing translations, notes, and review history are not intentionally added to that request.
- If no API key is supplied, the Google fallback sends source-text chunks in an HTTPS GET `q=` parameter. It does not send an API key, glossary, translation key, Def context, or notes. Retries can transmit the same text more than once and can leave source-bearing URLs in provider or proxy records.
- Provider retries and batch splitting can repeat source text and increase request count or cost.
- The application does not verify a provider operator's identity, retention, training, jurisdiction, privacy, quality, or pricing policies. Confirm the displayed destination and the provider's terms before starting translation.

Dry-run, source analysis, manual review, local apply preview, and ordinary project editing do not call a translation provider. A user-selected RMK Builder executable is different: it is not sandboxed and can use the current Windows user's file and network permissions. Run it only when the complete work clone and executable source are trusted.

## Logs and diagnostics

Daily logs are written to `%LOCALAPPDATA%\RimWorldAiTranslator\logs\RimWorldAiTranslator-YYYYMMDD.log`. They can contain timestamps, levels, operation state and counts, some project or mod display names, and bounded error categories or HResult values. The application redacts credential-like values and Windows absolute paths and does not intentionally log request or response bodies or source text.

There is no automatic log-retention period. Logs remain until the user deletes them.

The UI action labeled `진단 번들 저장` (Save diagnostic bundle) creates a local ZIP only at a user-selected path. It contains aggregate operating-system, runtime, culture, settings/provider-category, project/language, review-status/source, error-category, and product-file metadata. It is designed to exclude source text, translations, translation keys, API keys, authorization headers, raw logs, complete provider URLs or hosts, project names, notes, and absolute paths. The application does not upload or automatically delete the ZIP or its replacement backup.

## Delete local data

Exit the application before deleting files.

- Settings: delete `%LOCALAPPDATA%\RimWorldAiTranslator\settings.json` and `settings.json.bak`.
- Reviews: use the application's project-delete action for an owned review directory, or delete `%LOCALAPPDATA%\RimWorldAiTranslator\reviews` after exit.
- Logs: delete individual files or `%LOCALAPPDATA%\RimWorldAiTranslator\logs`.
- All app-owned local data: delete `%LOCALAPPDATA%\RimWorldAiTranslator`.
- Remove the application: delete the extracted local RC folder after the process exits.

Project deletion can leave information in `projects.json.bak` and successful-operation logs. Reports, diagnostic ZIPs, local-mod translation output, and RMK work-clone output stored elsewhere must be deleted separately. Provider-side data is controlled by the selected provider and cannot be deleted by this application.

Never attach real keys, private source text, user paths, logs, or diagnostics to a public issue. Follow [SECURITY.md](SECURITY.md) for private vulnerability reporting.
