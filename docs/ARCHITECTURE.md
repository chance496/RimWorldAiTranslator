# Architecture

기준: 공개 v1.1.0

## 실행 구조

```text
RimWorldAiTranslator.exe (WinExe, net8.0-windows, self-contained win-x64)
  -> RimWorldAiTranslator.App
     -> MainForm + 화면별 UserControl
     -> AppServices 조립 및 비동기 오케스트레이션
  -> RimWorldAiTranslator.Core
     -> 프로젝트/설정/검수 저장
     -> 모드 탐색과 안전한 XML 추출
     -> 제공자 검증, API 재시도/키 순환, 체크포인트
     -> 검색/필터/번역 메모리/품질 검사
     -> 로컬 Korean 적용, RMK XML/XLSX 트랜잭션
     -> 개인정보 보호 진단 번들
  -> RimWorldAiTranslator.Native
     -> 안전한 XML 및 RMK XLSX 저수준 처리
```

사용자 실행 경로와 활성 build/test/glossary/package 경로에는 PowerShell, Python, Node.js, 콘솔 스크립트 또는 별도 .NET 설치 의존성이 없다. 개발 도구는 `tools/RimWorldAiTranslator.GlossaryTool`과 `tools/RimWorldAiTranslator.Tooling`의 C# 실행 파일이다.

## 계층 책임

| 계층 | 경로 | 책임 |
|---|---|---|
| App | `src/RimWorldAiTranslator.App` | WinForms 셸, 대시보드, 설정, 검수 작업실, 대화상자, 테마, 진행·취소·종료 흐름 |
| Core | `src/RimWorldAiTranslator.Core` | UI 독립 저장·탐색·추출·번역·검수·품질·적용·RMK·진단 서비스 |
| Native | `src/RimWorldAiTranslator.Native`, `native/RimWorldTranslatorNative.cs` | XML/XLSX 형식 보존, 크기 제한, 저수준 읽기·쓰기 |
| Tests | `tests/RimWorldAiTranslator.Tests` | 임시 fixture 기반 전체 오프라인 회귀 |
| UI harness | `tests/RimWorldAiTranslator.UiHarness` | 실제 WinForms 화면을 격리 데이터 루트로 실행 |
| Benchmarks | `tests/RimWorldAiTranslator.Benchmarks` | 5,000행 추출·검수 로드·검색·필터·취소 측정 |
| Tools | `tools/RimWorldAiTranslator.*` | 결정적 용어집 생성·self-test, 검증된 release package와 zero audit |

`AppServices`가 저장소와 Core 서비스를 한 번 조립한다. `MainForm`은 화면 전환과 작업 수명만 관리하고, 실제 데이터 규칙은 Core에 둔다. 긴 작업은 `Task`와 `CancellationToken`을 사용하며 표시 중인 검수 화면과 중지 버튼을 유지한다.

## 데이터 흐름

1. `RimWorldModDiscoveryService`가 Steam 라이브러리, RimWorld 설치와 명시적 폴더에서 모드를 찾는다.
2. 사용자가 프로젝트를 만들면 원문 언어를 선택하고 `SourceExtractor`가 `LoadFolders.xml`, `Keyed`, `DefInjected`와 허용된 Def 표시 필드를 읽는다.
3. `TranslationEngine`은 기존 번역과 RMK 참고 번역을 병합해 비교 JSON을 만들고, API 또는 Google 후보를 배치 단위로 기록한다.
4. `ReviewWorkspaceService`가 비교 JSON과 결정을 안정적인 항목 ID로 결합한다. 원문 변경은 번역을 보존하되 `pending/sourceChanged`로 내린다.
5. `ReviewApplyService` 또는 `RmkExportService`가 상태·원문 해시·토큰·경로·중복을 재검증한 뒤 트랜잭션으로 출력한다.

## 저장과 호환성

- 기본 앱 데이터: `%LOCALAPPDATA%\RimWorldAiTranslator`.
- `projects.json`, `settings.json`, `review-decisions.json`은 알려지지 않은 JSON 속성을 보존한다.
- JSON은 UTF-8 임시 파일에 기록해 flush한 뒤 교체한다. 정상 백업으로 복구하며 주 파일과 백업이 모두 손상되면 쓰기를 차단한다.
- 검수 결정은 대상 파일+키, ID, 유일 키 순으로 승계한다. 모호한 단일 키는 자동 승계하지 않는다.
- RMK XLSX는 번역 당시 원문, `Required Mods`, 추가 열과 비대상 워크북 구조를 보존한다. 서로 다른 원문 언어는 변경 비교 대상이 아니다.

## 쓰기 경계

| 위치 | 기본 동작 | 쓰기 조건 |
|---|---|---|
| Workshop 구독본 | 항상 읽기 전용 | 없음 |
| 사용자가 지정한 로컬 모드 | 기본 읽기 전용 | dry-run 미리보기와 명시적 확인 뒤 `Languages\Korean`만 |
| RMK 구독본 | 항상 읽기 전용 | 없음 |
| RMK Git 작업 클론 | 읽기/쓰기 대상 후보 | 사용자가 `RMK에 적용`을 선택했을 때만 |
| 앱 데이터 루트 | 앱 소유 | 프로젝트·설정·검수·캐시·비민감 로그 |

프로젝트 삭제는 앱 소유 검수 루트만 제거한다. 원본 모드와 이미 적용된 Korean 폴더는 삭제하지 않는다.

## 보안 경계

- `SafeXml`은 DTD와 외부 엔터티를 금지하고 입력 크기를 제한한다.
- 제공자 URL은 HTTPS 원격 주소 또는 테스트용 HTTP loopback만 허용한다.
- API 키는 설정 모델, 로그, 검수, 진단과 품질 보고서에 직렬화하지 않는다.
- 진단 ZIP은 본문, 키, 절대 경로, 원시 로그와 URL 자격 증명을 제외한다.
- 내부 식별자와 렌더·경로·패치 필드는 추출 단계에서 제외하고 감사 개수만 남긴다.

## 배포

`RimWorldAiTranslator.Tooling package`는 소스가 없는 오프라인 복원, 경고 0 빌드, 전체 회귀, `win-x64` 자체 포함 publish, 버전·허용목록·ZIP 검증과 격리 탐색/정상 종료 smoke를 수행한다. ZIP은 평면 구조의 단일 EXE, 경로가 제거된 원본+DLC 용어집, 규칙과 문서만 포함한다. 추가 용어집은 사용자가 설정에서 로컬 파일을 선택한다.
