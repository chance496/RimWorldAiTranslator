# Project State

기준 시각: 2026-07-12 KST. 줄 번호는 이 스냅샷의 작업 트리 기준이다.

## 저장소와 작업 트리

- 제품 Git 루트: `C:\Users\wjdck\Documents\Rimworld\tools\RimWorldAiTranslator`
- 원격: `https://github.com/chance496/RimWorldAiTranslator.git`
- 현재 작업 브랜치: `codex/autonomous/20260712-073630`. 기준 백업은 `aff52cc`, P0 `2ef9488`, P1 `7e49808`, P2 `e63c404`, P3 제공자 `db01309`, 번역 메모리 `d2e0429` 체크포인트를 보존한다.
- 기존 수정 파일: `Apply-RimWorldAiReviewResults.ps1`, `Export-RimWorldAiReviewToRmk.ps1`, `Invoke-RimWorldAiTranslation.ps1`, `PACKAGE_README.txt`, `README.md`, `Start-RimWorldAiReviewGui.ps1`, `build-package.ps1`, `native/RimWorldTranslatorNative.cs`
- 기존 새 파일: `rimworld-def-field-rules.txt`
- 기존 제품 변경 규모: 8개 추적 파일에서 +752/-199줄. 이번 운영 체계 정리 전부터 존재한 사용자 작업이며 검증·커밋되지 않았다.
- 자동 테스트, 린터, CI workflow, 프로젝트 파일(`.sln`/`.csproj`)은 없다. `testdata/SampleMod`만 존재한다.

## AGENTS 용량 점검

- 상위 작업공간의 기존 `AGENTS.md`: 31,349바이트, 385줄.
- Codex 기본 `project_doc_max_bytes`: 32,768바이트. 기존 파일은 95.7%를 사용했고 1,419바이트만 남아 있었다.
- 정리 후 상위 `AGENTS.md`: 1,492바이트, 17줄(기존 대비 29,857바이트, 95.2% 감소).
- 새 제품 루트 `AGENTS.md`: 5,281바이트, 54줄(기본 한도의 16.1%). 상위 파일과 합쳐도 6,773바이트다.
- 한도 근거: [OpenAI Codex config schema](https://github.com/openai/codex/blob/main/codex-rs/core/config.schema.json)의 `project_doc_max_bytes` 기본값.
- 큰 파일은 한도에서 잘릴 수 있으므로 상태, 로드맵, 명령, 구조와 기록을 이 디렉터리의 문서로 분리했다.

## 구현된 기능

| 영역 | 현재 구현 | 코드 근거 |
|---|---|---|
| 실행 | C# 실행기가 Windows PowerShell에서 WinForms GUI를 시작하고 준비 창과 오류를 처리한다. | `launcher/RimWorldAiTranslatorLauncher.cs:21-108`, `Start-RimWorldAiTranslatorGui.ps1:7-31` |
| 프로젝트 저장 | `%LOCALAPPDATA%\RimWorldAiTranslator`에 프로젝트, 설정, 카탈로그, 통계와 검수 데이터를 저장한다. JSON은 원자 교체하며 정상 `.bak`으로 주 파일을 복구하고 손상 원본을 별도 보존한다. 둘 다 손상되면 빈 목록으로 덮지 않고 시작을 중단한다. | `RimWorldAiTranslator.Storage.ps1`, `Start-RimWorldAiReviewGui.ps1`의 `Load-ProjectStore` |
| 프로젝트 삭제 | 앱 소유 표식을 재검증한 검수 폴더만 삭제하고 원본 모드와 `Languages\Korean`은 보존한다. | `RimWorldAiTranslator.ProjectCleanup.ps1`, `Project.CleanupBoundary` |
| 추출 | 선택 언어의 Keyed/DefInjected와 허용된 Def 필드를 읽고 `LoadFolders.xml`의 활성 루트를 따른다. Patches 자동 번역은 안전상 비활성이다. | `Invoke-RimWorldAiTranslation.ps1:540-619`, `Invoke-RimWorldAiTranslation.ps1:633-720` |
| Def 안전성 | 허용/거부 규칙, 내부 식별자 감지, 제외 감사 JSON이 구현되어 있다. | `native/RimWorldTranslatorNative.cs:65-125`, `native/RimWorldTranslatorNative.cs:218-340`, `Invoke-RimWorldAiTranslation.ps1:564-615`, `Invoke-RimWorldAiTranslation.ps1:2094-2095` |
| 번역 실행 | Google fallback, OpenAI 호환 제공자, 여러 API 키, 키별 제한과 배치 분할을 지원한다. 완료 배치를 원자 체크포인트로 남기며 취소·재시도·남은 항목 재개를 지원한다. | `Invoke-RimWorldAiTranslation.ps1`, `Translation.ApiResilience` |
| 검수 안전성 | 토큰 종류·개수·문법 접두어, 비정상 개행, 한국어 포함, 잘못된 조사 표기를 검사하고 안전하지 않은 결과를 적용에서 제외한다. | `Start-RimWorldAiReviewGui.ps1:2422-2536`, `Invoke-RimWorldAiTranslation.ps1:2205-2318` |
| 업데이트 승계 | 파일+키 또는 유일 키로 이전 결정을 연결하고 원문이 바뀌면 번역을 보존한 채 `pending/sourceChanged`로 내린다. | `Start-RimWorldAiReviewGui.ps1:3075-3197` |
| 로컬/RMK 적용 | 상태, 원문 해시, 토큰, 경로와 중복 키를 검사해 로컬 Korean XML 또는 RMK 작업 경로에 쓴다. 기존 XML/XLSX는 `.bak`을 남기고 다중 파일 실패 시 전체를 롤백한다. | `Apply-RimWorldAiReviewResults.ps1`, `Export-RimWorldAiReviewToRmk.ps1`, `native/RimWorldTranslatorNative.cs` |
| 패키징 | Windows .NET Framework `csc.exe`로 실행기와 native DLL을 빌드하고 전체 오프라인 회귀, 패키지 Parser, ZIP 압축 해제 원문 추출 smoke를 통과한 파일만 `dist`에 묶는다. | `build-package.ps1`, `tests/Run-RegressionTests.ps1` |
| UI 감사·성능 | 격리된 5,000행 합성 프로젝트에서 창 크기·테마·글자 크기·접근성·검색/필터 결과와 로드·검색·이동·저장·메모리를 재현 측정한다. RMK XML/XLSX 생성·갱신 benchmark도 별도로 제공한다. | `tests/Run-UiPerformanceAudit.ps1`, `tests/Run-RmkPerformanceBenchmark.ps1` |
| 검증 성능 | PowerShell 검증 규칙을 기준 구현으로 유지하면서 native DLL이 있을 때 같은 토큰·조사·개행 판정을 컴파일된 정규식으로 실행한다. | `RimWorldAiTranslator.Validation.ps1`, `native/RimWorldTranslatorNative.cs` |
| 제공자 설정 점검 | 키 값이나 네트워크 호출 없이 URL 보안, 모델/Temperature 입력, 키 개수와 내장 프로필 일치를 확인한다. 수동 모델은 유지하고 온라인 미확인을 표시한다. | `RimWorldAiTranslator.ProviderValidation.ps1`, `Settings.ProviderValidation` |
| 로컬 번역 메모리 | 동일 원문의 안전한 기존 번역만 상태·출처·파일과 함께 용어 탭에 제안하며 현재 번역을 자동 변경하지 않는다. | `RimWorldAiTranslator.TranslationMemory.ps1`, `Review.TranslationMemory` |
| 진단 번들 | 비민감 설정·상태 개수·오류 분류·제품 해시만 로컬 ZIP에 기록하고 본문·키·경로·원시 로그를 제외한다. | `RimWorldAiTranslator.Diagnostics.ps1`, `Export-RimWorldAiTranslatorDiagnostics.ps1`, `Diagnostics.Privacy` |

## 미완성 또는 미검증

- P0/P1 저장·적용·Def 안전·원문 변경·토큰·취소·재시도·재개 경로는 16개 오프라인 회귀와 패키지 smoke를 통과했다.
- 실제 125/150/200% DPI는 Windows 디스플레이 배율 변경 없이 자동 재현할 수 없어 96 DPI 감사만 완료했다. 900×600/1280×720/1920×1080, 밝음/어두움/고대비와 글자 10/12에서는 잘림과 접근성 이름 누락이 0건이다.
- 제공자 모델의 실제 온라인 가용성과 최신 제한은 API 호출 없이 검증하지 않는다. 내장 프로필 또는 사용자가 입력한 값을 표시하되 온라인 미확인 상태를 유지한다.
- UI, 프로젝트 상태, RMK, 번역 프로세스 오케스트레이션이 큰 `Start-RimWorldAiReviewGui.ps1`에 남아 있다. 저장·검증·삭제 경계와 성능 runner는 독립 파일로 분리했으며, 무리한 전면 재작성은 하지 않는다.

## 알려진 오류와 위험

| 우선도 | 문제 | 영향과 근거 |
|---|---|---|
| 해결됨 | 손상 프로젝트 저장소의 조용한 빈 목록 대체, 로컬/RMK 부분 적용, 복구 백업 부재와 자식 로그 API 키 노출 가능성은 P0 회귀로 차단했다. | `StateStore.Recovery`, `Security.ApiKeyHandling`, `Apply.LocalRollback`, `Export.RmkTransaction` |
| 해결됨 | 자동 오프라인 회귀 부재, 직접 실행기 인코딩, 내부 식별자·중복 namespace, 토큰·원문 변경·RMK XLSX 보존, 취소·재시도·직접 출력 롤백을 P1 게이트로 고정했다. | `tests/Run-RegressionTests.ps1` 16개 suite 사례 |
| 명시적 차단 | 실제 125/150/200% DPI 자동 감사가 없다. | Windows 디스플레이 설정 변경은 현재 자율 작업의 시스템 변경 금지 범위다. runner는 실제 DPI를 기록해 다른 환경에서 같은 명령으로 보완할 수 있다. |
| 잔여 부채 | 큰 WinForms 스크립트에 여러 책임이 남아 있다. | 저장·검증·정리·benchmark는 분리됐지만 추가 분리는 동작 보존 회귀를 동반한 작은 단계로만 진행해야 한다. |

## 이번 점검에서 확인한 결과

- 전체 오프라인 회귀 19/19, 패키지 C# 빌드, 패키지 PowerShell Parser와 ZIP 새 폴더 원문 추출 7행 smoke가 통과했다.
- UI 5,000행 기준: 로드 1,558.896→993.706ms, 검색 중앙 3,071.292→1,191.644ms, 다음 항목 50.234→37.266ms, 실제 저장 1,925.418→546.736ms, working set 259.48→224.62MB. 변경 없는 저장은 중앙 0.111ms다.
- RMK 5,000행 기준: 생성 13,144.655→7,289.464ms, 갱신 중앙 14,919.371→8,640.982ms(최악 9,039.086ms), 최종 최대 working set 323.01MB다.
- 외부 네트워크와 실제 API, Workshop/RMK 구독본, `%LOCALAPPDATA%` 사용자 데이터는 사용하지 않았다. API 동작은 로컬 TCP 가짜 서버로 검증했다.
