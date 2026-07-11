# Project State

기준 시각: 2026-07-12 KST. 줄 번호는 이 스냅샷의 작업 트리 기준이다.

## 저장소와 작업 트리

- 제품 Git 루트: `C:\Users\wjdck\Documents\Rimworld\tools\RimWorldAiTranslator`
- 원격: `https://github.com/chance496/RimWorldAiTranslator.git`
- 현재 브랜치/HEAD: `audit/def-safety-ui-v0.2.1` / `7ec1335` (`v0.2.1`, `origin/main`)
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
| 프로젝트 삭제 | 앱 소유 표식을 재검증한 검수 폴더만 삭제하고 원본 모드와 `Languages\Korean`은 보존한다. | `Start-RimWorldAiReviewGui.ps1:1041-1134` |
| 추출 | 선택 언어의 Keyed/DefInjected와 허용된 Def 필드를 읽고 `LoadFolders.xml`의 활성 루트를 따른다. Patches 자동 번역은 안전상 비활성이다. | `Invoke-RimWorldAiTranslation.ps1:540-619`, `Invoke-RimWorldAiTranslation.ps1:633-720` |
| Def 안전성 | 허용/거부 규칙, 내부 식별자 감지, 제외 감사 JSON이 구현되어 있다. | `native/RimWorldTranslatorNative.cs:65-125`, `native/RimWorldTranslatorNative.cs:218-340`, `Invoke-RimWorldAiTranslation.ps1:564-615`, `Invoke-RimWorldAiTranslation.ps1:2094-2095` |
| 번역 실행 | Google fallback, OpenAI 호환 제공자, 여러 API 키 환경 변수 전달, 키별 제한, 배치 분할 재시도를 지원한다. | `Start-RimWorldAiReviewGui.ps1:4484-4595`, `Run-RimWorldAiTranslation.ps1:37-83`, `Invoke-RimWorldAiTranslation.ps1:1748-1818` |
| 검수 안전성 | 토큰 종류·개수·문법 접두어, 비정상 개행, 한국어 포함, 잘못된 조사 표기를 검사하고 안전하지 않은 결과를 적용에서 제외한다. | `Start-RimWorldAiReviewGui.ps1:2422-2536`, `Invoke-RimWorldAiTranslation.ps1:2205-2318` |
| 업데이트 승계 | 파일+키 또는 유일 키로 이전 결정을 연결하고 원문이 바뀌면 번역을 보존한 채 `pending/sourceChanged`로 내린다. | `Start-RimWorldAiReviewGui.ps1:3075-3197` |
| 로컬/RMK 적용 | 상태, 원문 해시, 토큰, 경로와 중복 키를 검사해 로컬 Korean XML 또는 RMK 작업 경로에 쓴다. 기존 XML/XLSX는 `.bak`을 남기고 다중 파일 실패 시 전체를 롤백한다. | `Apply-RimWorldAiReviewResults.ps1`, `Export-RimWorldAiReviewToRmk.ps1`, `native/RimWorldTranslatorNative.cs` |
| 패키징 | Windows .NET Framework `csc.exe`로 실행기와 native DLL을 빌드하고 전체 오프라인 회귀, 패키지 Parser, ZIP 압축 해제 원문 추출 smoke를 통과한 파일만 `dist`에 묶는다. | `build-package.ps1`, `tests/Run-RegressionTests.ps1` |

## 미완성 또는 미검증

- P0 저장·적용·보안 경로는 오프라인 회귀와 패키지 smoke를 통과했지만 P1의 전체 Def, 원문 변경, 토큰, 취소·재시도 사례는 아직 확장 중이다.
- README의 성능 수치는 이전 로컬 측정 결과지만 재현 가능한 benchmark 명령과 원시 기준 데이터가 저장소에 없다.
- UI, 프로젝트 상태, RMK, 번역 프로세스 오케스트레이션이 390KB가 넘는 `Start-RimWorldAiReviewGui.ps1` 한 파일에 결합되어 있다.

## 알려진 오류와 위험

| 우선도 | 문제 | 영향과 근거 |
|---|---|---|
| 해결됨 | 손상 프로젝트 저장소의 조용한 빈 목록 대체, 로컬/RMK 부분 적용, 복구 백업 부재와 자식 로그 API 키 노출 가능성은 P0 회귀로 차단했다. | `StateStore.Recovery`, `Security.ApiKeyHandling`, `Apply.LocalRollback`, `Export.RmkTransaction` |
| P1 | 자동 회귀 테스트와 CI가 없다. | 내부 식별자, 토큰, 원문 변경, RMK XLSX 보존과 적용 경로의 회귀를 릴리스 전에 자동 차단할 수 없다. |
| P1 | 직접 PowerShell 실행기의 누락 파일 오류 문구가 깨져 있다. | `Start-RimWorldAiTranslatorGui.ps1:14`의 한국어가 mojibake다. C# 실행기의 같은 경로는 정상 유니코드 이스케이프를 사용한다. |
| P2 | `IncludePatches` UI/매개변수는 존재하지만 엔진은 Patches 번역을 항상 비활성화한다. | 사용자가 체크박스가 번역 범위를 넓힌다고 오해할 수 있다. `Invoke-RimWorldAiTranslation.ps1:540-546`. |
| P2 | 7천 줄이 넘는 WinForms 스크립트에 책임이 집중되어 있다. | 변경 범위 파악, 단위 테스트, UI 회귀 격리와 성능 분석이 어렵다. |

## 이번 점검에서 확인한 결과

- 모든 루트 `*.ps1` 파일은 PowerShell Parser 정적 구문 검사를 통과했다.
- `git diff --check`는 오류 없이 줄 끝 변환 경고만 출력했다.
- 패키지 빌드, GUI 실행, 실제 모드/RMK 쓰기와 네트워크 호출은 수행하지 않았다.
