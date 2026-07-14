# RimWorld AI Translator v1.0.1-rc.1 (Local RC)

이 후보는 공개 v1.0.0을 변경하지 않는 로컬 검증 결과물입니다. push, PR, tag, GitHub Release, asset 변경, 외부 업로드와 실제 공개 배포는 범위가 아닙니다.

RimWorld AI Translator는 비공식 커뮤니티 도구이며 Ludeon Studios가 개발·보증·지원하는 제품이 아닙니다. RimWorld와 관련 명칭은 각 권리자에게 속합니다.

## C# 후보 변경

- 메인 프로그램, 번역 엔진, WinForms UI, 저장, 적용·RMK 내보내기, 용어집 도구와 패키징 도구를 C#/.NET 8 경로로 통합하고 사용자 실행의 PowerShell·Python·Node.js·Git 의존성을 제거했습니다.
- Workshop과 RMK 구독본을 읽기 전용으로 분류하고, 로컬/RMK 적용 직전에 현재 원문·경로·reparse·토큰을 다시 검증합니다.
- 적용 전 정확한 대상·행·파일·제외 수를 dry-run으로 표시하고 기본값 `아니요` 확인을 요구합니다.
- 의미상 손상된 JSON의 백업 복구와 사용자 알림, revision-safe 저장, 실패·취소 뒤 재개, 다중 파일 롤백과 동시 변경 감지를 보강했습니다.
- RMK Builder는 선택한 EXE의 identity·크기·SHA-256을 실행 직전에 확인하고 생성 파일을 transaction으로 게시하지만 sandbox는 아닙니다. 신뢰한 전체 작업 클론에서만 실행해야 합니다.
- 프로젝트가 직접 작성한 기하 도형에서 재현 생성되는 9-size Windows 아이콘과 설명·제품·버전·저작권 메타데이터를 포함합니다. 외부 이미지·폰트·게임 자산은 아이콘에 사용하지 않았습니다.

## 현재 실행의 기술 증거

- 정착된 Phase 08 source/test/tools snapshot은 SHA-256 `7A777BD9D051E4209A7AF7CDF83028966B95F256A2DF3BC3C6D61D9478A51141`입니다.
- 8개 프로젝트의 엄격한 Release 빌드가 경고 0·오류 0으로 완료됐습니다.
- 같은 Release 테스트가 독립된 직렬 프로세스에서 80/80을 세 번 연속 통과했으며 flaky 실패는 0이었습니다.
- 5,000행 합성 WinForms 상호작용, startup/close/cancellation, slow I/O/logger, same/cross-process lease, repository CAS, 접근성·명령·provider 경계와 MainForm 20-cycle 검증이 오류 출력과 잔류 제품 프로세스 없이 통과했습니다.
- 실제 API 키, 실제 사용자 데이터, 실제 번역 provider와 유료 API는 사용하지 않았습니다. 공개 GitHub 상태도 변경하지 않았습니다.

## 배포 제한과 알려진 blocker

- 이 로컬 후보는 Authenticode 서명이 없습니다. Microsoft SmartScreen 경고가 나타날 수 있으며, 경고 유무는 안전성을 보증하지 않습니다.
- self-contained 실행 파일은 .NET `8.0.28` runtime을 포함합니다. 시스템에 새 .NET을 설치해도 앱에 포함된 runtime은 갱신되지 않으므로, 보안 업데이트에는 새 후보를 다시 빌드하고 검증해야 합니다. 라이선스·고지 inventory는 `THIRD_PARTY_NOTICES.md`에 있습니다.
- `glossary.generated.ko.json`은 RimWorld 공식 Core/DLC localization 관측에서 파생됐지만 재배포 권리가 확인되지 않았습니다. 이 파일은 로컬 RC ZIP에서 제외합니다.
- 공식 파생 용어집을 제외하면 기본 용어 제안과 glossary-bearing AI request payload가 Golden Master와 달라집니다. 따라서 기능·요청 동등성과 전체 공개 품질 판정은 **BLOCKED**입니다. 합성 `sample-glossary.txt`나 사용자 용어집은 권리 문제를 대신 해결하거나 Golden parity를 증명하지 않습니다.
- clean-PC, 실제 Microsoft Excel, 150%/200%·혼합 DPI, screen reader와 공개 전 최종 수동 검증은 별도 blocker로 남습니다.

개인정보와 취약점 신고는 `PRIVACY.md`와 `SECURITY.md`를 확인하세요.

# RimWorld AI Translator v1.0.0

첫 안정화 릴리스입니다.

## 주요 기능

- 모드 하나를 프로젝트 하나로 관리하며 AI 초벌 번역, 직접 번역, 항목별 검수, 메모와 작업 이력을 로컬에 보존합니다.
- Cerebras, OpenAI, Gemini, DeepSeek, Qwen, Groq, Mistral, OpenRouter, Z.AI와 사용자 지정 OpenAI 호환 API를 지원합니다. API 키가 없으면 Google 번역을 사용할 수 있습니다.
- 여러 API 키의 순환 사용, 무료 한도 기반 요청 제어, 배치 분할 재시도, 완료 배치 체크포인트, 중지와 부분 복구를 지원합니다.
- Steam Workshop과 로컬 모드를 자동 검색하고 영어·중국어·일본어 등 프로젝트 기준 원문 언어를 선택할 수 있습니다.
- `Keyed`, `DefInjected`, 검증된 Def 표시 필드를 추출하며 내부 식별자, 렌더 트리, 경로와 기술 필드는 번역 대상에서 제외합니다.
- RimWorld 변수, 태그, 포맷 토큰, 문법 접두어와 한국어 자동 조사 문법을 번역·검수·적용 단계에서 보호합니다.
- 원문이 바뀐 키는 기존 번역과 과거 원문을 보존하면서 `변경됨 + 미번역`으로 내려 재검토 전 적용을 차단합니다.
- 검토됨만 적용하거나 안전한 번역됨까지 함께 `Languages\Korean`에 적용할 수 있으며 다중 파일 실패 시 전체를 롤백합니다.
- RMK 구독본을 읽기 전용 번역 자료로 사용하고, 선택한 RMK 작업 클론에 XML과 림추출기 호환 XLSX를 안전하게 병합합니다.

## 안정성 및 사용성

- 프로젝트·설정·검수 상태를 원자적으로 저장하고 정상 백업에서 복구합니다. 손상된 상태를 조용히 빈 데이터로 덮지 않습니다.
- 실행 준비 화면, 최대화 레이아웃 확정, 프로젝트 원자 공개를 통해 WinForms 컨트롤이 순서대로 나타나거나 화면이 흔들리는 현상을 줄였습니다.
- 작업 상태 막대는 본문 크기를 바꾸지 않으며 원문 분석과 AI 번역은 별도 프로세스에서 실행되어 창과 중지 버튼이 응답합니다.
- 가상 문자열 목록, 행 상태 캐시, 희소 검수 저장, 네이티브 XML/XLSX 판독기로 대형 프로젝트의 로드·검색·저장 비용을 줄였습니다.
- 품질 센터, 기존/후보/현재 번역 비교, 로컬 번역 메모리, 전체 안전 검토, 검색·상태·출처·편집 시각 필터와 키보드 단축키를 제공합니다.
- 프로페셔널, 사이파이, 비비드, 스튜디오, 프런티어 디자인과 밝음·어두움·고대비·글자 크기 설정을 제공합니다.

## 검증

- 오프라인 회귀 테스트 20개, 패키지 PowerShell 구문 검사, SourceOnly 추출 smoke, 5,000행 UI 성능·접근성 감사와 실제 최대화 시작 화면 감사를 통과했습니다.
- 실제 Defensive Network 모드의 원문 1,184개에서 검수 항목 846개를 읽기 전용으로 구성해 원본 모드 쓰기 없이 검증했습니다.

## 설치

`RimWorldAiTranslator-v1.0.0.zip`을 원하는 폴더에 압축 해제한 뒤 `RimWorldAiTranslator.exe`를 실행하세요. Windows 10/11과 Windows 기본 PowerShell이 필요하며 Python, Node.js, Git 같은 개발 도구는 필요하지 않습니다.

# RimWorld AI Translator v0.2.1

## Changes

- Detect source changes from the previous full project snapshot when an RMK entry has no source-history XLSX, so imported RMK translations cannot hide an update.
- Create or merge a RimworldExtractor-compatible source-history XLSX during RMK export while preserving existing workbook styles, comments, extra columns, required-mod data, and unreviewed historical source text.

# RimWorld AI Translator v0.2.0

## Changes

- Added persistent translation provenance for RMK imports, existing mod translations, AI drafts, and manual local edits.
- Added RMK XLSX source-history comparison by `Def Class + Node`; stale RMK translations become updated and untranslated on first import.
- Compare RMK language-folder history only against a matching current language folder; direct Def values are compared as the mod's actual source so a Def source-language change is still flagged for review.
- Added RMK/local-origin filters and newest/oldest sorting based only on the time a user actually edited a translation.
- Added a bulk review-complete action that skips blank translations, changed sources, and safety warnings.
- Fixed Windows PowerShell failing with `Argument types do not match` when an RMK workbook contains tens of thousands of rows.
- Replaced cell-by-cell XLSX and recursive Def parsing with bounded, DTD-disabled .NET readers; Medieval 2 source refresh dropped from a 17.6-second failure to a successful roughly 3.0-second runner pass.
- Precompiled the native XML/XLSX reader into the release package and retained source compilation only as a development fallback, removing repeated runtime C# compilation.
- Streamed large RMK shared-string and worksheet XML instead of materializing the whole sheet, while preserving file-size, DTD, entity, and path-traversal limits.
- Added `LoadFolders.xml` content-root support so shared languages, versioned Defs, and optional integration folders are scanned as one project.
- Moved source refresh to the cancellable background runner so the main window remains responsive.
- Reduced a measured 1,797-row review load from 8.19 seconds to about 1.0-1.2 seconds by avoiding repeat decision normalization, indexing repeated paths, caching row state, and replacing hundreds of nested card controls with a full owner-drawn virtual list.
- Added sparse, compact review-state persistence so auto-save stores AI/local edits and changed statuses without rebuilding every default RMK or mod translation.
- Deferred Steam mod-cache validation until after first paint and added an immediate native startup window; the packaged EXE showed visible feedback in roughly 0.1-0.22 seconds in local tests.
- Reused fonts and direct .NET constructors across static and review controls, reducing startup allocations and GDI object churn.
- Hardened launcher argument quoting and fixed its Korean startup error messages.

# RimWorld AI Translator v0.1.18

## Changes

- Added provider-based translation settings for Cerebras, OpenAI, Gemini, DeepSeek, Qwen, Groq, Mistral, OpenRouter, BigModel / Z.AI, custom OpenAI-compatible endpoints, and keyless Google Translate.
- Added editable per-provider API URLs, model presets, temperature settings, and in-memory multi-key rotation without persisting API keys.
- Fixed versioned `LoadFolders.xml` mods such as Kiiro Race resolving to the Workshop root instead of the active `1.6` content folder.
- Reworked project cards and compact review layouts to prevent progress bars, buttons, and reference tabs from overlapping at smaller resolutions.
- Excluded definite internal `DefInjected` identifiers such as AlienRace color-channel names and non-display `PawnRenderTreeDef` fields from AI translation and review lists, with an audit record of every exclusion.
- Protected RimWorld grammar-rule prefixes such as `r_logentry->` and require them to remain unchanged at the start of translated values before apply or RMK export.
- Added RimWorld Korean automatic-particle guidance to AI prompts and block reversed forms such as `이(가)` or `은(는)` during review, direct apply, and RMK export; valid forms include `(이)가` and `(은)는`.
- Added local RMK integration that discovers the Steam subscription and a `bus` branch working clone by Workshop or Package ID.
- Reuses RMK translations as editable defaults and sends only missing strings to AI translation.
- Added an `RMK에 적용` destination checkbox: unchecked writes to the original mod, while checked merges the same reviewed statuses into the RMK working clone.
- Replaced the AI overwrite warning with explicit `Overwrite`, `Translate missing only`, and `Cancel` choices; missing-only mode also preserves manual review translations that have not been applied to the mod yet.
- Added project-time source-language selection when a mod contains multiple language folders; the choice is stored per project and reused for refresh and AI translation.
- Added dedicated `Def Class` and `Node` search scopes.
- Added editing-focused keyboard shortcuts for navigation, candidate selection, status changes, source refresh, and AI translation control.
- Debounced review search, replaced repeated array growth, limited initial card rendering, and deferred full warning checks to visible or explicitly filtered rows for faster large-project interaction.
- Indexed glossary lookup without changing selected term order; repeated batch term selection is substantially faster while producing the same prompt terms.
- Removed the `cmd.exe` translation launch hop. API keys remain environment-only, while a local runner accepts only an allowlisted parameter set from a bounded JSON file.
- Improved low-resolution behavior with a 900x600 minimum layout, compact header controls, and scrollable editor/settings surfaces.
- Existing RMK keys remain in their original XML files, new keys are added once, changed or unsafe strings are skipped, and `LoadFoldersBuilder` runs after export.
- RMK Builder output is decoded with its native Korean code page and completion is verified from regenerated, valid output files instead of a localized log phrase.
- RMK subscription files are read-only references; Git commits and pushes remain manual.

# RimWorld AI Translator v0.1.17

## Changes

- Changed translated-entry safety checks to validate the reviewer's current edited text instead of stale AI-candidate metadata.
- Added protected-token, Korean-text, source-copy, blank-text, and pathological-newline checks to both the review UI and final apply path.
- Cached translation validation and warning results, with automatic invalidation whenever source or translation text changes.
- Optimized `Complete and next` to update only the active card and affected counters instead of serializing every decision and rebuilding the full list on each click.
- Batched routine status saves through the existing 1.2-second autosave timer while preserving immediate saves for explicit save, apply, project changes, and shutdown.
- Measured a 91.9 ms median transition time on a 749-entry review project, and 66.6 ms while using the translated-status filter.

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
