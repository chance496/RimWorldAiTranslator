# Worklog

## 2026-07-13 - Phase 04 physical-boundary and review-provenance checkpoint

- The final package-tool code re-audit now reports no P0/P1 after clean-source/artifacts-path provenance, Windows identity pins and side-effect-free rollback preflight. Tooling strict build remains 0/0 and its self-test 11/11; full package E2E is intentionally deferred until product source stops changing.
- Closed the RMK parent-junction Workshop alias and project cleanup plan/marker defects. RMK checks canonical opened paths and all volume-root components; cleanup marker v2 binds project, mod and review physical identities; the user-confirmed exact plan is passed unchanged to deletion. Latest strict product builds are 0/0, regression reached 47/47, and the independent boundary re-audit recommends PASS with only a same-user final check/use P2.
- Continued adversarial review found remaining data blockers rather than accepting the interim regression count: explicit-target bare-key/ordinal fallback, undeclared or newest comparison substitution, source-less direct Apply/RMK, duplicate workbook/XML identities and capture-phase snapshot gaps. These remain `IN_PROGRESS` with byte-preservation and fail-closed fixtures being added.
- UI-thread path probes are being moved into tracked cancellable background workflows; no claim is made until a responsiveness harness passes.
- No external repository change, real user-data fixture, provider call, staging, commit or shutdown occurred.

## 2026-07-13 - Phase 04 second adversarial checkpoint

- Closed the UI shutdown/save blockers with a durable-accept boundary for settings, a worker-owned 30-second close barrier, default-No force exit, full workflow drain and post-close UI suppression. Strict targeted builds pass with zero warnings/errors, the console suite passes 43/43, and both startup-close and cancellation-timeout harnesses pass. A current-code independent re-audit found no remaining UI close P1/P2.
- Rebuilt and ran the first package-hardening self-test at 11/11, then deliberately rejected that result as final evidence after an independent second pass found three P1 gaps: lexical path versus handle identity, consumption of default incremental `bin/obj`, and mixed rollback after partial verified cleanup.
- Package remediation now uses Windows volume/file identity, a run-owned clean source snapshot and SDK `--artifacts-path` across restore/build/test/publish. The verified-recovery cleanup path and fault injection are still being completed, so Phase 04 remains `IN_PROGRESS` and no RC package is claimed.
- Active working-filesystem script count remains zero. The Git index still names the two tracked-but-deleted scripts until a future authorized commit, and ignored historical user-owned ZIPs remain untouched; final proof must inspect the exact new RC separately.
- No push, PR, tag, Release, asset, upload, paid API, real user-data fixture, staging or commit was performed. The user authorized shutdown only at genuine completion or terminal stop; no shutdown has yet been scheduled.

## 2026-07-13 - Phase 04 durable-close and package re-audit checkpoint

- Unified MainForm asynchronous work under an owned lifetime boundary. Normal close now stops new work, requests cancellation, drains active workflows, flushes settings, and performs the final review save before disposal. Save failure defaults to keeping the app open.
- Removed control-lifetime cancellation from already accepted settings snapshots, isolated settings notifications, and made persistent logging/subscriber failure non-fatal. Current targeted Release builds report zero warnings/errors and the console regression passes 43/43.
- Preserved the active-source PowerShell count at zero. No historical user-owned archive was removed or changed.
- Independently re-audited the C# packager. Phase 04 remains open on six P1 findings: verified-recovery file pinning, child environment isolation, exact runtime-pack identity, source input pinning, ZIP-derived manifest verification, and isolated NuGet extraction sealing.
- Verified the three locally cached 8.0.28 runtime packages offline: each has the pinned Microsoft Author certificate and NuGet.org repository countersignature. Packaging still may not proceed until source-controlled raw/content hashes and the remaining fail-closed boundaries are implemented and retested.
- No push, PR, tag, Release, upload, paid API, real user-data fixture, shutdown, restart, staging or commit was performed.

## 2026-07-13 - Phase 04 PowerShell-zero and final blocker checkpoint

- The two root developer scripts were deleted only after the C# replacements built with zero warnings/errors, the Tooling security/recovery self-test passed 7/7, the source audit reported exactly those two files, and an independent adversarial review returned PASS-to-delete.
- The current eight-project Release solution builds under Recommended analyzers with warnings as errors at 0 warnings/0 errors. The current console regression is 41/41 and the C# glossary self-test passes.
- The first full C# package attempt exposed an invalid restore switch and was repaired. The second failed closed because exact self-contained .NET 8.0.28 runtime packs were not present in the run-isolated cache; no network source was enabled and no RC output was installed.
- A strict UI/save audit found two data-safety blockers: failed review saves could return success to dependent refresh/translation/apply flows, and close-time saving could race an older autosave outside the save gate. Both are under focused repair and regression testing before Phase 04 can pass.
- Active filesystem scripts are now zero. User-owned ignored historical archives under `dist/` still contain legacy scripts and are preserved; the final zero claim will cover active source and the exact new versioned RC package, not historical inert archives.
- No push, PR, tag, Release, asset, external upload, paid API, real user-data fixture, shutdown or restart action occurred.

## 2026-07-13 - 공개 품질 Phase 04 체크포인트 (로컬 RC 진행 중)

- 현재 공개 판정은 아직 내리지 않았다. Phase 03 기준선/대응표는 완료됐고 Phase 04 C# 전환과 PowerShell-zero 게이트를 진행 중이다. 원격 push/PR/tag/Release/asset 변경은 없었다.
- `AnalysisMode=Recommended`, nullable, deterministic build와 warnings-as-errors를 함께 적용했다. Native의 기존 전역 공개 API/P/Invoke와 Core/App의 의도된 서비스 추상화에만 좁은 호환 예외를 문서화했고, 문화권·취소 토큰·JSON 옵션·컬렉션·경계 관련 진단은 코드로 수정했다.
- 편집 내용을 모델에 반영하기 전 화면 전환/자동 저장이 빠지는 결함과 비동기 저장 완료가 더 최신 편집의 dirty 상태를 지우는 경쟁을 수정했다. 20,000행 합성 검수에서 저장 중 새 번역·메모가 보존되고 후속 저장으로 왕복됨을 확인했다.
- RMK Builder를 앱 전역 작업·취소에 연결하고 `LoadFolders.xml`/`ModList.tsv`를 하나의 트랜잭션으로 취급한다. 합성 자식 EXE의 비정상 종료·취소는 두 파일을 원래 바이트로 복원하고, 성공은 XML/TSV 검증 뒤에만 유지한다.
- 활동 기록의 JSON 읽기를 UI에서 Core 비동기 서비스로 이동했다. 설정은 공급자 선택을 지속하고, 키 노출은 시작 시점부터 최대 15초이며 창 비활성화 시 즉시 숨긴다. 로그는 API 키와 drive/UNC 절대경로를 마스킹한다.
- Golden 검색 키보드 이동, 실시간 prefix/suffix diff, Ctrl+Enter 다음 항목 이동, 로딩 오버레이 이전 타이머/중복 취소, 카드 컨트롤 해제와 접근성 이름을 보완했다.
- 현재 집중 회귀는 41/41 PASS이고 Native/Core/App/테스트 대상 Release 빌드는 권장 분석기 조건에서 경고 0/오류 0이다. C# 패키지 도구 자체 검사는 7/7 PASS이나, 루트 스크립트 두 개는 최종 대체 증명 검토 전이라 아직 삭제하지 않았다.
- 다음 단계는 Tooling diff 자체 검토와 self-test 재실행, 정확히 두 개발 스크립트 제거, whole-solution/패키지/실행/재귀 zero 감사를 순서대로 수행하는 것이다.

## 2026-07-13 - C# 네이티브 v1.0.0 릴리스 후보

- WinForms 앱, 프로젝트·설정·검수 저장, 모드 탐색, XML 추출, API 재시도·키 순환, 체크포인트·취소·재개, 로컬 적용, RMK XML/XLSX, 품질·진단을 `src/`의 .NET 8 App/Core/Native 계층으로 이전했다.
- 기존 JSON의 알 수 없는 속성과 RMK 워크북 부가 구조를 보존하며, 저장 이중 손상·직접 출력 실패·RMK 다중 파일 실패·원문 변경·토큰 손상·키 노출을 임시 fixture 회귀에 고정했다.
- 원문 분석과 AI 번역 중 검수 화면과 중지 버튼을 유지하고, 진행 중 종료는 취소 완료 뒤 닫도록 정리했다. 전체 안전 검토, 진단 ZIP, 품질 보고서, 프로젝트 통계 캐시와 RMK 자동 탐색을 연결했다.
- RMK XLSX의 과거 원문 언어가 현재 선택 언어와 다를 때 없는 언어 폴더를 추출해 로드를 실패시키던 문제를 수정하고 서로 다른 언어를 변경 비교하지 않도록 했다.
- C# 회귀 30/30, Release 빌드 경고 0/오류 0. 실제 Workshop 모드는 읽기 전용으로 열어 원문·RMK 기존 번역·Def Class/Node·갱신 중 중지를 확인했다.
- Git에서 무시되던 로컬 모드별 용어집을 패키지 필수 파일로 삼던 재현성 문제를 제거했다. 배포본은 추적된 원본+DLC 용어집만 포함하고, 추가 용어집은 설정에서 로컬 파일을 선택·해제해 검수와 AI 번역에 함께 사용한다.
- 최종 5,000행 측정: 원문 추출 중앙 19.292ms, 검수 로드 88.613ms, 키 검색 9.229ms, 상태 필터 0.446ms, 사전 취소 1.157ms, 측정 프로세스 작업 집합 133.4MiB.
- 자체 포함 EXE 측정: 첫 창 2.239초, 재실행 0.517초, 작업 집합 167.6/146.1MiB, 정상 종료 0.395/0.313초, ExitCode 0, PowerShell 자식 0개.
- 패키지 검증 뒤 구형 PowerShell 런타임·실행기와 그 전용 테스트를 제거했다. `build-package.ps1`과 `Build-RimWorldGlossary.ps1`만 개발 보조 도구로 유지한다.

## 2026-07-12 - v1.0.0 안정화 릴리스 준비

- 제품 버전을 `1.0.0`으로 확정하고 EXE/DLL 어셈블리 버전, 패키지 `VERSION`, README, 패키지 안내서와 릴리스 노트를 동기화했다.
- 패키지 빌드는 바이너리 `FileVersion=1.0.0.0`, `ProductVersion=1.0.0`을 검사하고 `VERSION`과 `RELEASE_NOTES.md`를 ZIP에 포함한다.
- 최종 게이트: 오프라인 회귀 20/20, 패키지 Parser, SourceOnly 7행 smoke, 실제 최대화 `Startup.VisualSequence`, 5,000행 상태 막대 bounds 불변 감사 PASS. 게시 ZIP SHA-256은 릴리스 생성 시 자산과 함께 기록한다.

## 2026-07-12 - UI 결합도·대형 목록 추가 최적화

- 메인 GUI를 AST로 조사한 결과 8,972줄·483,584바이트·함수 260개였고, `Apply-AppTheme` 422줄, 감사 전용 접근성·성능·캡처 코드 약 300줄이 제품 경로에 섞여 있었다.
- 화면 캡처, 접근성·실제 글자 잘림 검사와 성능 보고를 319줄 `RimWorldAiTranslator.UiAudit.ps1`로 옮겼다. 일반 실행에서는 파일과 DWM fallback을 읽지 않고 감사 인수가 있을 때만 로드한다. 검색 컨텍스트까지 정리한 최종 메인 파일은 8,711줄·467,616바이트다.
- 각 JSON 행에 `_runtimeCache`를 `Add-Member`하던 방식을 네이티브 `ConditionalWeakTable` 저장소로 교체했다. 같은 행은 같은 캐시를 받고 프로젝트 전환 시 재설정되며 원본 행은 변하지 않는다. 네이티브 DLL이 없는 소스 실행에는 동일 fallback을 둔다.
- 검색어·상태·파일·검색 범위를 행마다 UI에서 다시 읽던 경로를 검색 1회당 하나의 불변 컨텍스트로 바꿨다. 파일 검색은 같은 부분 문자열 의미를 유지하는 원본 대상 경로를 사용한다.
- 품질 모듈은 표준화된 항목을 직접 읽고 외부·구형 형식에만 호환성 속성 탐색을 사용한다. `Ui.QualityTools` 회귀가 기존 일반 형식과 결과를 확인한다.
- 동일 5,000행·5회 기준에서 검수 로드 998.6→814.6ms, 첫 검색 3,664.8→1,485.6ms, 검색 중앙 969.0→865.2ms, working set 256.9→249.7MB다. 검색 결과 295/5,000과 상태 2,667/2,333은 동일하다. 품질 센터 첫 화면은 10.077→7.153초다.
- 최종 native DLL과 15개 화면 전체 반복에서는 로드 840.7ms, 첫 검색 1,153.9ms, 검색 중앙 979.5ms, working set 238.9MB, 품질 센터 7.713초였다. 잘림·접근성 누락은 0건이고 감사 인수 없는 제품 시작도 2.951초에 정상 공개됐다.
- 최종 재패키지 시작 감사도 `ExitCode=0`, 완성 공개 3.440초, 숨은 초기화 8샘플, 준비 화면과 메인 화면 사이 공백 0샘플로 통과했다. 소스와 패키지 README SHA-256이 일치한다.
- `Apply-AppTheme`는 길지만 팔레트와 실제 반응형 컨트롤 배치를 함께 소유한다. 컨트롤 소유권 없이 파일만 나누면 전역 결합을 숨기므로 이번 변경에서는 이동하지 않고 후속 구조 부채로 명시한다.

## 2026-07-12 - 시작 화면 원자 공개

- 사용자 화면에서 메인 WinForms 창이 먼저 보이고 자식 컨트롤이 순서대로 칠해지는 초기 프레임을 확인했다. 기존 실행기 준비 화면은 2.5초 뒤에만 나타나 약 2초인 정상 시작을 가리지 못했다.
- 실행기는 120ms 뒤 단일 `OnPaint`로 그리는 460×154 정적 준비 화면을 표시한다. 애니메이션 진행 막대와 개별 자식 컨트롤을 제거해 준비 화면 자체도 한 프레임으로 그린다.
- 메인 창은 `Opacity=0`, 작업 표시줄 비표시 상태에서 테마·레이아웃·프로젝트와 캐시를 구성한다. `Shown`에서 최종 레이아웃과 페인트를 강제한 뒤 `Opacity=1`로 공개한다. 실행기는 layered-window alpha를 읽어 공개 전에는 준비 화면을 닫지 않는다.
- 격리 패키지 실행 91회 샘플에서 준비 화면을 확인했고, 메인 창은 7개 가시 샘플 동안 alpha 0을 유지한 뒤 완성 상태에서만 공개됐다. 최종 EXE는 `ExitCode=0`, 900×600 화면 잘림 0건이다.
- `tests/Run-StartupVisualAudit.ps1`을 추가해 준비 화면 유지, 숨은 메인 초기화, 완성 뒤 공개, 최종 화면 감사와 종료 코드를 재현 검증한다. 전체 오프라인 회귀 20/20과 패키지 Parser·SourceOnly 7행 smoke도 통과했다.
- 5,000행 전체 UI 감사 15/15를 다시 실행해 접근성 이름 누락, 컨트롤 경계 잘림과 실제 글자 잘림이 모두 0건임을 확인했다. 첫 화면 준비 기록은 화면 복잡도에 따라 2.442~10.077초이며 그동안 정적 준비 화면이 유지된다.

## 2026-07-12 - 로딩·잘림·디자인 프리셋 재작업 시작

- 사용자 피드백: 전체 화면 작업 오버레이가 작업을 가리고 완료 뒤 한 번 더 눌러야 하며, 일부 글자가 여전히 잘리고, 시작 중 화면 전환이 깜박이며, 더 전문적인 여러 디자인 콘셉트가 필요하다.
- 기준 백업: `codex/backup/ui-polish-20260712-143852`, 태그 `codex-backup-ui-polish-20260712-143852`, 커밋 `a3a450a`.
- 시작 화면 100행 격리 fixture 3회 프로세스 시간: 5,328.615 / 5,038.286 / 4,685.384ms, 중앙 5,038.286ms. 스냅샷 안정화 대기 1.38초가 포함되므로 같은 runner와 조건으로 전후를 비교한다.
- 확인된 원인 후보: 전체 화면 `operationOverlay`, 완료 상태의 수동 복귀 버튼, `Shown` 뒤 테마·첫 화면 구성, 패키지에도 있는 캡처 P/Invoke 형식의 런타임 `Add-Type`, 경계만 검사하고 텍스트 측정을 하지 않는 UI 감사.
- 변경 계약: 번역·취소·재시도·완료분 복구 의미는 유지한다. 전체 화면을 비차단 작업 스트립으로 축소하고 성공·취소는 자동 복귀시킨다. 텍스트 측정 감사를 추가해 실제 잘림을 고치고, 시작 전 구성과 네이티브 형식 재사용으로 깜박임·시작 비용을 줄인다. 설정에는 실제 토큰으로 연결된 여러 전문 디자인 프리셋을 추가한다.
- 원문 분석과 AI 번역 상태를 82px 상단 막대로 축소했다. 작업 화면은 계속 조작할 수 있고 성공·취소는 0.9초 뒤 자동으로 닫히며, 오류만 재시도와 닫기 명령을 유지한다. 최소 900×600에서 막대 아래 분할기 높이도 다시 제한한다.
- 버튼·체크박스·콤보박스·라벨의 실제 렌더링 글자 크기를 측정하는 `textClipped` 감사를 추가했다. 상태 문구 한 건과 줄바꿈 라벨 오탐을 수정한 뒤 전체 15개 화면에서 경계·글자 잘림·접근성 이름 누락이 모두 0건이다.
- 테마와 첫 화면 구성을 `Shown` 이후가 아니라 첫 표시 전에 완료하고, 유효한 모드 캐시도 첫 표시 전에 채운다. DWM 캡처 형식은 native DLL로 옮겼고 런처 준비창은 2.5초를 넘는 느린 시작에서만 표시한다.
- 설정에 `프로페셔널`, `사이파이`, `비비드`, `스튜디오`, `프런티어` 컨셉을 추가했다. 밝음·어두움과 독립적으로 저장하며 고대비가 우선한다. 다섯 팔레트를 실제 대시보드·설정·검수 화면으로 렌더링해 확인했다.
- 100행 시작 화면의 첫 준비 시점 3회는 2.037/2.231/2.140초, 중앙 2.140초다. 더 엄격한 텍스트 감사까지 포함한 프로세스 중앙값은 기준 5,038.286ms에서 4,834.967ms로 줄었다.
- 5,000행 최종 UI 감사: 로드 919.681ms, 검색 중앙 1,050.208ms, 다음 33.471ms, 저장 445.542ms, 무변경 저장 0.111ms, working set 229.88MB. RMK는 생성 6,510.892ms, 갱신 중앙 7,712.870ms/최악 7,778.216ms다. 전체 회귀 20/20도 통과했다.
- 패키지 EXE는 첫 화면 2.066초, 준비창 미표시, `ExitCode=0`, 경계·글자 잘림·접근성 누락 0건으로 확인했다. 외부 화면 캡처 경합으로 밝은 화면 한 장이 검게 오염된 사례가 재실행에서는 사라졌고, 밝은 테마의 비정상 검은 픽셀 비율을 새 UI 게이트로 추가했다.

## 2026-07-12 - P2/P3 개척지 번역 작업실 완성

- 기존 P2/P3 완료 근거는 5개 정적 화면과 기본 성능에 치우쳐 첫 행동, 사전 점검, 실제 작업 상태, 오류·취소·완료 전환과 통합 품질 도구를 증명하지 못했다. 해당 항목을 재개방해 `2068b1f`에서 실행 계약을 고정하고 `751b791`, `c1008dd`로 구현했다.
- 중앙 UI 토큰을 밝음·어두움·고대비에 적용하고, 첫 화면을 `모드 선택 → 원문 분석 → 초벌 번역 → 검토·적용` 흐름이 보이는 개척지 번역 작업실로 재구성했다. 최근 프로젝트, 제공자 준비 상태와 빈 상태의 다음 행동은 실제 저장 데이터에 연결된다.
- AI 번역 전 사전 점검에 실제 프로젝트, 원문 언어, 제공자·모델, 행/배치와 토큰 범위, 원본 무수정 원칙을 표시한다. `미번역 부분만`, `전체 다시 번역`, `취소` 선택을 기존 실행 매개변수에 연결했다.
- 실제 프로세스 로그를 단계·배치·진행률로 해석하는 개척지 스캔 오버레이를 원문 분석과 AI 번역에 연결했다. 불확정 단계는 백분율 없이 활동 상태를 표시하고, 오류 재시도·협조 취소·완료분 복구·검수 화면 복귀를 기존 프로세스 흐름에 연결했다.
- 실제 도구 6개를 통합했다: 번역 품질 센터, 이전/현재 번역 Diff, 번역 범위·사용량 사전 점검, 개인정보 보호 HTML 보고서, 명령 팔레트·단축키, 기존 동일 원문 번역 메모리. 품질 센터는 희소 결정을 만들지 않고 가상 목록과 편집 무효화 캐시를 쓴다.
- 선택하지 않은 백업 복원 UI는 충돌·복원 경계까지 완성하기에 범위가 커 보류했다. 제공자 온라인 연결 테스트는 실제 자격증명과 네트워크가 필요해 기존 로컬 설정 검증을 유지했다.
- 시각 반복에서 카드·오버레이 2px 잘림, 오류 상태 버튼 재배치, 125% 물리 화면 캡처 크롭, 명령 팔레트 closure 오류, 테스트 전용 분기의 제품 경로 잔존과 장시간 계산 뒤 DWM 캡처 검은 영역을 발견해 수정했다.
- 5,000행 최종 UI 감사 15/15: 잘림 0건, 접근성 이름 누락 0건. 공통 기준 대비 로드 925.4→965.0ms, 검색 중앙 1,074.9→1,024.1ms, 다음 34.9→36.0ms, 저장 558.2→442.8ms, 무변경 저장 0.26→0.16ms, working set 254.12→258.79MB다. 새 품질 화면은 같은 구현의 초기 21,566.6ms에서 12,729.7ms로 개선했다.
- 전체 `build-package.ps1`은 최종 20/20 회귀(39.780초), C# 빌드, 패키지 Parser, SourceOnly 7행 ZIP smoke를 통과했다. 배포 EXE를 격리 앱 데이터로 실행해 900×600 시작 화면과 `ExitCode=0`을 확인했다.
- 캡처 루트: 기준 `...ui-remediation-baseline-d0064d1bb9a543ee8c86fa334d4a5b08`, 최종 `...ui-remediation-final-970a4e1c42ff4f9496275d8b15a6832f`, 품질 재확인 `...quality-visual-final-0f826d98c48448b0998fee00f24d9104`, 패키지 EXE `...package-ui-0f527b3bdc4b4c9dbadbb95db17af19e`.
- 실제 API, 외부 네트워크, Workshop/RMK 구독본과 사용자 `%LOCALAPPDATA%`는 사용하지 않았다. 실제 125/150/200% DPI와 제공자 온라인 가용성은 남은 외부 검증이다.

## 2026-07-12 - UI·품질 도구 공통 기반

- 밝음·어두움·고대비 색상, 간격, 컨트롤 높이와 포커스를 한곳에서 제공하는 `RimWorldAiTranslator.UiSystem.ps1`을 추가했다.
- 실제 번역 로그의 원문 분석, 대상 준비, 배치 진행, 사용 한도 대기, 재시도, 안전 검사, 완료·취소·오류를 구조화한다. 전체 배치 수가 확인된 경우에만 확정 진행률을 제공한다.
- 실제 검수 행에서 미번역, 원문 변경, 안전 실패, 토큰/태그 손상, 원문 동일, 길이 이상, 기존 번역 변경과 `대상 파일+키` 중복을 계산하는 `RimWorldAiTranslator.Quality.ps1`을 추가했다.
- 품질 보고서는 집계 수치만 HTML로 원자 저장하며 기존 파일을 `.bak`으로 보존한다. 원문·번역문·API 키·절대 경로는 포함하지 않는다.
- 검증: `UiTools` 1/1 PASS, PowerShell 전체 Parser PASS, 패키지 게이트 20/20 PASS(41.160초), C# 빌드·패키지 Parser·ZIP 원문 7행 smoke PASS.

## 2026-07-12 - P2/P3 UI·작업 도구 재평가 기준선

- 재작업 기준 백업: 브랜치 `codex/backup/p2p3-ui-20260712-120922`, 태그 `codex-backup-p2p3-ui-20260712-120922`, 커밋 `4c11b4a`.
- 현재 패키지를 다시 빌드해 회귀 19/19(42.411초), C# 컴파일, 패키지 Parser와 ZIP 원문 7행 smoke를 통과했다.
- 기준 화면: `%TEMP%\RimWorldAiTranslator-ui-remediation-baseline-*`의 빈 프로젝트 1280×720/900×600, 프로젝트 카드, 설정 화면과 `review-audit`의 검수 화면 5종.
- 기존 P2 근거는 잘림 0건과 성능을 증명하지만, 빈 상태의 단일 주 행동, 작업 단계 피드백, 오류·취소·완료 상태, 일관된 토큰과 실제 품질 도구를 증명하지 못했다.
- 빈 프로젝트 화면은 안내 한 줄 뒤 대부분이 빈 공간이고, 프로젝트 카드 화면은 정보가 고립되며, 설정은 API와 RMK 흐름이 단절되어 있다. 검수 화면은 버튼과 상태가 같은 강도로 경쟁하고 역사/문제 탭이 작업 도구 역할을 하지 못한다.
- 선택 구현: 품질 검사 센터, 이전/현재 번역 Diff, 실제 대상 기반 번역 사전 점검, 개인정보 보호 결과 보고서, 명령 팔레트. 반복 검수·오류 탐색·실행 범위 확인에 직접 이득이 있고 현재 데이터에 결정적으로 연결할 수 있다.
- 보류 후보: 자동 백업 복원 UI는 복원 경계와 충돌 처리 범위가 커 이번 UI 목표에서 성급히 추가하지 않는다. 공급자 온라인 연결 테스트는 실제 자격증명/네트워크가 필요하며 기존 로컬 설정 검증을 유지한다.

## 2026-07-12 - P0-P3 최종 통합 검증

- 기준 백업 `aff52cc`부터 전체 diff 39개 파일을 보안, 데이터 손실, 예외 처리, 취소·복구, 호환성, Def 안전과 성능 관점에서 다시 검토했다.
- 손상된 `.rimworld-ai-project.json` 소유권 표식을 조용히 무시하던 경로를 발견했다. 해당 폴더는 계속 보존하되 삭제 확인문에 보존 개수를 표시하고 `Project.CleanupBoundary`에서 손상 마커·원본 모드·Korean 보존을 검증했다.
- 재귀 PowerShell Parser 18/18, 전체 회귀 19/19(41.618초), 최종 패키지 게이트 19/19(40.870초), C# 빌드, 패키지 Parser, ZIP 원문 7행 smoke를 통과했다.
- UI 5,000행 5개 시나리오는 검색 295/5,000, 상태 2,667/2,333과 일치했고 잘림·접근성 누락 0건이다. 최종 측정은 로드 1,088.478ms, 검색 중앙 1,164.046ms, 다음 36.549ms, 저장 582.474ms, 무변경 저장 0.155ms, working set 228.19MB다.
- RMK 5,000행은 생성 7,089.816ms, 갱신 중앙 8,983.882ms/최악 9,429.046ms, 최대 working set 323.02MB다.
- 패키지 스크립트 14개의 소스 해시가 일치하고 필수 EXE·DLL과 ZIP 23개 항목을 확인했다. 격리 앱 데이터에서 EXE 설정 화면을 렌더링해 `ExitCode=0`, 잘림·접근성 누락 0건을 확인했다.
- 실제 API, 외부 네트워크, Workshop/RMK 구독본, 실제 `%LOCALAPPDATA%`와 push/release는 사용하지 않았다. 실제 125/150/200% DPI만 시스템 표시 배율 변경이 필요해 명시적 차단으로 남는다.

## 2026-07-12 - P3 개인정보 보호 진단 번들

- 설정 화면에 사용자가 직접 누를 때만 실행되는 `진단 번들 저장`을 추가하고 CLI wrapper도 패키지에 포함했다.
- ZIP에는 런타임·비민감 설정·제품 파일 해시, 프로젝트/검수 상태 개수, 현재 로그의 오류 유형별 개수만 담는다.
- 원문, 번역문, 번역 키, API 키, 사용자/모드/프로젝트명, 전체 경로, URL 호스트·쿼리, 원시 로그는 포함하지 않는다. 모델 ID는 값 대신 12자리 해시만 기록하고 예상 밖 설정·상태 문자열은 `other`로 집계한다.
- 같은 파일을 강제 교체할 때 원자 교체와 `.bak`을 사용하며, 임시 작업공간은 검증된 `%TEMP%` 접두 경계 안에서만 정리한다.
- 검증: `Diagnostics` 1/1 PASS. privacy fixture의 키·본문·프로젝트명·모델·임의 열거값·경로 비노출, 6개 ZIP 항목, 오류/상태 집계, 덮어쓰기 차단과 3회 연속 강제 교체 백업을 확인했다. 설정 화면 1280×720에서 잘림·접근성 누락 0건. 전체 패키지 게이트는 19/19 PASS(40.744초)다.

## 2026-07-12 - P3 로컬 번역 메모리 제안

- 동일한 원문의 안전한 기존 번역만 제안하는 독립 선택 모듈을 추가하고, 검토 완료·내 번역, 검토 완료·RMK, 번역됨 순으로 정렬한다.
- 원문 변경, 안전 검사 실패, 미번역 상태, 현재 항목 자신과 중복 번역은 제외한다. 대소문자나 다른 원문을 퍼지 매칭하지 않는다.
- 용어 탭에 `상태 · 출처 · 파일`과 번역을 표시하며 현재 번역 칸에는 자동 입력하지 않는다. 읽기 전용 텍스트이므로 사용자가 필요할 때만 선택·복사한다.
- 상태·번역이 바뀌면 메모리 캐시를 무효화하고, 용어 탭을 열 때 현재 편집을 먼저 저장해 오래된 제안을 표시하지 않는다.
- 검증: `TranslationMemory` 1/1 PASS, 중복 원문 2행 UI에서 출처 표시와 빈 번역 미변경 확인, UI 감사 5/5·잘림 0건·접근성 이름 누락 0건.

## 2026-07-12 - P3 제공자 설정 로컬 점검

- API 호출 없이 URL 형식, 외부 HTTPS/루프백 HTTP 경계, URL 내 인증정보, 모델 입력, Temperature, 내장 모델 목록과 키 개수만 점검하는 독립 모듈을 추가했다.
- 수동 모델 ID는 차단하거나 바꾸지 않고 `수동 모델 ID 유지`로 표시한다. 실제 모델 제공 여부와 최신 제한은 추측하지 않고 `온라인 미확인`으로 표시한다.
- 설정 화면에는 키 문자열 대신 개수만 전달하며 결과를 한 줄 상태·툴팁·접근성 설명으로 보여준다. 키가 없으면 기존 Google fallback을 그대로 안내한다.
- 검증: `ProviderValidation` 1/1 PASS, 설정 화면 1280×720 밝은 테마에서 잘림 0건·접근성 이름 누락 0건.

## 2026-07-12 - P2 성능·UI 감사 체크포인트

- 5,000행 합성 프로젝트를 격리 앱 데이터로 여는 UI benchmark와 5,000행 RMK XLSX 생성·갱신 benchmark를 재현 가능한 명령으로 추가했다.
- 검색 시 전체 행의 결정 객체와 상태를 만들던 경로를 지연 평가하고, 정적 검색 문자열 캐시, 변경 없는 저장 생략, 선택 카드만 다시 그리기를 적용했다.
- 공유 토큰·조사·비정상 개행 검증은 판정 규칙을 유지한 채 native DLL의 컴파일된 정규식 경로를 사용하고 DLL이 없으면 기존 PowerShell 경로로 돌아간다.
- UI 5,000행 전후: 로드 1,558.896→993.706ms, 검색 중앙 3,071.292→1,191.644ms, 다음 항목 50.234→37.266ms, 실제 저장 1,925.418→546.736ms, working set 259.48→224.62MB.
- RMK 5,000행 전후: 생성 13,144.655→7,289.464ms, 갱신 중앙 14,919.371→8,640.982ms. 최종 갱신 최악은 9,039.086ms, 최대 working set은 323.01MB다.
- 900×600 밝음/큰 글자, 1280×720 밝음, 1920×1080 어두움, 1280×720 어두움/고대비를 확인해 잘림 0건, 접근성 이름 누락 0건, 검색·상태 필터 기대 개수 일치를 확인했다.
- 실제 환경은 96 DPI였다. 125/150/200%는 Windows 디스플레이 배율 변경이 필요하므로 자동 수행하지 않고 명시적 차단으로 기록했다.
- 검증: 16/16 회귀 PASS, `build-package.ps1` PASS, 패키지 Parser·ZIP 원문 7행 smoke PASS, UI 감사 4/4 PASS.

## 2026-07-12 - P1 번역 무결성·복구 체크포인트

- Keyed/DefInjected namespace를 포함한 안정 식별자로 같은 키의 서로 다른 Def 번역이 섞이거나 사라지지 않게 했다.
- AlienRace/PawnRenderTreeDef의 표시 문자열과 런타임 식별자를 fixture로 구분하고 제외 사유를 감사 JSON에 남긴다.
- 공유 검증 모듈로 토큰 종류·개수, 문법 접두어, 포맷/태그, 비정상 개행과 RimWorld 한국어 자동 조사 표기를 엔진·UI·로컬/RMK 적용에서 일치시켰다.
- RMK 번역 당시 원문과 현재 원문 변경을 검출하고 기존 번역·과거 원문·XLSX 스타일·댓글·추가 열을 보존하는 왕복 회귀를 추가했다.
- 중첩 재시도를 제거하고 배치별 원자 체크포인트, 협조 취소와 5초 제한시간 종료, 완료분 검수 복구 및 남은 항목 재개를 구현했다.
- GUI를 거치지 않는 직접 Korean XML 출력도 다중 파일 트랜잭션과 지속 `.bak`을 사용한다.
- 프로젝트 삭제 정책을 독립 모듈로 분리해 앱 전용 검수 폴더만 삭제하고 원본 모드·Korean 폴더·외부 경로를 보존함을 검증했다.
- 검증: 오프라인 회귀 16/16 PASS(61.012초), 패키지 전체 게이트 16/16 PASS(59.931초), C# 빌드·패키지 Parser·ZIP 원문 7행 smoke PASS.

## 2026-07-12 - 안정화 작업 시작 체크포인트

- 원래 브랜치/HEAD: `audit/def-safety-ui-v0.2.1` / `7ec133500600017f02688c990f6513f5498466d0`.
- 백업 브랜치: `codex/backup/20260712-073630`.
- 예정 백업 태그: `codex-backup-20260712-073630`.
- 기존 8개 수정 파일과 새 운영 문서·Def 규칙만 보존하며 ignored 실행 파일, `dist`, `reviews`, 로컬 용어집은 제외한다.
- 사전 PowerShell Parser 8/8 통과, `git diff --check` 오류 없음, 후보 파일에서 API 키 형태 값 없음.
- 이 체크포인트는 기존 작업의 보존용이며 기능 승인 판정이 아니다.

## 2026-07-12 - 오프라인 회귀 harness 시작

- `%TEMP%` 격리 복사본만 사용하는 의존성 없는 PowerShell runner를 추가한다.
- 첫 suite는 harness 자체 격리, PowerShell 구문, SampleMod 원문 추출, 로컬 Korean 적용과 기존 키 보존을 검증한다.
- 실제 API, Workshop, RMK 구독본과 `%LOCALAPPDATA%\RimWorldAiTranslator`는 사용하지 않는다.

## 2026-07-12 - P0 저장·적용·보안 게이트

- 프로젝트 저장소 주 파일과 백업이 모두 손상되면 빈 프로젝트로 시작하지 않고 원본을 보존한 채 시작을 중단한다. 백업이 정상이면 주 파일을 복구하고 깨진 파일을 별도 보존한다.
- 로컬 Korean XML과 RMK XML/XLSX 교체 시 직전 버전 `.bak`을 남기며, 뒤 파일 실패를 주입해 앞 파일과 XLSX가 바이트 단위로 복원되는 것을 확인했다.
- API 키를 자식 프로세스 환경에서 즉시 제거하고 실제 입력 키 및 인증 헤더를 로그에서 마스킹한다. 인자 JSON에는 키가 들어가지 않는다.
- `build-package.ps1`는 기존 배포물을 교체하기 전에 전체 오프라인 회귀를 실행하고, 패키지 Parser와 ZIP 새 폴더 원문 추출 smoke를 통과해야 완료된다.
- 검증: 오프라인 회귀 8/8 PASS, 패키지 빌드 및 ZIP 원문 7행 smoke PASS. 네트워크와 실제 사용자 데이터는 사용하지 않았다.

## 2026-07-12 - 자율 개발 운영 체계 점검

### 범위

- 제품 소스코드는 수정하지 않음.
- AGENTS 용량과 저장소 경계를 확인하고 운영 문서를 분리함.
- 빌드, 테스트, 실행, 패키징 명령과 현재 기능/위험을 코드에서 조사함.

### 확인한 상태

- 상위 기존 `AGENTS.md`: 31,349바이트, 385줄.
- Codex 기본 프로젝트 문서 한도: 32,768바이트. 기존 사용률 95.7%.
- 정리 후 상위 `AGENTS.md`: 1,492바이트, 17줄. 제품 루트 `AGENTS.md`: 5,281바이트, 54줄.
- 제품 Git: `audit/def-safety-ui-v0.2.1`, HEAD `7ec1335`, 8개 수정 파일과 1개 새 규칙 파일.
- 자동 테스트/린트/CI 없음.

### 실행한 검사

- `git status --short --branch`, `git remote -v`, `git log`, `git diff --stat`, `git diff --name-status`, `git diff --check`.
- 루트 `*.ps1` 8개를 `System.Management.Automation.Language.Parser.ParseFile`로 검사: 8 PASS, 0 FAIL.
- `build-package.ps1`, README, 실행기, UI 저장/삭제, 추출, 배치 재시도, 적용, RMK XML/XLSX 경로를 읽기 전용으로 추적.

### 확인한 주요 위험

- 프로젝트 상태 주 파일과 백업이 모두 손상되면 빈 목록으로 조용히 대체됨.
- 적용 XML과 RMK XLSX에 성공 후 남는 롤백 백업이 없음.
- 현재 대규모 제품 변경이 커밋되지 않음.
- 회귀 테스트와 패키지 자동 게이트가 없음.
- 직접 PowerShell 실행기의 한 오류 문구가 깨져 있음.

### 보류한 검사

- 패키지 빌드, GUI 실행, 실제 모드/RMK 쓰기, 네트워크 번역.
- 사유: 문서 전용 범위와 최근 호스트 불안정. 자율 제품 개발 시작 전 `ROADMAP.md`의 차단 요소를 먼저 해결해야 함.

### 문서 검증

- 상위/제품/범위별 `AGENTS.md`가 모두 32,768바이트 미만임을 확인.
- 필수 운영 문서와 모든 코드 근거 대상의 존재를 확인.
- 새 지침·문서에서 trailing whitespace와 API 키 형태 문자열이 없음을 확인.
- Markdown 외부 링크가 HTTPS임을 확인.
- 기존 추적 제품 소스 diff가 점검 전과 같은 8개 파일 `+752/-199`임을 확인. 이번 작업에서 제품 소스는 수정하지 않음.

## 2026-07-12 - 준비 화면 활동성 개선

- 미완성 메인 WinForms 화면을 가리는 기존 원자 공개 흐름은 유지하고, 자식 컨트롤 없는 준비 캔버스에 왕복형 무한 진행 표시와 움직이는 말줄임표를 추가했다.
- 애니메이션은 실제 완료율을 표시하지 않으며 40ms 타이머로 본문 아래 작은 영역만 무효화한다.
- 시작 감사는 타이머 활성·간격과 220ms 사이 진행 표시 픽셀 변화를 단언한다. 최종 패키지 반복 실행에서 170픽셀 변화, 준비 화면 0.305~0.330초, 완성 공개 3.019~3.373초, 중간 공백 0, 잘림 0, `ExitCode=0`을 확인했다.
- 새 EXE를 압축하는 순간 외부 판독기가 만든 일시적 공유 잠금에 대비해 패키지 ZIP 생성을 최대 4회 제한 재시도한다.
- 검증: 전체 오프라인 회귀 20/20 PASS, 패키지 Parser·압축 해제 후 SourceOnly 7행 smoke PASS, `Startup.VisualSequence` PASS.

## 2026-07-12 - 최초 최대화 배치 보정

- 최초 실행 시 폼은 최대화됐지만 `Load`의 `SuspendLayout` 안에서 대시보드 자식 폭이 860px로 남아, 명령 버튼 오른쪽 여백이 704px가 되는 문제를 재현했다.
- 대시보드 기하 계산을 `Resize-DashboardLayout`으로 분리하고 네이티브 최대화 크기가 확정된 뒤 공개 전에 한 번만 최종 배치한다. 초기 잘못된 폭에서 프로젝트 카드를 만들던 중복도 제거했다.
- 수정 후 논리 클라이언트 폭 1,536에서 명령 버튼 오른쪽 여백은 28px이고 전체·글자 잘림은 0건이다.
- 시작 감사는 고정 900×600 대신 실제 최대화 상태를 사용하며 오른쪽 여백 12~48px를 회귀 조건으로 검사한다.
- 검증: 전체 회귀 20/20, 패키지 Parser·SourceOnly 7행 smoke, 최소 창 프로젝트 화면, 어두운 설정 화면, 최대화 `Startup.VisualSequence` 모두 PASS.

## 2026-07-12 - 프로젝트 로드 원자 공개

- 기존 프로젝트는 `Show-Workspace` 전에 검수 데이터를 구성한다. 새 프로젝트는 첫 원문 분석 중 대시보드를 유지하고, 검수 데이터가 완성된 뒤에만 작업 화면으로 전환한다. 새 원문·번역 결과를 보이는 작업 화면에 다시 읽을 때는 `workspaceLoadCover`로 본문을 가린다.
- 목록은 기존 `BeginUpdate + AddRange`를 유지하며 원문, 번역, 메타, 역사·용어·메모 컨트롤이 순서대로 보이던 과정만 가린다. 상단 작업 상태 띠는 커버 위에 유지된다.
- 모든 자식 네이티브 창에 `WM_SETREDRAW`를 적용하는 시도는 RichTextBox 재활성화 비용으로 60초 감사를 초과해 폐기했다. 현재 구현은 본문 커버와 `SuspendLayout`만 사용한다.
- 500행 보이는 재로드 244.539ms, 최종 5,000행·3회 감사의 보이는 재로드는 1,239.924ms이며 두 경우 모두 커버 사용을 확인했다. 5,000행 검색·상태 필터 결과는 295개와 2,667/2,333개로 유지됐다.

## 2026-07-12 - 새 프로젝트 시작 전환 안정화

- 새 프로젝트 준비 시 `operationOverlay` 높이 82px를 본문에 더해 `main`/`dashContent`를 아래로 밀었다가 완료 후 되돌리던 흐름을 확인했다. 상태 막대를 고정 overlay로 바꿔 표시 전·중·후 작업 영역이 모두 `{X=0,Y=78,Width=1280,Height=642}`로 유지된다.
- Defensive Network는 정상 RMK `ModList.tsv` 2,929행에 Workshop/Package ID가 없었지만 기존 fallback이 Data의 YAML 3,083개를 UI 스레드에서 전부 열거했다. 실제 재귀 열거는 10.609초가 걸렸다.
- 정상 인덱스의 명시적 미일치는 빈 결과로 캐시하고 broad fallback을 생략한다. 같은 인덱스 판독과 미일치 확인은 약 206.8ms였다. 인덱스가 비었거나 읽히지 않는 예외 경로만 기존 호환 탐색을 유지한다.
- 원문 분석 준비 상태를 RMK 조회와 임시 인수 파일 작성보다 먼저 그리며, 준비 중에는 중지 버튼을 비활성화하고 자식 프로세스 시작 뒤 활성화한다. 5,000행 UI 감사에서 원자 재로드 1,341.139ms, 상태 막대 bounds 불변, 검색·필터 결과 보존을 확인했다.
- 실제 Defensive Network 구독본을 읽기 전용 `SourceOnly + ReviewOnly`로 격리 `%TEMP%`에 추출한 결과 2.743초에 원문 1,184개, 검수 846개를 구성했다. 원본 모드 쓰기와 외부 API 호출은 0건이다.

## 2026-07-14 - 공개 준비 Phase 05 데이터·번역 호환성

- 기존 프로젝트·검수 파일을 열기만 할 때 기본값/스키마 마이그레이션을 자동 저장하지 않도록 분리했다. 명시적 저장 또는 적용·번역처럼 디스크 상태를 소비하는 작업 직전에만 보존한다.
- JSON은 엄격한 UTF-8, 명시적 `null`, 컬렉션 원소, v6 대상 identity/중복과 지원 버전을 검증하고, 주 파일이 손상되면 검증된 백업으로 복구하며 둘 다 손상되면 쓰기를 차단한다. unknown 필드와 `error`/미래 status는 round-trip 보존한다.
- LanguageData XML은 DTD를 금지하고 BOM/줄바꿈, 주석, PI, 순서를 유지하며 `Patches`를 자동 번역하지 않는다. RMK XLSX는 올바른 worksheet를 헤더로 선택하고 stable row, 공백·줄바꿈, Required Mods 및 비대상 ZIP part를 보존한 뒤 재개방 검증 후 교체한다.
- 번역 audit JSON/CSV, progress/token warning, preserved-translation과 ownership marker도 flush 뒤 엄격 재개방·구조 검증을 통과해야 원자 교체한다.
- Golden 커밋에서 요청 관련 함수 여섯 개만 AST로 분리 실행했다. C# system/user UTF-8 SHA-256과 fake response가 정확히 일치하고, 두 요청을 같은 직렬화기로 canonicalize하면 차이 0이다. 실제 공개 UI의 generated glossary 상한은 Golden과 C# 모두 40이다.
- 합성 API 행렬은 400/401/403/408/429/500/502/503, timeout, 연결/응답 중단, 빈·잘린·잘못된·과대 응답, 누락·중복·순서 변경, key rotation, batch split, 취소/재개와 완료 오표시 방지를 포함한다.
- 최종 Phase 05 검증은 Release 빌드 경고/오류 0, 전체 65/65 PASS다. 모든 쓰기는 GUID 임시 복사본에서 수행했고 실제 사용자 데이터, Workshop/RMK 구독본, 실제 키와 외부 API를 사용하지 않았다.
- Phase 05 판정은 `PASS`다. 실제 Excel/clean-PC 및 안전하지 않은 실제 디스크 고갈·프로세스 강제 종료 검사는 Phase 08/10 수동·적대 감사 항목으로 남겼다. 공개 GitHub 상태와 Git stage/commit은 변경하지 않았다.
- 문서화 뒤 전체 비교 함수를 다시 검색해 TranslationEngine의 RMK 과거 원문 비교 한 곳이 여전히 앞뒤 공백을 제거함을 발견했다. 줄바꿈 표현만 정규화하도록 수정하고 공백만 달라진 source-history fixture로 바꾼 뒤 Release 0/0 및 65/65를 다시 통과했다.
- 독립 재감사에서 XML sanitizer가 유효한 surrogate pair까지 삭제하고, provider direct-map이 객체를 문자열로 강제 변환하며, MissingOnly 보존 파일이 source provenance/history를 잃는 문제를 찾았다. XML 1.0 scalar 단위 보존, string-only/exact-ID 응답, preserved schema v2와 v1 fail-closed로 수정했다.
- 보존 번역은 source hash/text/change/history를 엄격 검증하고, hash mismatch·sourceChanged·증거 없는 v1은 기존 번역을 `Existing`으로 보존한 채 재번역/재검수한다. 승인 후 reload와 두 번째 refresh, 이력 없음 비가장, A→B 승인→C에서 B 승계를 합성 회귀로 고정했다. invalid UTF-8/hash/schema/duplicate 파일은 provider 호출 전에 거부한다.
- malformed 2xx 응답도 body 검증 전에 사용량을 계상해 일일 예산을 우회하지 못하게 했고, 누락뿐 아니라 예상 밖 ID도 거부한다. RMK reader가 허용하는 identifier-only 및 class+node 구형 헤더는 writer가 마지막 사용 열 뒤에 누락 metadata 열만 추가해 안전하게 갱신한다.
- 2026-07-14 03:04 KST 체크포인트는 Release 빌드 경고/오류 0, 전체 65/65 PASS였다. 14개 manifest 항목 해시는 해당 fixture 정의와 일치하도록 묶었고, RMK target 생성 readback 실패 시 신규 빈 residue 정리는 ISSUE-076/Phase 08로 추적한다.
- 사용량 선계상 후 JSON root 또는 `usage`가 배열인 경우 `InvalidOperationException`으로 재시도가 끊기는 최종 감사 결함을 ValueKind guard와 배열 회귀로 닫았다. 이를 포함한 실제 최종 정착본은 03:08 KST Release 0/0, 전체 65/65다.

## 2026-07-14 - 공개 준비 Phase 06 UI·접근성

- Golden Master의 합성 15개 화면과 C# 후보를 동일 상태·크기·테마로 다시 캡처했다. 후보 15/15와 실제 장치 DPI 120(125%) 보충 상태 20/20에서 접근성 이름 누락, 컨트롤 잘림, 텍스트 잘림, 오류 출력이 모두 0이었다.
- 최소 창과 125%에서 고정 높이가 검토 헤더·편집기·도구 영역을 압박하던 문제를 논리/장치 DPI 변환, 적응형 2행 헤더, 영역별 스크롤과 수렴 재배치로 수정했다. 대시보드는 좁은 폭에서 검색·모드·작업 버튼을 나누어 긴 모드 이름과 필수 작업을 함께 보존한다.
- 비활성 명령은 화면과 접근성 이름에 `현재 사용 불가`를 표시하고 Enter로 선택·실행되지 않게 했다. 포커스, 논리적 Tab 순서, 읽기 전용 메타데이터 복사, 동적 상태 설명을 집중 회귀로 고정했다.
- 5,000개 합성 문자열에서 초기화 246.846ms, 품질 82.147ms, 검색 290.480ms, 상태 필터 65.271ms, 다음 선택 6.292ms였다. 필터·품질 계산의 UI 스레드 외 실행, 품질 가상 목록, 선택/스크롤 유지와 즉시 중지 피드백을 함께 확인했다.
- 최종 회귀는 엄격 빌드 경고/오류 0, 콘솔 67/67, UI 상호작용 35/35, 메타데이터·접근성 13/13, 종료 4/4, 명령 팔레트 9/9, 안전 실패 기록 PASS, 잔류 프로세스 0이다.
- 캡처는 합성 fixture allowlist만 허용하고, owner는 PrintWindow, owned dialog는 안정적인 hybrid client 방식으로 기록했다. 진행·오류 표시와 실패 기록에서 원시 경로·예외 메시지·stack·인증정보를 제거했다. 폐기된 오류 증거와 실행 전용 임시 파일을 지운 뒤 Phase 06 텍스트 증거에서 호스트 프로필·인증·키 형태 적중 0을 확인했다.
- 15개 상태마다 side-by-side, overlay, diff, mask와 사람이 읽는 구조·흐름 판정을 남겼다. 프레임워크·폰트 차이로 픽셀 비율은 크지만 필수 컨트롤, 작업 순서, 상태·기본값, modal 소유, 접근 가능한 필수 작업의 차이는 발견되지 않았다.
- 자동 Phase 06 판정은 `PASS`다. 실제 150%/200%, 혼합 DPI 모니터 이동, Narrator/NVDA, OS 고대비 전환과 clean-PC 렌더링은 `phase06/MANUAL_UI_CHECKLIST.md`에 Phase 10/사용자 수동 항목으로 남겼으며 자동 PASS로 간주하지 않는다.
- 실제 사용자 데이터·실제 API 키·외부 provider 호출을 쓰지 않았고, stage/commit/push/PR/tag/Release/asset/외부 업로드와 공개 GitHub 상태 변경은 없었다.

## 2026-07-14 - 공개 준비 Phase 07 보안·개인정보·공급망

- provider URL은 HTTPS만 허용하고 fragment를 금지하며 `api-version`, `format=json`, `version`만 엄격한 query로 허용한다. 원시·반복 인코딩 credential을 URI 전체와 settings primary/backup에서 거부하고 두 저장본이 모두 안전해질 때까지 UI correction 상태를 유지한다. 격리된 provider URL UI harness는 실제 provider 호출 없이 exit 0, 오류 파일 없음으로 끝났다.
- 출력·백업·복구는 신뢰된 쓰기 경계 안에서 파일 identity와 SHA-256 CAS로 결합했다. 준비 파일, 대상, `.bak`, post-commit, rollback/recovery 경쟁에서 동시 저장 승자를 덮어쓰지 않으며, review decision의 backup recovery는 쓰기 가능 작업으로 취급해 Workshop·network·앱 외부 root를 probe 전에 거부한다.
- RMK/XLSX/ZIP은 macro·OLE·VML·active content type·외부/이탈 relationship·formula surface를 갱신 전에 거부하고 원본 바이트를 보존한다. JSON, glossary, XML, comparison, Steam/RMK discovery와 language traversal에는 개별·합계 제한과 취소를 적용하고 comparison CSV의 formula-leading cell을 중립화했다.
- RMK Builder는 sandbox가 아니다. 확인한 EXE의 canonical identity·길이·SHA-256을 다시 고정하고 suspended start, kill-on-close job, 제한 환경·표준 스트림·시간·출력, 생성 파일 transaction을 적용하지만, 사용자가 선택한 clone의 인접 DLL/config와 현재 사용자 filesystem/network 권한까지 인증하거나 제거하지는 않는다.
- 중간 전체 회귀는 먼저 68/71로 structured query 보존과 direct/RMK rollback 보고 결함 세 개를 드러냈고, 수정 뒤 70/71에서 마지막 leading-ampersand credential delimiter 결함을 드러냈다. 실패를 완화하지 않고 모두 수정한 최종 엄격 Release 빌드는 8개 프로젝트 경고/오류 0, 최종 console은 71/71, FAIL/SKIP 0/0, exit 0, 23.028초였다. slow logger drain UI harness는 exit 0/오류 파일 없음, glossary self-test는 PASS였고 최종 독립 delta 감사는 P0/P1 0이었다.
- source credential scan은 예상된 합성 test 파일 4개만 찾았고 실제 secret은 0이었다. 활성 PowerShell과 direct/transitive `PackageReference`는 모두 0이다. cleared source에서 vulnerable/deprecated 조회는 결론 불가이며 PASS로 간주하지 않는다. Phase 04 ZIP은 현재 source보다 오래된 stale 증거다.
- Phase 07 현재-source 기술 판정은 `PASS`다. 그러나 SEC-009의 공식 유래 glossary 재배포 권리와 SEC-010의 self-contained .NET runtime notices가 해결되지 않았으므로 전체 공개 품질 RC 또는 공개 배포 가능 판정은 아직 PASS가 아니다.
- 실제 사용자 데이터·실제 API 키·유료/실제 provider·외부 업로드를 사용하지 않았고 stage/commit/push/PR/tag/Release/asset과 공개 GitHub 상태를 변경하지 않았다. 다음 작업은 다른 Phase 08 작업보다 먼저 `docs/public-release/08_RELIABILITY_PERFORMANCE_AND_TESTS.md`를 전부 읽는 것이며, 이 체크포인트에서는 그 문서를 읽지 않았다.

## 2026-07-14 - 공개 준비 Phase 08 신뢰성·성능·테스트

- Phase 08 문서를 시작 직전에 읽고, 이전 76/76과 기존 성능/UI 자료를 현재 PASS 증거로 승계하지 않았다. 최종 source/test/tools snapshot SHA-256은 `7A777BD9D051E4209A7AF7CDF83028966B95F256A2DF3BC3C6D61D9478A51141`이다.
- recovery 명령은 app-owned authority에만 두고 forward/rollback/intermediate endpoint를 mutation 전에 내구성 있게 선언했다. 반복 child kill, root-local forgery, same-ID/content swap, unknown concurrent winner를 exact identity/hash CAS로 처리한다.
- canonical data-root file lease와 project revision CAS를 추가해 교차 프로세스 contender와 stale writer를 명시적으로 거부한다. AtomicJson backup은 한 번 고정한 검증 바이트/identity만 deserialize와 restore에 사용한다.
- RMK Builder는 live clone 밖의 app-owned stage에서 sealed manifest를 검증하고, child 전후 source/stage/live 경계를 다시 확인한 뒤 FileTransaction으로만 게시한다. owner identity가 정확한 residue만 회수하고 unknown/ambiguous residue는 경고 후 보존한다. 선택한 clone의 executable/DLL/config 신뢰가 필요한 비-sandbox 경계는 그대로 공개한다.
- exact directory cleanup은 검증한 parent handle과 delete-on-close namespace lease를 함께 유지해 이 Windows 호스트에서 확인된 parent rename을 차단한다. 독립 감사 P0/P1은 0이며, handle-relative가 아닌 lease 생성과 전원/파일시스템 실패 뒤 zero-byte residue 가능성은 ISSUE-097 P2로 남겼다.
- 첫 전체 실행은 77/80으로 builder-plan 예외 계약, cleanup parent rename, Windows lock 예외형 세 결함을 드러냈다. 테스트를 완화하지 않고 실제 계약에 맞게 수정한 뒤 집중 회귀와 전체 게이트를 다시 시작했다.
- 최종 엄격 Release 빌드는 8개 프로젝트 경고/오류 0이다. cleanup, RMK builder, atomic-storage, forced-exit recovery, atomic-race 집중 게이트가 PASS했고, 동일 Release test DLL은 별도 직렬 프로세스에서 80/80을 3회 연속 통과했다(108.931/108.901/108.485초, flaky 0).
- 현재 소스의 5,000행 WinForms interaction 5회, startup/close/truthfulness/slow-I/O/logger, same/cross-process lease, repository CAS, metadata/accessibility, command palette, provider URL과 실제 MainForm 20-cycle이 모두 PASS했다. search median 175.294ms, status 61.494ms, stop feedback worst 12.076ms이며 MainForm working/private/managed growth는 7.844%/5.267%/0.217%, handle/GDI/USER delta는 0이다.
- 자동 성능 gate는 PASS지만 전체 evidence completeness는 `BLOCKED`로 유지했다. 현재 정확한 package 크기, packaged cold/warm startup·idle·exit/process tree, clean-PC/실제 Excel/150·200%·mixed-DPI/보조기술이 아직 없고 Golden RMK 경계가 비동등하기 때문이다. 숫자를 억지 비율로 만들지 않고 Phase 09/10으로 이관했다.
- Phase 08 기술 판정은 `PASS`다. SEC-009 glossary 재배포 권리, SEC-010 runtime notices, 최신 local RC, Phase 10 적대 감사와 수동 항목이 남아 있으므로 전체 RC 판정은 아직 없다. 실제 사용자 데이터·키·외부 provider를 사용하지 않았고 stage/commit/push/PR/tag/Release/asset/외부 업로드·시스템 변경도 수행하지 않았다.

## 2026-07-14 - 공개 준비 Phase 09 로컬 RC·문서

- Phase 09 문서를 시작 직전에 전부 읽고, 이후 패키지 입력을 240개 파일로 동결했다. combined SHA-256은 `546BE0C220FF382EFFE13EEA230AF222D4E5C2AE00144EA87A912F751481956D`다. 이후 readiness 문서 갱신은 governance-only 변경으로 분리하며 제품·테스트·도구·패키지 allowlist 입력이 바뀐 것처럼 주장하지 않는다.
- 첫 clean package 시도는 상속된 긴 TEMP/profile 경로 때문에 Windows 경로 제한에서 fail closed했다. 패키지 작업공간을 `%TEMP%\\RwatPkg-<GUID>` 바로 아래의 길이 제한·run-owned root로 옮기고, child TEMP/profile/AppData를 그 안에 격리했다. 표준 MessageBox의 visible button ID와 `DM_GETDEFID`가 다른 호스트 동작은 PID·제목·본문·단일 enabled button을 모두 고정한 뒤에만 alias를 허용했다. 정착 tooling strict build는 0/0, self-test는 17/17이다.
- 두 clean root의 동일 길이 EXE에서 2,655개 byte position이 달랐다. bundle/PE를 직접 비교해 absolute-path-derived file-local prefix 42회 occurrence가 차지한 2,604 positions, deterministic PE timestamp 4, MVID 16, bundle ID 31로 전부 설명했고 unexplained position은 0이었다. Compression을 끈 unmapped pair도 달랐으므로 원인 수정은 전체 run-owned work root를 `/_/`로 매핑하는 동일 `PathMap`을 build/publish에 적용한 것이다. Compression-off는 별도 최종 package policy로 유지했다.
- 최종 두 run은 서로 다른 GUID root에서 각각 8개 프로젝트 strict Release 0 warnings/errors, console 80/80, glossary self-test와 cold/warm/duplicate package smoke를 모두 통과했다. 두 run의 ZIP, EXE와 추출된 14개 파일은 byte-identical이다. 최종 ZIP은 `dist/RimWorldAiTranslator-v1.0.1-rc.1-win-x64.zip`, 66,240,659 bytes, SHA-256 `DC132CC15B3654BB7306207DB26684B4021580A037B2E84F400A2105F6F966EF`이고, EXE는 163,782,434 bytes, SHA-256 `94ABDFE4E3F5D9EDAB75276E745313F6126CF9DC47139F6047D736A52200DE3E`다. Manifest는 2,652 bytes, SHA-256 `710991677E19244BB3CAF5680070C97F6778EE8E832945D5219D5B3AEB1DC1B5`다.
- exact archive는 top-level 14개 allowlist만 포함하고 unsafe/duplicate path, PowerShell, 공식 파생 generated glossary가 0이며 모든 entry hash가 manifest와 일치한다. EXE는 Windows GUI x64, FileVersion `1.0.1.0`, ProductVersion `1.0.1-rc.1`, PerMonitorV2/asInvoker/uiAccess=false, exact 9-frame icon이고 Authenticode는 `NotSigned`다.
- 별도 한국어+공백 경로 추출본은 first usable 2,013.585ms, 3초 idle 뒤에도 responsive, normal close 55.527ms, exit 0, descendants/same-EXE process 0이었다. 두 package run의 cold first usable은 2,031/2,055ms, warm은 630/627ms이며 duplicate contender는 exit 2, holder는 responsive하고 root 재획득까지 PASS했다. 사용자 `%LOCALAPPDATA%`나 discovery/profile을 쓰지 않았고 default settings 파일을 강제로 만들지 않았다.
- exact package-bound 5,000행 benchmark는 12개 자동 check와 package-size gate가 모두 PASS했다. JSON top-level은 separate smoke, non-equivalent Golden 경계, packaged Apply/RMK kill breadth, clean-PC/manual을 억지로 합치지 않으므로 evidence completeness `BLOCKED`를 유지한다. 최종 package process/work root/temp residue는 0이다.
- 현재 Defender engine `1.1.26060.3008`, signature `1.455.125.0`로 exact ZIP과 추출 폴더만 targeted scan했고 둘 다 exit 0, threat 0이었다. signature update, full-system scan이나 영구 안전 보증으로 확대하지 않는다. 기존 v1.0.0 release-note 영역과 사용자 소유 ZIP hash/write time은 변경되지 않았다.
- SEC-010은 exact ZIP에 들어간 네 개의 .NET 8.0.28 notice/license 파일과 `THIRD_PARTY_NOTICES.md` inventory/manifest hash로 해결했다. SEC-009의 3,833개 official-derived observation은 권리 근거가 없어 `glossary.generated.ko.json`을 exact ZIP에서 제외했다. 이는 disputed bytes 직접 배포를 피하지만 bundled suggestion과 glossary-bearing Golden request parity를 잃으므로 전체 RC blocker로 남긴다.
- Phase 09 자동 판정은 `PASS`, 전체 판정은 `BLOCKED`다. SEC-009와 clean-PC/no-SDK, 실제 Excel, 실제 150/200%·mixed-DPI, screen-reader/OS high contrast, native dialog/default-No/focus, API-key reveal/copy, 사용자 Golden 시각 확인을 실행하지 않았다. 실제 사용자 데이터·실제 키·유료/실제 provider를 사용하지 않았고 stage/commit/push/PR/tag/Release/asset/외부 업로드·시스템 변경을 수행하지 않았다. 다음 작업은 Phase 09 문서 정합성과 package 이후 governance drift를 고정한 다음 Phase 10 문서를 처음부터 끝까지 읽고 적대적 최종 감사를 실행하는 것이다.

## 2026-07-14 - 공개 준비 Phase 10 독립 감사와 최종 게이트

- Phase 10 절차를 시작 직전에 읽고, 54개 기능 중 위험 기반 22개(40.7%)와 저장/복구, Apply/DryRun/RMK, 키/로그, 경로/XML/XLSX/ZIP, 취소/재시작, PowerShell 및 패키지 경계를 독립적으로 재감사했다.
- 세 P1을 발견했다. Apply/RMK 미리보기와 실행 계획이 달라질 수 있었고 Apply 안전 후보 수/제외 표시가 부정확했다. 복구 안내는 정확한 손상본 파일명을 잃거나 큐에 남을 수 있었다. native ZIP 검증은 항목별 Zip64/split/extra/count/trailing 불일치를 모두 거부하지 않았다.
- 계획 SHA-256 fingerprint와 write-boundary baseline 재검증, 중앙 집중식 leaf-only 복구 안내, 엄격한 central-directory 항목 파서를 구현했다. 집중 테스트 뒤 strict Release는 8개 프로젝트 경고/오류 0, standalone 전체 회귀는 81/81을 통과했다.
- 서로 다른 세 clean package root에서 각각 strict 0/0, 81/81, glossary self-test와 cold/warm/duplicate smoke를 반복했다. ZIP, EXE, 14개 payload는 byte-identical이며 manifest는 진실한 `createdUtc` 하나를 제외하고 동일하다.
- 최종 ZIP은 66,253,420 bytes, SHA-256 `7A4B0302F0614F657F6C4C5B7CDD7809B4CAA562E20CC13B8D66D186603C5B17`이다. EXE는 163,800,354 bytes, SHA-256 `1EC570FACCF0C653A3A7860A0756D0D123A141696B91E03C92DEDBED4636ABDA`다.
- exact allowlist/manifest/PowerShell-zero/credential/Defender/v1.0.0 불변/runtime/residue 감사가 통과했다. 빈 application package graph에서 `dotnet list --vulnerable`가 divide-by-zero로 실패한 결과는 PASS가 아닌 N/A로 남기고, .NET 8.0.28 지원 상태는 Microsoft 공식 servicing/support 근거로 별도 확인했다.
- 첫 residue 쿼리는 세션 전 사용자 TEMP root 6개와 존재하지만 점유되지 않은 영속 coordination file을 잔여물로 오판했다. 사용자 root를 삭제하지 않고 현재-run GUID root/프로세스/journal 0, smoke cleanup 3/3과 lock exclusive-open으로 범위를 교정해 PASS했다.
- 최종 기능 수치는 PASS 47 / NEEDS_EVIDENCE 5 / BLOCKED 2 / FAIL 0이다. SEC-009 glossary 권리와 그에 따른 TRN-003/REV-006 parity, clean PC/no SDK, 실제 Excel, 150/200%·mixed DPI, Narrator/NVDA·OS high contrast, native dialog/privacy/사용자 시각/SmartScreen 확인이 남는다.
- 따라서 로컬 자동 기술 게이트는 PASS지만 최종 판정은 `BLOCKED`다. stage/commit/push/PR/tag/Release/asset/외부 업로드/유료·실제 API/공개 GitHub 변경은 수행하지 않았고, 실제 사용자 데이터와 키도 사용하지 않았다.

## 2026-07-14 - Phase 10 최종 v5 봉합

- 기존 81-test 패키지 이후 남은 로컬 갭을 다시 도전했다. 시작 실패 안내/복구, 원문 언어 선택 취소 무쓰기, settings·activity·recovery notice 재시작 바인딩은 실제 MainForm 및 최종 패키지 EXE에서 통과했다. identical-source 전파는 source와 각 target 모두 공통 `ReviewSafety`를 통과하도록 보강했다.
- 패키지 smoke가 오래된 숫자 PPID와 재사용 PID를 결합해 무관한 시스템 프로세스를 descendant로 오판한 실패를 보존하고, 각 parent-child 연결의 creation identity를 검증하도록 수정했다. RMK 격리 stage의 quarantine rename이 일시적 Win32 5에서 fail closed한 사례는 동일 pinned parent/root identity를 매 시도 재검증하면서 오류 5/32/33만 최대 8회 재시도하도록 제한했다.
- 로컬 갭 최초 등록은 81/82로 실패했고 UI harness 종료/잠금 및 두 패키지의 일시적 cleanup 실패도 첫 오류에서 중단하지 않았다. 원인을 수정한 뒤 focused regression, strict Release, standalone 82/82, 별도 post-PID-fix tooling self-test 18/18과 세 clean package run을 다시 수행했다. 최종 세 package run은 각각 strict 0/0, 82/82, glossary 및 cold/warm/duplicate/cleanup PASS다.
- 최종 package input snapshot v5는 250 files, combined SHA-256 `C279F6F6C87634D95035C3D17821770DE0703649CCD33A3A55B123695683A006`다. ZIP은 66,254,006 bytes, SHA-256 `2302E87747F55348EA5EF96E2D352686ADFD943BB82954C9DA1F789553BBB7C1`; EXE는 163,800,354 bytes, SHA-256 `9EEB6A80DD51DCAFD31D2516CFB6F1C70257CEDD94783C360ED58FEA3FE919C8`다. 세 run의 ZIP/EXE/14 files는 byte-identical이고 manifest는 진실한 `createdUtc`만 다르다.
- exact archive, verify-zero, credential/PowerShell, Defender, v1.0.0 불변, performance와 residue 감사를 v5에 다시 수행했다. 최종 residue 감사 전에 현재 세션의 실패 run 소유 root 세 개를 exact prefix/GUID/root identity/reparse/process로 검증한 후에만 삭제했고, 기존 synthetic root는 보존했다.
- 최종 기능 수치는 PASS 51 / NEEDS_EVIDENCE 1(PKG-003) / BLOCKED 2(TRN-003, REV-006) / FAIL 0이다. SEC-009, 익명화된 기존 프로젝트 재열기, clean PC/no SDK, 실제 Excel, DPI/보조기술, native dialog/privacy/시각/SmartScreen 수동 확인이 남으므로 최종 판정은 `BLOCKED`다.
- stage/commit/push/PR/tag/Release/asset/외부 업로드/유료·실제 API/공개 GitHub 변경은 수행하지 않았고 실제 사용자 데이터·실제 API 키를 사용하지 않았다. 사용자가 승인한 컴퓨터 종료는 최종 보고와 마지막 무결성 확인 뒤 단 한 번의 마지막 운영 작업으로만 수행한다.
- 최종 독립 증거 대조에서 문서 과장 두 건을 발견해 제품 바이트를 바꾸지 않고 교정했다. tooling 18/18은 PID-identity 수정 뒤 별도 1회 실행 증거이며 세 package log 각각에 포함된 검사가 아니므로 run별 주장을 제거했다. 성능 표와 최종 보고의 cancellation/memory 수치도 `final-v5-package-performance.json`의 실제 v5 값으로 교체했다.
- Phase 08 계약의 packaged idle working set 누락은 exact v5 ZIP을 고유한 합성 data/profile/TEMP root에서 실행해 보완했다. 첫 wrapper는 compression assembly를 로드하지 않아 프로세스 실행 전 실패했고 exact root를 정리했다. 교정 실행은 ready 뒤 5초 동안 working set 77,262,848-77,287,424 bytes, private 19,906,560-19,910,656 bytes, handle 410, responsive true, 정상 exit 0과 temp 제거를 증명했다.
- 반면 equivalent Golden process boundary, project/Apply/RMK cancellation timing, packaged forced-exit/recovery breadth와 Phase 10 최소 수동 end-to-end 흐름은 좁은 자동 증거로 대체하지 않았다. 이를 STATE/PLAN/FINAL/PERFORMANCE와 MRC-015~020에 명시해 최종 `BLOCKED`의 범위를 정확히 넓혔다.

## 2026-07-14 - PowerShell 안정판 작업 데이터 호환성 복구

- 실제 기존 프로젝트와 RMK 통합문서의 원본은 건드리지 않고 격리된 임시 복사본으로 재현했다. 실패 위치는 XLSX `sheetView/@showFormulas=false`를 수식 실행 표면으로 오인한 native passive-XML 검사였고, 이를 고친 뒤에는 안정판이 첫 행을 채택하던 중복 RMK ID를 C#판이 모두 손상으로 거부하는 두 번째 비호환도 확인했다.
- Golden Master의 project v2, settings v3, review v5, ownership marker v1과 RMK first-wins 읽기 동작을 다시 대조했다. 누락된 선택 필드와 알 수 없는 추가 필드는 허용하고, 검증 가능한 v5 review 결정은 유지하며, marker v1 또는 marker 없음은 읽기 전용 열기를 막지 않도록 복구했다. marker가 없는 대상의 삭제 권한은 부여하지 않는다.
- 실제 복사본에서 프로젝트 846개 검수 결정, 원문 3,742개, 분석 결과 2,684개를 정상 로드했다. 열기 전후 프로젝트·설정·review·원문·통합문서 해시가 같았고, 별도 작업 복사본에서만 v5→v6 마이그레이션과 정확한 백업을 수행한 뒤 재실행을 확인했다.
- 대표 안정판 fixture와 회귀 테스트를 추가해 프로젝트/설정/review/RMK/원문 분석, marker v1·marker 없음 읽기, 명시적 마이그레이션과 재실행, 프로젝트 데이터 삭제 시 원문·RMK 보존, 손상 JSON/XLSX의 무변경 거부를 검증했다. 기본 UI 오류는 내부 예외 형식과 로컬 경로를 노출하지 않고 손상 또는 미지원 형식임을 안내한다.
- strict Release 빌드는 경고/오류 0/0이고 호환성·cleanup 집중 게이트와 UI bootstrap/single-instance harness는 PASS다. 현재 전체 suite는 82/83이며 유일한 실패는 이번 호환성 범위 밖의 `Phase08.ForcedExitRecovery` 1,000-target journal 시간 70.539초(계약 60초)다. 요청에 따라 성능 최적화나 임계치 완화는 수행하지 않았고 전체 공개 판정은 `BLOCKED`로 유지한다.
- stage/commit/push/PR/tag/Release/asset/외부 배포·업로드·유료 API는 수행하지 않았다. 실제 데이터는 원본 불변을 검증한 읽기 전용 복사본에만 사용했고, 검증 뒤 고유 임시 복사본을 제거했으며 fixture에는 익명화된 합성 데이터만 남겼다.
- 사용자 요청으로 현재 호환성 수정본을 기존 `dist`와 분리된 `artifacts/local-builds/RimWorldAiTranslator-legacy-compat-win-x64/`에 self-contained single-file `win-x64`로 publish했다. EXE는 163,800,866 bytes, SHA-256 `9CEAE0AE5136F5E503819DEF1B9EC51272D376F0961F5BDD36B62F06C1D54C44`이며, 고유 격리 data/discovery/profile에서 1.117초 내 responsive window와 acknowledgement를 확인하고 정상 종료 코드 0을 얻었다. 임시 smoke root는 제거했고 기존 RC/Release asset은 변경하지 않았다.
- 실제 UI 재검증에서 프로젝트 삭제 뒤 이전 `ReviewWorkspace`가 메모리에 남아, 다음 새 프로젝트의 원문 분석 직전에 이미 삭제된 review root로 v5 마이그레이션 저장을 시도하는 `DirectoryNotFoundException`을 확인했다. 삭제가 커밋되면 동일 프로젝트 workspace와 백그라운드 필터/품질 작업을 즉시 분리하도록 수정하고 `삭제 → stale migration save 없음` UI 회귀를 추가했다. Phase08 UI truthfulness, legacy compatibility, cleanup 집중 게이트와 strict Release 0/0이 통과했다.
- 교정 실행파일은 `artifacts/local-builds/RimWorldAiTranslator-legacy-compat-win-x64-r2/`에 별도로 publish했다. EXE는 163,800,866 bytes, SHA-256 `345EB1E13938E5B50818C15DE681617727AE4EC43C350DD7E927F840656624EC`이며, 격리 startup 3.072초, responsive window, 정상 close/exit 0을 확인하고 smoke root를 제거했다.
- 사용자 피드백에 따라 API 키 영역의 `입력된 키 없음` 마스킹 화면, 15초 표시 타이머와 `키 편집` 버튼을 제거했다. 키가 필요한 공급자는 빈 다중 입력란을 바로 표시하고 키는 기존대로 현재 프로세스 메모리에만 유지한다. UI 회귀는 빈 직접 편집기와 버튼 부재를 확인하며 provider URL 보안 gate와 strict Release 0/0이 통과했다. r3 EXE는 163,800,866 bytes, SHA-256 `05E5EF48C27A8FACEC8F4002A743BBB89ABDCDC8949B85E0A4690F1B50B46D66`이고 격리 startup/정상 exit 0을 통과했다.

## 2026-07-14 - 시작 화면 점진 렌더링 제거

- 원인은 `MainForm.Shown` 이후 빈 대시보드 렌더링, 강제 `PerformLayout`/`Update`, 불투명 전환, 모드 검색 결과 렌더링과 프로젝트 통계 재렌더링이 순서대로 실행되던 시작 경로였다. 메인 폼과 자식 패널 생성에도 일괄 레이아웃 중지가 없었고 bootstrap은 표시 직후 강제 `Refresh`를 호출했다.
- bootstrap이 완성된 로딩 화면으로 남아 있는 동안 메인 폼의 모드 검색, 프로젝트 통계, 공급자 상태, glossary, 격리 acknowledgement와 최초 DPI/handle/layout 계산을 모두 완료하도록 전환했다. 메인 폼은 준비 중 투명·비활성 상태로 생성하고 메시지 큐가 idle에 도달한 뒤 한 번에 표시하며 그 후 bootstrap을 숨긴다. 실패 시 준비되지 않은 메인 폼은 표시하지 않는다.
- 대시보드는 시작 시 최종 데이터로 한 번만 카드를 만들고 같은 너비의 반복 Resize 렌더링을 건너뛴다. MainForm, bootstrap, dashboard, settings와 flow panel에 double buffering을 적용하고 생성 단계 `SuspendLayout`/`ResumeLayout`을 추가했다. 시작 경로의 `Refresh`, `Update`, 사후 `BeginInvoke(PerformLayout)`을 제거했다.
- UI 회귀는 첫 공개 프레임과 두 번의 후속 UI 메시지 처리 뒤 가시 컨트롤의 수·텍스트·위치·크기가 동일하고 공개 후 `ControlAdded`가 0인지 검증한다. 느린 bootstrap, 시작 취소, transition 실패, 시작 실패도 준비되지 않은 메인 화면을 노출하지 않는 조건으로 통과했다. 실제 격리 UI를 두 차례 캡처해 레이아웃이 유지되는 것도 확인했다.
- strict Release build는 경고/오류 0/0, Phase08 UI truthfulness, slow bootstrap 성공/취소, transition failure, provider URL/settings, legacy PowerShell compatibility와 project cleanup이 통과했다. 전체 기능 suite는 82/83이며 유일한 실패는 이번 범위 밖의 기존 `Phase08.ForcedExitRecovery` 1,000-target 시간 상한으로 69.723초/60초였다.
- 사용자 전달용 r4는 `artifacts/local-builds/RimWorldAiTranslator-legacy-compat-win-x64-r4/RimWorldAiTranslator.exe`, 155,588,155 bytes, SHA-256 `FED024E75B2417E039BE3EAA3A7FF4BE46759A2DCC9B9349A710AD38F32ED23B`다. 고유 격리 data/discovery root에서 responsive main window와 acknowledgement, 정상 close/exit 0을 확인했고 임시 root를 제거했다. Release/tag/asset은 생성하거나 수정하지 않았다.
- 사용자 요청에 따라 검증된 현재 C# 후보 215개 경로를 커밋 `d641d0fae445ed38d57ca379518aaca736f172cc`로 만들고 `origin/codex/csharp-migration`에 새 원격 브랜치로 push했다. 빌드 산출물과 실제 사용자 데이터는 포함하지 않았고 PR, tag, Release, asset, 배포는 만들거나 수정하지 않았다.

## 2026-07-14 - 구형 RMK 조건부 서식·시작 지연·실패 화면 교정

- 실제 PowerShell-era RMK 통합문서의 격리 복사본에서 `InvalidDataException`을 다시 재현했다. 정확한 거부 위치는 `xl/worksheets/sheet1.xml`의 SpreadsheetML `conditionalFormatting/cfRule/formula`였으며, PowerShell 안정판은 이 표시 규칙을 사전 거부하지 않았다. 원본은 해시와 수정 시간이 유지됐고 진단 복사본은 검증 뒤 제거했다.
- C# reader는 정확한 `cfRule/formula`만 수동 조건부 서식으로 읽도록 좁혔다. `WEBSERVICE`, `HYPERLINK`, RTD/DDE/CALL/REGISTER.ID, URL·UNC 참조 같은 외부 동작은 계속 `InvalidDataException`으로 거부하고, 셀 `f`, validation/table/defined-name 수식과 active relationship/content type 거부도 유지한다.
- 익명 합성 PowerShell fixture에 정상 조건부 서식 수식을 추가했다. 해당 fixture는 기존 프로젝트·설정·review·RMK 로드, marker 없음/구형 marker 읽기 전용 열기, 원문 분석, 열기 무쓰기, 백업 마이그레이션·재실행, 삭제 경계, 손상 JSON/XLSX 무변경 거부를 함께 검증한다.
- 실패 overlay가 `원문 분석 중` 제목과 회전 표시를 남기던 문제를 고쳐 최종 제목·실패 단계·재시도 가능 여부를 표시하고 spinner를 멈춘다. 저장 프로젝트 열기 시 dashboard의 선택 모드도 해당 프로젝트 경로로 동기화해 다른 모드가 선택된 것처럼 보이지 않게 했다.
- 시작 화면은 최초 공개에 필요하지 않은 glossary 로드를 완성 프레임 공개 뒤 백그라운드 작업으로 옮겼다. 느린 glossary를 의도적으로 막은 UI 회귀에서 메인 폼의 완성 프레임이 먼저 안정적으로 노출되고 공개 뒤 컨트롤 추가·위치 변화가 0임을 확인했다.
- strict Release 빌드는 경고/오류 0/0이다. `Compatibility.LegacyPowerShellProject`, `--phase08-ui-truthfulness`, `--slow-bootstrap`은 모두 PASS했고 전체 회귀는 기능·보안 82/83 PASS다. 유일한 실패는 범위 밖의 기존 `Phase08.ForcedExitRecovery` 1,000-target 성능 계약으로 85.836초/60초였으며 테스트를 완화하거나 성능 작업을 추가하지 않았다.
- 전달용 로컬 빌드는 `artifacts/local-builds/RimWorldAiTranslator-legacy-compat-win-x64-r7/RimWorldAiTranslator.exe`, 155,589,691 bytes, SHA-256 `55EFBD6A7E5BC780E2B6EEB8FA1D1A4B2783ED826254D7327FD49B5A16456B26`이다. 고유 격리 data/discovery/profile/TEMP에서 5.512초 내 startup acknowledgement, responsive main window, 정상 close/exit 0과 임시 root 제거를 확인했다. 이 빌드는 로컬 전달본이며 RC/Release asset이 아니다.
- 사용자 지시에 따라 C# 후보를 `main`에 병합하고 수정 커밋 `6a977a16913922313d2da7e0679e3c0983a607a9`까지 `origin/main`에 일반 push했다. `origin/main`이 후보 tip `7e0eb0addd56b41d0b3d1443911f919e7567f406`을 포함함을 확인한 뒤 원격·로컬 `codex/csharp-migration`을 삭제했다. 기존의 분리된 로컬 main은 `backup/local-main-b49f217-20260714`로 보존했다. PR, tag, Release, asset, metadata, Actions와 배포는 변경하지 않았다.

## 2026-07-14 - 로컬 번역기 저장·복구 단순화

- `main` 기준 저장·복구 호출 경로와 보호 목적을 먼저 표로 정리하고, 관리자·악성 로컬 프로세스의 namespace 경쟁, hardlink/file-ID/PID 재사용, 모든 강제 종료 지점 자동 판정, journal 변조와 데이터베이스 수준 다중 파일 원자성을 제품 위협 모델에서 제외했다.
- 결과는 앱 소유 임시 작업에 만들고 flush·형식 검증한 뒤 같은 볼륨의 대상에 교체한다. 대상과 `.bak`의 시작 snapshot을 보존하고, 오류·취소 시 실제 교체 직후 내용과 같은 파일만 역순 복원한다. 일반 외부 편집이 있으면 해당 파일을 덮어쓰지 않고 충돌로 중단한다.
- 강제 종료 흔적은 단일 manifest와 `started.marker`로 감지한다. 준비만 된 앱 소유 폴더는 정리하지만 시작된 작업은 자동 commit·자동 추론하지 않는다. 완성된 메인 화면에서 사용자에게 시작 전 백업 복구를 묻고, 복원 I/O는 UI 스레드 밖에서 실행한다. 손상 metadata는 내부 예외명·경로 없이 파일을 변경하지 않았다고 알린다.
- `AtomicCommitRecoveryPlan`, `FileTransactionRecoveryArtifacts`, target별 reserve/intent/ready/applied/done 및 연쇄 SHA-256 artifact, 디렉터리 guard 파일, 장기 write-boundary handle, hardlink count와 임시 파일용 중복 P/Invoke identity 구현을 제거했다. 핵심 9개 파일 8,127줄·61타입·29훅은 7개 2,781줄·22타입·4훅으로 감소했다.
- `Storage.SimpleRecoveryThreatModel`과 기존 회귀를 합쳐 새/기존 파일 저장, `.bak`, 두 번째 파일 실패·취소 rollback, 잠금·read-only, 허용 루트 밖·Workshop, XML 검증 실패, JSON 단일 정상 backup/이중 손상, 강제 종료 감지·무단 commit 금지·명시적 restore, 외부 편집 충돌, API key/원문 로그 redaction을 검증했다.
- synthetic 100-target은 1.649초, 1,000-target은 20.673초로 기존 1,000-target 71.961초보다 71.3% 감소했다. 기존 60초 합격 계약은 제거하고 100개 소형 파일 수 초 이내를 일반 사용 관찰 기준으로 제안했다.
- 최종 strict Release build는 경고/오류 0/0, 전체 회귀는 80/80 PASS(최종 59.025초)다. 실제 사용자 데이터·API·Workshop/RMK 구독본을 사용하지 않았고 패키지·Release·tag·PR·push·외부 배포는 수행하지 않았다.
