# RimWorld AI Translator v0.1.16

## Changes

- Added an optional bulk action for manual translations: when identical source text appears elsewhere in the project, the reviewer can apply one translation to every matching string.
- Preserved notes and already reviewed identical translations during bulk changes; changed translations return to the translated state for review.
- Fixed source refresh discarding translations that had not yet been written into the mod's `Languages\Korean` folder.
- Source refresh now searches project run history for the latest valid review state, even when the most recent run is incomplete or has no decision file.
- Changed source values retain their translation but return to untranslated with a persistent updated marker; genuinely new keys remain new untranslated entries.
- Replaced unstable sequential-ID fallback matching with file-and-key or unique-key matching so inserted keys cannot inherit another string's translation.
- Rewrote the repository and packaged Korean documentation with correct UTF-8 text and current project workflow details.

# RimWorld AI Translator v0.1.15

## Changes

- Fixed AI translation failing when a saved mod folder path ended with a directory separator.
- Hardened Windows command-line argument quoting so trailing backslashes and embedded quotes cannot merge later translation options into a path.
- Normalized project mod paths when projects are created, opened, or translated.

# RimWorld AI Translator v0.1.14

## Changes

- Added a persistent `updated source` marker when a mod update changes the source text assigned to an existing key.
- Updated strings remain untranslated and excluded from apply until they are translated or reviewed again; the previous source text is retained in history.
- Added updated-string counts, list badges, editor badges, activity text, and a dedicated filter.
- Added one-click `All`, `Untranslated`, `Translated`, `Reviewed`, and `Updated` filters above the string list while retaining detailed filters in the dropdown.
- Refined the project dashboard and review workspace with a calmer RimWorld-inspired palette, clearer hierarchy, denser project cards, lighter borders, and responsive action groups.
- Fixed project cards counting a JSON result array as one item instead of showing every review string.

# RimWorld AI Translator v0.1.13

## Changes

- Translation editors now start with the AI candidate when available, otherwise the existing Korean translation, and remain blank only when neither exists.
- Added explicit text status and warning labels so review state is not communicated by color alone.
- Added persisted system/light/dark theme, text size, high-contrast, and edit auto-save settings without storing API keys.
- Added accessible names, descriptions, focus cues, tooltips, and keyboard navigation for the dashboard and review workspace.
- Added `Ctrl+F`, `F6`/`Shift+F6`, `Esc`, and expanded arrow-key navigation alongside the existing save and review shortcuts.
- Added delayed automatic saving for translation and memo edits, plus a confirmation dialog before writing reviewed translations into a mod.
- Fixed non-content splitters and progress indicators appearing as unnamed keyboard targets to accessibility tools.
- Reduced startup work by loading the 2.1 MB official glossary only when the terms tab is first opened.
- Added a validated local mod-catalog cache; unchanged Steam and local mod roots reuse the cached list while additions and removals invalidate it automatically.
- Removed a duplicate project-card statistics refresh during initial display.
- Hardened all mod XML readers by prohibiting DTDs and external entities and limiting parsed XML documents to 128 MB.
- Added strict language-folder, output-path, HTTPS endpoint, and XML localization-key validation before translation or review application.
- Fixed review-output containment checks to require a true directory boundary instead of a plain string prefix.
- Pinned PowerShell, cmd, Explorer, and tar launches to their Windows system paths to avoid executable search-path hijacking.
- Added reparse-point protection before the package builder clears its output folder.

# RimWorld AI Translator v0.1.12

## Changes

- Rebuilt the review workspace around a compact project header, searchable string list, focused translation editor, and structured history side panel.
- Added a restrained RimWorld-inspired light palette while preserving a dedicated dark theme for Windows dark mode.
- Fixed the workspace being rendered underneath the top command bar.
- Added responsive toolbar wrapping and compact editor sizing so restored windows do not clip review controls.
- Reworked the main flow so mod work is managed directly by mod cards instead of separate project/mod selectors.
- A local project now owns exactly one RimWorld mod; source loading, AI translation, and apply actions always use that project's saved mod folder.
- Added a source-only load path for manual translation without API calls; existing Korean translations are available as editable starting text.
- AI translation now fills previously untouched blank decisions with draft candidates while preserving manually edited translations.
- Replaced the old item table with a search-first review list using text/key filters and result cards.
- Reworked the whole GUI toward a bright three-column review app: mod/search list, source/translation editor, and history/terms/memo side panel.
- Large review lists now render the first visible batch of cards instead of freezing while drawing every key at once.
- Fixed several clipped dashboard and workspace controls by laying out top actions, project creation controls, and review panels from the current window size.

# RimWorld AI Translator v0.1.11

## Changes

- Rebuilt the GUI around local project management: project cards, activity history, global settings, and a per-project review workspace.
- Added file grouping, string list filtering, status filters, search, progress stats, edit history, related glossary terms, memo, and issue tabs.
- Added two local apply modes: reviewed-only apply and translated-plus-reviewed apply.
- When a mod update changes a key's source text, the saved decision is demoted to untranslated and excluded from apply until reviewed again.
- Added keyboard shortcuts: `Ctrl+S` save, `Ctrl+Enter` review and advance, `Alt+Up/Down` navigation.
- The review workbench still writes `review-decisions.json`, so the existing apply workflow remains compatible.

# RimWorld AI Translator v0.1.10

## Changes

- API keys are now optional in the GUI. Empty API input automatically uses Google Translate fallback.
- Removed the review-only and overwrite checkboxes from the GUI.
- GUI runs now always create review output first and open the review workbench on completion.
- GUI review application applies only approved reviewed rows, replacing existing keys for those approved rows.
- The mod browser now prefers active LoadFolders version paths such as `1.6` when a workshop root uses versioned folders.

# RimWorld AI Translator v0.1.9

## Changes

- Added a left-side mod browser that auto-detects RimWorld workshop and local mod folders.
- Reads mod names from `About\About.xml` and fills the mod path field when a mod is selected.
- Added a refresh button and search box for the detected mod list.

# RimWorld AI Translator v0.1.8

## Changes

- Added a local item-by-item review workbench for review-only results.
- Review decisions are saved to `review-decisions.json` with approved, rejected, hold, and pending states.
- Review application now prefers approved reviewed text when decisions exist, while keeping the previous safe-candidate fallback.

# RimWorld AI Translator v0.1.7

## Changes

- Removed the batch-size dropdown and fixed GUI runs at the stable batch size of 40.

# RimWorld AI Translator v0.1.6

## Changes

- Review-only runs now open the generated review folder automatically when they finish successfully.
- Added a `검토 폴더 열기` button to reopen the latest review folder from the GUI.
- The GUI log now points users to `_TranslationAudit\*-comparison.csv` for side-by-side review.

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
