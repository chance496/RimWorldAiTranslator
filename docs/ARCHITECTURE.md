# Architecture

## 구성 요소

```text
RimWorldAiTranslator.exe
  -> Windows PowerShell
     -> Start-RimWorldAiReviewGui.ps1
        -> 프로젝트/설정/검수 상태 (%LOCALAPPDATA%)
        -> Storage / Validation / ProviderValidation / TranslationMemory / Diagnostics 모듈
        -> UiSystem / Quality 모듈
        -> Run-RimWorldAiTranslation.ps1
           -> Invoke-RimWorldAiTranslation.ps1
              -> Google 또는 OpenAI 호환 번역 API
              -> RimWorldTranslator.Native.dll (XML/Defs/RMK XLSX)
        -> Apply-RimWorldAiReviewResults.ps1 (로컬 Korean)
        -> Export-RimWorldAiReviewToRmk.ps1 (RMK 작업 클론 + XLSX)
        -> Export-RimWorldAiTranslatorDiagnostics.ps1 (본문 없는 로컬 진단 ZIP)
```

## 실행과 UI

- `launcher/RimWorldAiTranslatorLauncher.cs`가 패키지 EXE다. Windows 기본 PowerShell을 숨은 프로세스로 시작하고 WinForms 메인 창이 나타날 때까지 준비 창을 표시한다.
- `Start-RimWorldAiTranslatorGui.cmd`와 `Start-RimWorldAiTranslatorGui.ps1`는 소스 실행 경로다.
- `Start-RimWorldAiReviewGui.ps1`가 대시보드, 프로젝트, 모드 탐색, 제공자 설정, 검수 편집기, 저장, RMK와 자식 프로세스 제어를 모두 담당한다.
- 저장·검증·프로젝트 정리·제공자 설정 점검·동일 원문 번역 메모리·진단 집계는 독립 PowerShell 모듈로 분리되어 GUI와 CLI에서 공유한다.
- `RimWorldAiTranslator.UiSystem.ps1`는 테마 토큰, 번역 범위 추정, 단순 Diff와 실제 로그 단계 해석을 제공한다. `RimWorldAiTranslator.Quality.ps1`는 품질 문제 모델과 본문 없는 HTML 보고서를 제공한다.

## 데이터 흐름

1. UI가 Steam 라이브러리/로컬 Mods에서 모드를 찾고 프로젝트 하나에 모드 하나를 연결한다.
2. 원문 로드는 `SourceOnly + ReviewOnly`로 엔진을 별도 PowerShell 프로세스에서 실행한다.
3. 엔진은 `LoadFolders.xml`, 언어 XML, Defs를 읽고 번역 가능 항목과 제외 감사를 검수 run에 기록한다.
4. AI 번역은 검수 run에 후보만 만들며 원본 모드에 즉시 쓰지 않는다. 키는 환경 변수로 자식 프로세스에 전달된다.
5. UI는 `review-decisions.json`에 희소 결정, 상태, 출처, 시각, 원문 해시와 이전 원문을 저장한다.
6. 적용 시 별도 스크립트가 검수 상태와 안전 조건을 다시 검사해 로컬 Korean 또는 RMK 작업 클론에 병합한다.
7. 진단 번들은 원문·번역문·키·API 키·절대 경로·원시 로그를 전달하지 않고 설정/상태 개수와 오류 분류만 로컬 ZIP에 쓴다.
8. 품질 센터는 현재 검수 행과 희소 결정을 읽어 문제를 계산하고 가상 목록으로 표시한다. 번역 사전 점검과 작업 오버레이는 원본 모드 쓰기 전의 검수 단계에만 연결된다.

## 저장 경계

| 위치 | 역할 | 기본 정책 |
|---|---|---|
| `%LOCALAPPDATA%\RimWorldAiTranslator` | `projects.json`, `settings.json`, 캐시, `reviews` | 앱 소유. 실제 데이터는 테스트 입력으로 쓰지 않음 |
| Workshop 모드 | 원문과 기존 번역 참조 | 읽기 전용. 사용자가 로컬 적용을 명시한 경우 Korean만 대상 |
| RMK 구독본 | 기존 번역과 원문 이력 참조 | 항상 읽기 전용 |
| RMK Git 작업 클론 | 검수된 XML/XLSX 내보내기 | 사용자가 지정하고 확인한 경로만 쓰기 |
| `testdata` | 합성 fixture | 테스트에서 원본 읽기 전용, 출력은 임시 경로 |
| `dist` | 빌드 결과 | 생성물. `build-package.ps1`이 재생성 |

## 형식과 식별

- 원문/후보 감사: `_TranslationAudit/*-source.json`, `*-comparison.json/csv`, `*-token-warnings.json`, `*-skipped-internal-identifiers.json`.
- 사용자 결정: `review-decisions.json` version 5 sparse 형식.
- 적용 파일: RimWorld `LanguageData` XML.
- RMK 이력: RimworldExtractor 호환 6열 XLSX와 기존 XML.
- 승계 키: 파일+키 우선, 프로젝트에서 유일한 키 보조. RMK는 `Def Class + Node` 식별자를 사용한다.

## 외부 의존성과 신뢰 경계

- 런타임: Windows PowerShell, WinForms, .NET Framework.
- 빌드: Windows에 포함된 .NET Framework `csc.exe`; 별도 패키지 관리자는 없다.
- 네트워크: 선택한 번역 제공자. API URL은 HTTPS로 제한하고 키는 환경 변수로 전달한다.
- 입력 XML/XLSX는 불신한다. DTD/외부 엔터티 차단, 크기 제한, 경로 루트 검증과 유효한 XML 키 검사가 필요하다.

## 주요 기술 부채

- UI와 애플리케이션 계층이 하나의 390KB+ PowerShell 파일에 결합되어 있다.
- 오프라인 회귀와 UI/성능 runner는 있으나 원격 CI workflow는 없다.
- PowerShell과 native C#에 Def/토큰/안전 규칙이 중복되어 동기화 회귀 위험이 있다.
- 빌드와 패키징이 한 스크립트에 결합되어 빠른 build-only/verify-only 경로가 없다.
