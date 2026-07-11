# Quality Gates

기준 시각: 2026-07-12 KST. 아래 명령은 저장소의 스크립트, README 또는 현재 지침에서 확인했고, 추측한 도구 이름은 넣지 않았다. 모든 명령은 제품 Git 루트에서 실행한다.

## 현재 존재하는 명령

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

운영 지침에 요구되어 있고 이번 점검에서 실제 실행해 8개 스크립트 모두 통과한 명령:

```powershell
$failed = $false
Get-ChildItem -LiteralPath . -File -Filter '*.ps1' | Sort-Object Name | ForEach-Object {
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

현재 이 검사는 별도 저장소 스크립트나 패키징 단계에 연결되어 있지 않다.

### CLI 번역/원문 로드

README에 기록된 네트워크 키가 필요 없는 검수 출력 명령:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Invoke-RimWorldAiTranslation.ps1 `
  -ModRoot "<fixture-or-mod-path>" `
  -TranslationProvider Google `
  -ReviewOnly
```

`-SourceOnly`, `-MockTranslations`, `-DryRun` 매개변수는 엔진에 구현되어 있지만 이를 조합한 저장소 소유 테스트 명령은 아직 없다. 실제 사용자 모드나 네트워크를 자동 게이트에 사용하지 않는다.

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

- 자동 테스트 runner: 없음
- Pester 또는 다른 단위/통합 테스트: 없음
- 린터/formatter: 없음
- CI workflow: 없음
- benchmark runner: 없음
- 패키지 ZIP 압축 해제 후 실행 smoke test: 없음
- UI 스냅샷/DPI/접근성 자동 감사: 없음

`testdata/SampleMod`는 fixture일 뿐 실행 명령이나 기대 결과가 정의되어 있지 않다. 따라서 현재 “테스트 통과”라고 말할 수 있는 저장소 표준은 없다.

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

- PowerShell Parser: 8/8 PASS.
- `git diff --check`: whitespace 오류 없음. Git의 향후 LF→CRLF 변환 경고는 존재한다.
- 패키지 빌드/GUI/네트워크/실제 파일 쓰기: 실행하지 않음. 최근 호스트 불안정과 이번 문서 전용 범위 때문에 보류했다.
