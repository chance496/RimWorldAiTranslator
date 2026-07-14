# UI Parity Matrix

## Evidence boundary

- Golden Master: exact commit `4c7d11b49126ba3987e9d49bd16944d4376ba0bc`.
- Golden images/trees: `artifacts/release-readiness/20260713-195509/phase06/golden/`.
- Final matched C# images/trees: `artifacts/release-readiness/20260713-195509/phase06/candidate/`.
- 125% DPI and supplemental state images/trees: `artifacts/release-readiness/20260713-195509/phase06/supplemental/`.
- Five comparison artifacts per matched state (JSON, side-by-side, overlay, diff, mask): `artifacts/release-readiness/20260713-195509/phase06/comparisons/`.
- All captures use synthetic rows and the explicit isolated fixture root. The earlier discovery-contaminated capture remains deleted and is not evidence.
- Pixel differences are descriptive only. WinForms host/font rendering and the native C# control hierarchy produce large pixel ratios; the verdict is based on visible structure, required controls, workflow, state, clipping, accessibility metadata, interaction probes and human review.

## Matched 100% DPI matrix

All 15 final candidate captures completed with exit code 0 and contain a PNG, accessibility tree and evidence JSON. Every row reports `missingAccessibleNames=0`, `clippedControls=0`, `clippedText=0`, and `errorBytes=0`.

| UI ID | Screen/state | Client/theme | Golden evidence | Candidate and comparison evidence | Human structural/flow disposition | Verdict |
|---|---|---|---|---|---|---|
| UI-001 | Empty dashboard, minimum, large text | 900x600, Light | `dashboard-empty-minimum-light.*`; 36 visible controls, 0 missing/clip | same-name candidate PNG/tree/evidence; comparison ratio 0.497733 | Same navigation, search, mode selector, create/folder/refresh actions and empty-state workflow; adaptive rows keep required actions reachable | PASS |
| UI-002 | Dashboard with project card | 1280x720, Light/Frontier | `dashboard-projects-light.*`; 42 visible controls, 0 missing/clip | same-name candidate set; ratio 0.999999 | Same search/mode/card counts, recent state, Delete and Open workflow; native C# palette/spacing differ without changing information or order | PASS |
| UI-003 | Settings | 1280x720, Dark/SciFi | `dashboard-settings-dark.*`; 64 visible controls, 0 missing/clip | same-name candidate set; ratio 0.630845 | Provider/API, appearance, diagnostics and RMK sections remain present and keyboard reachable; capture redacts synthetic key/endpoint fields | PASS |
| UI-004 | Workspace minimum / large text | 900x600, Light | `minimum-light-large-text.*`; 67 visible controls, 0 missing/clip | same-name candidate set; ratio 0.636035 | Adaptive two-row header and scrolling preserve Stop, refresh/translate/apply, editor, status actions and lower tool tabs; no required action is clipped | PASS |
| UI-005 | Workspace notebook | 1280x720, Light/Vivid | `notebook-light.*`; 71 visible controls, 0 missing/clip | same-name candidate set; ratio 0.684680 | Same list/editor/tool-tab hierarchy, status actions, selection and source/translation state | PASS |
| UI-006 | Workspace desktop | host-clamped 1540x845, Dark/Studio | `desktop-dark.*`; 71 visible controls, 0 missing/clip | same-name candidate set; ratio 0.497327 | Same wide list/editor/tool structure and navigation; candidate uses native C# spacing and DPI-aware sizing | PASS |
| UI-007 | High-contrast workspace | 1280x720, high contrast/text 12 | `notebook-dark-high-contrast.*`; 71 visible controls, 0 missing/clip | same-name candidate set; ratio 0.616984 | Text, selection, warnings and disabled state remain distinguishable without color-only status; human focus perception under the actual OS mode remains manual | PASS automated |
| UI-008 | Translation preflight modal | 1280x720, Light | `translation-preflight-light.*`; 95 visible controls, 0 missing/clip | same-name candidate set; ratio 0.769632 | Owned modal retains project/provider/count/usage, save warning, mode choice, Start and Cancel; initial focus and keyboard flow verified | PASS |
| UI-009 | Command palette | 1280x720, Dark | `command-palette-dark.*`; 78 visible controls, 0 missing/clip | same-name candidate set; ratio 0.625712 | Search, category, shortcut and Run/Close flow retained; unavailable commands are visibly and accessibly labeled and cannot execute via Enter | PASS |
| UI-010 | Operation loading | 1280x720, Dark | `operation-loading-dark.*`; 81 visible controls, 0 missing/clip | same-name candidate set; ratio 0.603588 | Current operation/progress remains bounded and Stop remains visible; operation state does not displace the editor/actions | PASS |
| UI-011 | Operation error/retry | 1280x720, Light | `operation-error-light.*`; 82 visible controls, 0 missing/clip | same-name candidate set; ratio 0.598790 | Text error/retry/close flow remains discoverable; UI/log presentation is privacy-bounded | PASS |
| UI-012 | Operation cancelled / partial | 900x600, Light/text 12 | `operation-cancelled-light.*`; 76 visible controls, 0 missing/clip | same-name candidate set; ratio 0.689333 | Partial/cancelled status remains explicit and the main editor/status actions remain usable | PASS |
| UI-013 | Operation completed | 1280x720, Dark | `operation-completed-dark.*`; 80 visible controls, 0 missing/clip | same-name candidate set; ratio 0.600541 | Completion state and transition back to review remain present without layout shift | PASS |
| UI-014 | Quality center, 5,000 rows | 1280x720, Light | `quality-center-light.*`; 70 visible controls, 0 missing/clip | same-name candidate set; ratio 0.684990 | Quality tab, class/issues navigation, report/recheck actions and virtual list are present; 5,000-row probe passes | PASS |
| UI-015 | Translation memory/glossary | 1280x720, Light | `translation-memory-light.*`; 63 visible controls, 0 missing/clip | same-name candidate set; ratio 0.688852 | Same-source local memory and glossary/term evidence are visible in the selected tool view; synthetic-only data and capture redaction remain intact | PASS |

## Supplemental DPI/state matrix

The final supplemental run has 20/20 successful captures at actual device DPI 120 (125%). Each row has a PNG, tree and evidence JSON with zero missing names, control clipping, text clipping or error bytes.

| Coverage | Evidence states | Result |
|---|---|---|
| Required resolutions | dashboard 1366x768; settings 1600x900; workspace requested 1920x1080 (client 1920x1055) | PASS |
| Window bounds | maximized workspace; requested 900x600 minimum scaled to client 1102x691 | PASS |
| Theme/accessibility | dark settings; actual-125 high-contrast workspace; light workspace | PASS automated; actual OS high-contrast toggle remains manual |
| Product surfaces | Activity, RMK, Log, Quality, Memory/Glossary, workspace loading | PASS |
| Dialogs/errors | source-language dialog, translation preflight, operation error, disabled command palette | PASS |
| Data/selection | empty search, multilingual row 29, bottom row 4,999, 5,000-row fixture | PASS |
| Focus | search box, language list, selected preflight mode (`미번역 부분만 번역`) and command search have recorded focused nodes | PASS |
| Scroll | bottom selection and adjacent keyboard navigation preserve a visible, bounded viewport | PASS |

## Interaction, keyboard and responsiveness

- `ui-interaction-postfix-final.json`: 35/35 checks pass on 5,000 synthetic rows. Workspace initialization 246.846 ms, quality calculation 82.147 ms, search 290.480 ms, status filter 65.271 ms and next selection 6.292 ms, all below the declared thresholds.
- Filtering and quality calculation are asserted off the UI thread; the quality list is virtual. Stop changes immediately to the disabled/requested state.
- F2, F3, Shift+F3, Ctrl+F, Ctrl+1/2/3, Alt+Q, Alt+C, Escape and F6 region navigation pass. Selection/scroll retention and edit-origin/undo behavior pass.
- `metadata-accessibility-postfix-final.json`: 13/13 checks pass for read-only focus, names/values, logical tab order, copy gestures and dynamic status/update descriptions.
- `command-palette-disabled-keyboard-postfix-final.json`: 9/9 checks pass; Enter cannot select or execute a disabled command, while it executes the enabled command once.
- `close-behavior-postfix-final.json`: dirty No/Yes/cancel and active-operation cancel/close scenarios pass 4/4.
- `regression-postfix-final.json`: strict build 0 warnings/errors, console 67/67, all UI probes pass, safe failure sink passes and residual processes are 0.

## Golden context and performance

- Golden 5,000-row load 727.512 ms; visible reload 401.694 ms; search 945.662 ms; next item 46.155 ms; save changed/no-change 1,597.872/5.346 ms.
- Golden operation bounds remain stable before/during/after progress. Candidate interaction timings are not a wire-equivalent benchmark, but demonstrate no 5,000-row long UI freeze and pass the Phase 06 responsiveness thresholds.

## Remaining manual UI checks

The automated Phase 06 gate is `PASS`. The following are deliberately not converted to automated PASS and are listed in `MANUAL_UI_CHECKLIST.md`:

- actual Windows 150% and 200% display scaling and moving the app between monitors;
- Narrator/NVDA announcements and landmark navigation;
- actual OS high-contrast toggle, keyboard focus/hover perception and native confirmation/error dialogs;
- clean-PC font/runtime/rendering check.

These are explicit Phase 10/user-manual items. They do not erase the completed 100%/125% automated evidence, but the final overall RC verdict must state their unresolved status honestly.
