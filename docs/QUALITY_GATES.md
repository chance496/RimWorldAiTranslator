# Quality Gates

기준 시각: 2026-07-12 KST. 아래 명령은 저장소의 스크립트, README 또는 현재 지침에서 확인했고, 추측한 도구 이름은 넣지 않았다. 모든 명령은 제품 Git 루트에서 실행한다.

## 현재 존재하는 명령

### 오프라인 회귀 테스트

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Run-RegressionTests.ps1 -Suite All
```

테스트는 합성 fixture를 고유한 `%TEMP%` 폴더에 복제하며 실제 사용자 프로젝트와 외부 네트워크를 사용하지 않는다. API 회귀는 루프백 TCP 가짜 서버를 쓴다. 작업 중에는 `-Suite Harness`, `Syntax`, `StateStore`, `SecretHandling`, `ProviderValidation`, `TranslationMemory`, `Diagnostics`, `UiTools`, `ProjectCleanup`, `DryRun`, `DefSafety`, `DuplicateIdentity`, `TokenSafety`, `ApiResilience`, `DirectOutput`, `LocalApply`, `LocalRollback`, `RmkExport`, `RmkHistory`처럼 가장 좁은 suite부터 실행한다.

### 실행

README와 실제 CMD 진입점:

```powershell
.\Start-RimWorldAiTranslatorGui.cmd
```

CMD는 다음 PowerShell 진입점을 호출한다.

```powershell
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File .\Start-RimWorldAiTranslatorGui.ps1
```

`Start-RimWorldAiTranslatorGui.ps1`는 다시 `Start-RimWorldAiReviewGui.ps1`를 `-NoProfile -STA -ExecutionPolicy Bypass`로 실행한다. 릴리스 사용자는 `RimWorldAiTranslator.exe`를 실행한다.

### 빌드 및 패키징

README와 `build-package.ps1`에서 확인한 유일한 빌드/패키징 명령:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build-package.ps1
```

이 명령은 Windows .NET Framework `csc.exe`로 `launcher/RimWorldAiTranslatorLauncher.cs`와 `native/RimWorldTranslatorNative.cs`를 컴파일하고 `dist/RimWorldAiTranslator` 및 `dist/RimWorldAiTranslator.zip`을 다시 만든다. 별도의 build-only 명령은 없다.

### PowerShell 정적 구문 검사

운영 지침에 요구되어 있고 이번 최종 점검에서 실제 실행해 소스·테스트 20개 스크립트 모두 통과한 명령:

```powershell
$failed = $false
Get-ChildItem -LiteralPath . -Recurse -File -Filter '*.ps1' |
Where-Object { $_.FullName -notlike '*\dist\*' } |
Sort-Object FullName | ForEach-Object {
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) {
        $failed = $true
        $errors | ForEach-Object { Write-Error "$($_.Extent.StartLineNumber): $($_.Message)" }
    }
}
if ($failed) { exit 1 }
```

동일 Parser 검사는 오프라인 회귀와 `build-package.ps1`의 패키지 복사 후 단계에 연결되어 있다.

### UI·성능·접근성 감사

5,000행 합성 검수 프로젝트를 고유한 `%TEMP%` 작업공간과 격리 앱 데이터에서 실행한다. 시작 빈 상태·프로젝트·설정·검수·사전 점검·명령 팔레트·로딩·오류·취소·완료·품질·번역 메모리를 포함한 15개 상태, 밝음·어두움·고대비, 최소/일반/대형 창, 글자 크기 10/12의 PNG·접근성 JSON과 성능 JSON을 남기며 실제 사용자 프로젝트나 네트워크를 사용하지 않는다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Run-UiPerformanceAudit.ps1 `
  -Rows 5000 `
  -Iterations 5 `
  -OutputRoot "$env:TEMP\RimWorldAiTranslator-ui-audit"
```

검색 결과와 `미번역`/`번역됨` 필터 개수, 비어 있지 않은 화면, 보이는 상호작용 컨트롤의 접근성 이름, 부모 영역 잘림을 함께 단언한다. 실제 DPI는 보고서의 `dpiX`/`dpiY`로 기록한다.

한 상태만 재현할 때는 저장소가 제공하는 `-ScenarioName`을 사용한다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Run-UiPerformanceAudit.ps1 `
  -Rows 5000 -Iterations 1 -ScenarioName quality-center-light `
  -OutputRoot "$env:TEMP\RimWorldAiTranslator-quality-audit"
```

### RMK XLSX 성능 benchmark

고유한 `%TEMP%` 안에서만 5,000행 RMK XML/XLSX를 생성하고 같은 workbook 갱신을 반복한다. 생성·갱신 시간, 최악값, 자식 프로세스 최대 working set과 workbook 크기를 JSON으로 기록하고 XLSX ZIP 구조를 검증한다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Run-RmkPerformanceBenchmark.ps1 `
  -Rows 5000 `
  -Iterations 3 `
  -OutputPath "$env:TEMP\RimWorldAiTranslator-rmk-performance.json"
```

### CLI 번역/원문 로드

README에 기록된 네트워크 키가 필요 없는 검수 출력 명령:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Invoke-RimWorldAiTranslation.ps1 `
  -ModRoot "<fixture-or-mod-path>" `
  -TranslationProvider Google `
  -ReviewOnly
```

`-SourceOnly`, `-DryRun`과 합성 검수 결정을 조합한 저장소 소유 회귀가 있으며 실제 사용자 모드나 네트워크를 자동 게이트에 사용하지 않는다. `-MockTranslations`의 배치 재시도·재개 검증은 P1에서 확장한다.

### 검수 결과 적용

README의 로컬 적용 명령이며, 안전 점검에서는 반드시 `-DryRun`을 추가한다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Apply-RimWorldAiReviewResults.ps1 `
  -ModRoot "<fixture-mod-path>" `
  -ReviewRoot "<fixture-review-path>" `
  -Overwrite `
  -ApplyStatus ApprovedOnly `
  -DryRun
```

### RMK 내보내기

README의 RMK 병합 명령이며, 안전 점검에서는 반드시 fixture와 `-DryRun`을 사용한다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Export-RimWorldAiReviewToRmk.ps1 `
  -RmkEntryRoot "<fixture-rmk-entry-path>" `
  -ReviewRoot "<fixture-review-path>" `
  -SourceLanguage English `
  -Overwrite `
  -ApplyStatus ApprovedOnly `
  -DryRun
```

## 존재하지 않는 게이트

- Pester 테스트 프레임워크: 없음. 저장소 소유 `Run-RegressionTests.ps1` 통합 runner를 사용한다.
- 린터/formatter: 없음
- CI workflow: 없음
- 패키지 ZIP 압축 해제 후 GUI smoke의 자동 빌드 연결: 없음. CLI 원문 추출은 빌드에 연결했고, 최종 점검에서는 패키지 스크립트와 EXE를 격리 앱 데이터로 별도 실행했다.
- 125/150/200% 실제 DPI 자동 감사: 없음. 현재 runner는 실행 환경의 실제 DPI를 기록하지만 Windows 디스플레이 배율을 바꾸지 않는다.

`testdata/SampleMod`의 기대 원문 7개, 내부 식별자 제외 1개, 로컬/RMK 적용과 롤백 기대값이 회귀 runner에 정의되어 있다.

## 백업 복구

- 기존 XML/XLSX를 교체하면 같은 경로의 `<파일>.bak`에 직전 버전이 남는다.
- 앱과 RimWorld를 종료한 뒤 현재 파일을 별도 보관하고 `.bak`을 원래 파일명으로 복사하면 수동 복구할 수 있다.
- 다중 파일 적용 도중 실패하면 해당 실행에서 이미 쓴 파일은 자동으로 직전 상태로 롤백된다. 롤백 자체가 실패하면 오류에 대상 경로가 표시되며 성공으로 처리하지 않는다.

## 변경별 필수 게이트

| 변경 영역 | 최소 필수 검증 |
|---|---|
| 문서/AGENTS만 | 링크·경로 확인, 바이트 크기, `git diff --check`, 제품 소스 무변경 확인 |
| PowerShell 로직 | Parser, 대상 fixture 회귀, `git diff --check` |
| Native C# | Parser가 아닌 `build-package.ps1`의 C# 컴파일, XML/XLSX fixture, 패키지 확인 |
| 저장/적용/RMK | 손상·중단·경로 이탈·중복 키·원문 변경·토큰 사례, `DryRun`, 실패 주입, 백업/복구 확인 |
| UI | Parser, 핵심 흐름, 최소/일반 창, 100/125/150/200% DPI, 밝음/어두움/고대비, 키보드, 전후 스크린샷 |
| 성능 | 동일 fixture와 출력, warm/cold 조건 명시, 반복 수, 중앙값·최악값·메모리 전후 기록 |
| 릴리스 | 전체 회귀, 패키지 빌드, 필수 파일 목록, 새 폴더 압축 해제, EXE 시작/종료, 소스-패키지 일치 |

## 이번 점검 실행 결과

- 오프라인 회귀: 20/20 PASS(최종 패키지 게이트 39.780초). 취소·부분 체크포인트·재시도·재개는 로컬 TCP 가짜 API로 검증했고 제공자 설정·번역 메모리·진단 privacy·UI/품질 순수 도구도 포함한다.
- 패키지 빌드: PASS. native 검증 컴파일, 패키지 PowerShell Parser와 새 임시 폴더 ZIP 원문 추출 7행 smoke PASS. 패키지 PowerShell 16개, 전체 25개 파일, 필수 EXE·DLL과 349,557바이트 ZIP을 확인하고 README 두 파일의 소스/패키지 SHA-256 일치를 확인했다.
- UI 감사: 15/15 PASS. 5,000행 검색 결과 295/5,000개 및 상태 필터 2,667/2,333개 일치, 모든 작업 상태와 도구 화면 포함, 잘림 0건, 접근성 이름 누락 0건. 로드 965.0ms, 검색 중앙 1,024.1ms, 다음 36.0ms, 저장 442.8ms, 무변경 저장 0.16ms, working set 258.79MB다.
- 품질 센터: 5,000행 첫 상태 화면 21,566.6→12,729.7ms, 내부 계산 5.6초. 희소 결정을 만들지 않고 필터는 캐시·가상 목록을 사용한다.
- RMK benchmark: 5,000행 생성 7,089.816ms, 갱신 중앙 8,983.882ms/최악 9,429.046ms, 최대 working set 323.02MB.
- 패키지 실행: 격리 앱 데이터로 패키지 EXE 시작 화면을 900×600에서 렌더링했다. EXE `ExitCode=0`, 잘림·접근성 이름 누락 0건이다.
- 네트워크, 실제 API, Workshop, RMK 구독본과 `%LOCALAPPDATA%\RimWorldAiTranslator` 쓰기: 실행하지 않음.
