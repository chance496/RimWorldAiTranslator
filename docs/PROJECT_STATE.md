# Project State

기준 시각: 2026-07-13, v1.0.1-rc.1 로컬 C# 릴리스 후보 (공개 v1.0.0 보존)

## 현재 구현

| 영역 | 상태 | 코드 근거 |
|---|---|---|
| 실행 | C# WinForms 단일 EXE가 직접 시작하며 사용자 및 개발 RC 경로에 PowerShell 의존성이 없다. | `src/RimWorldAiTranslator.App/Program.cs`, `tools/RimWorldAiTranslator.Tooling` |
| 프로젝트·설정 | 기존 JSON과 알 수 없는 속성을 유지하고 원자 저장·백업 복구·이중 손상 쓰기 차단을 제공한다. API 키는 직렬화하지 않는다. | `Core/Storage`, `Core/Models`, `Storage.*`, `Security.*` 회귀 |
| 모드 탐색 | Steam 라이브러리와 RimWorld 설치의 Workshop/로컬 모드를 찾고 여러 원문 언어를 선택한다. | `Core/Discovery/RimWorldModDiscoveryService.cs` |
| 원문 추출 | `LoadFolders.xml`, Keyed, DefInjected, 허용된 Def 표시 필드를 읽고 내부 식별자·패치·기술 필드를 제외한다. | `Core/Extraction/SourceExtractor.cs`, `native/RimWorldTranslatorNative.cs` |
| 번역 | OpenAI 호환 제공자, 키 순환, Google fallback, 재시도, 손상 JSON 분할, 체크포인트, 취소와 재개를 지원한다. 원본+DLC 용어집과 사용자가 선택한 추가 용어집을 결합한다. | `Core/Translation` |
| 검수 | 가상 목록, 텍스트·키·Def·Node 검색, 상태·파일·출처 필터, 수동 편집, 동일 원문 일괄 적용, 전체 안전 검토, 기록·용어·메모를 제공한다. | `App/Controls/ReviewWorkspaceControl.cs`, `Core/Review` |
| 원문 업데이트 | 이전 결정을 승계하고 원문 변경 시 번역을 보존한 채 미번역·변경됨으로 내려 적용을 막는다. | `ReviewWorkspaceService`, `Review.SourceChangeInheritance` |
| 로컬 적용 | Workshop은 거부한다. 명시적 로컬 대상은 dry-run 미리보기와 확인 뒤 현재 원문·토큰·경로·reparse를 재검증하고 실패 시 롤백한다. | `Core/Apply/ReviewApplyService.cs`, `Apply.*` 회귀 |
| RMK | 구독본과 작업 클론을 자동 탐색하고 기존 번역·XLSX 원문 이력을 읽으며 작업 클론에 XML/XLSX를 트랜잭션으로 병합한다. | `Core/Rmk`, `Rmk.*`, `Export.*` 회귀 |
| 품질·진단 | 안전 문제 탐색, 본문 없는 HTML 보고서와 개인정보를 제외한 진단 ZIP을 제공한다. | `Core/Quality`, `Core/Diagnostics` |
| UI | 밝음·어두움, 5개 컨셉, PerMonitorV2 DPI, 최소 창 크기, 비동기 로드, 취소 가능한 진행 상태와 종료 대기를 제공한다. | `App/ThemePalette.cs`, `MainForm.cs`, `UiControls.cs` |

## 호환성

- 기존 `projects.json`, `settings.json`, `review-decisions.json`과 RMK XLSX를 읽고 알 수 없는 JSON 필드를 보존한다.
- 프로젝트 데이터는 기존 `%LOCALAPPDATA%\RimWorldAiTranslator` 위치를 사용한다.
- 배포 대상은 Windows 10/11 x64다. 자체 포함 EXE이므로 별도 .NET SDK/Runtime은 필요하지 않다.
- 기존 PowerShell 소스 실행 경로는 v1.0.0에서 제거했다. 사용자 데이터 형식은 유지한다.

## 검증된 상태

- Release 솔루션 빌드: 경고 0, 오류 0.
- 이번 실행의 현재 오프라인 C# 회귀: 37/37 PASS. 합성 `%TEMP%` fixture와 loopback handler만 사용했다.
- 실제 로컬 Workshop/RMK/사용자 앱 데이터는 이번 RC 증거에 사용하지 않는다. 모든 UI/package 탐색 증거는 marker가 있는 고유 합성 root로 격리한다.
- win-x64 패키지 smoke는 최종 로컬 RC 단계에서 격리 탐색 ACK, 정상 창, 자식 프로세스 0, 정상 종료와 ExitCode 0을 새로 증명해야 한다.
- 5,000행 합성 측정은 `docs/QUALITY_GATES.md`에 기록한다.

## 알려진 제한과 기술 부채

| 우선순위 | 항목 | 현재 대응 |
|---|---|---|
| P1 조사 | 실제 외부 제공자의 모델 목록·제한은 시점별로 바뀐다. 모든 제공자를 실키로 자동 호출하는 릴리스 테스트는 없다. | 로컬 형식 검증과 loopback 회귀를 사용하며 사용자가 모델 ID를 수정할 수 있다. |
| P2 | 첫 실행은 자체 포함 단일 EXE 초기화 때문에 재실행보다 느리다. | 현재 측정 2.24초, 재실행 0.52초. 시작 중 미완성 화면은 공개하지 않는다. |
| P2 | 자동 UI 픽셀 비교와 다중 모니터 DPI 매트릭스는 별도 CI가 없다. | UI harness와 수동 접근성/레이아웃 검증을 사용한다. |
| P2 | `native/RimWorldTranslatorNative.cs`는 이전 구현 호환을 위해 nullable 검사를 끈 상태다. | Core 공개 경계에서 null·크기 검사를 수행하며 후속 정적 분석 대상으로 둔다. |
| P3 | 코드 서명 인증서가 없다. | 해시와 공개 소스를 제공하지만 SmartScreen 경고는 남을 수 있다. |

이 문서는 구현 상태를 설명하며 최종 PASS 선언이 아니다. Phase 05~10의 호환성·UI·보안·신뢰성·패키지·독립 감사가 끝날 때까지 남은 P0 여부와 RC 판정은 `docs/release-readiness/STATE.md`에서 추적한다.
