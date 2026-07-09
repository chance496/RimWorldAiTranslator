# RimWorld AI Translator v0.1.6

## Changes

- Review-only runs now open the generated review folder automatically when they finish successfully.
- Added a `검토 폴더 열기` button to reopen the latest review folder from the GUI.
- The GUI log now points users to `_TranslationAudit\*-comparison.csv` for side-by-side review.
- Added a GUI batch-size dropdown for 40, 60, or 80 entries per request.

# RimWorld AI Translator v0.1.5

## Changes

- Added an `Apply review results` workflow in the GUI so review-only candidates can be applied without calling the API again.
- Added `Apply-RimWorldAiReviewResults.ps1` for CLI application of `safeToApply=true` comparison rows.
- Review application respects the existing overwrite checkbox: unchecked keeps existing Korean keys, checked replaces them.

# RimWorld AI Translator v0.1.4

## Fixes

- Reduced the default batch size from 80 to 40 to lower malformed JSON risk on long description-heavy mods.
- Added automatic split retry: if a batch keeps returning malformed JSON or missing ids, it is retried as smaller half-batches down to single entries.
- The GUI now hides raw model JSON dumps and repeated `\u000a` escape spam from the debug log.
- Added prompt guardrails against padding blank lines and repeated newline escapes without imposing a hard translation length limit.

# RimWorld AI Translator v0.1.3

## Fixes

- Shortened malformed model-response warnings so broken JSON is not dumped into the GUI log.
- Treats pathological newline-spam translation candidates as unsafe writes and records them in audit/comparison output.

# RimWorld AI Translator v0.1.2

## Fixes

- Fixed GUI API key parsing so multiple keys entered on separate lines are counted and passed to the translator correctly.
- Fixed the Stop button so it terminates the translator process tree instead of only killing the intermediate command wrapper.

## Documentation

- Clarified that normal users enter Cerebras API keys directly in the GUI, one key per line, and do not need key files.
- Documented that DLL/C# hardcoded runtime inspect strings, gizmo labels, and status text may require source changes or Harmony patches when the mod does not use RimWorld translation keys.

# RimWorld AI Translator v0.1.1

## Changes

- Removed the `-ApiKeyFile` option and key-file documentation.
- The GUI now passes API keys to the translator process through an environment variable instead of writing a temporary key file.
- Removed `api-keys.example.txt` from the release package.

# RimWorld AI Translator v0.1.0

Initial public release.

## Highlights

- Windows GUI launcher for RimWorld mod translation.
- Cerebras `gemma-4-31b` chat completions integration.
- Free-tier defaults: 5 requests/min/key, 30k input tokens/min/key, 1M daily tokens/key.
- Multiple API keys can be entered one per line and are rotated automatically.
- Automatic source language folder detection for English, Chinese, Japanese, and other non-Korean RimWorld language folders.
- Writes translations into `Languages\Korean` inside the selected mod folder.
- Official RimWorld Core+DLC Korean glossary included as `glossary.generated.ko.json`.
- Optional user glossary support via TXT, TSV, CSV, or JSON.
- Dry run and review-only modes for safer checks before writing files.

## Package

Download `RimWorldAiTranslator.zip`, extract it, and run `RimWorldAiTranslator.exe`.

Windows PowerShell, internet access, and a Cerebras API key are required.
