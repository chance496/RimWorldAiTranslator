# Quality Gates

모든 명령은 저장소 루트 `tools/RimWorldAiTranslator`에서 실행한다. 기본 테스트는 `%TEMP%` 합성 fixture만 사용하며 실제 Workshop, RMK, `%LOCALAPPDATA%\RimWorldAiTranslator`, API 키와 외부 네트워크를 쓰지 않는다.

## 개발 환경

- Windows 10/11 x64
- .NET SDK 8.0.422 이상
- 릴리스 사용자에게는 SDK나 .NET Runtime이 필요하지 않다.

## 복원과 빌드

```powershell
dotnet restore .\RimWorldAiTranslator.sln
dotnet build .\RimWorldAiTranslator.sln -c Release --no-restore
```

완료 조건: 모든 프로젝트가 빌드되고 경고 0, 오류 0.

## 오프라인 회귀

이 저장소의 회귀 runner는 테스트 SDK가 아니라 의존성 없는 콘솔 프로그램이다.

```powershell
dotnet run --project .\tests\RimWorldAiTranslator.Tests\RimWorldAiTranslator.Tests.csproj -c Release --no-build
```

완료 조건: `30/30 tests passed.`와 종료 코드 0. 저장·복구, 키 비저장, 선택형 추가 용어집, XML/Def 안전, 다중 키·재시도, 취소·재개, 로컬 적용, RMK 트랜잭션·원문 이력, 진단 개인정보를 포함한다.

## 성능 측정

```powershell
dotnet run --project .\tests\RimWorldAiTranslator.Benchmarks\RimWorldAiTranslator.Benchmarks.csproj -c Release --no-build -- --rows 5000 --iterations 5
```

완료 조건: 추출·검수 로드 결과가 정확히 5,000행, 키 검색 1행, 상태 필터 5,000행이며 JSON 측정 결과와 종료 코드 0을 출력한다. 성능 수치는 회귀 참고치이며 절대 통과 한계로 사용하지 않는다.

## 소스 실행과 UI harness

```powershell
dotnet run --project .\src\RimWorldAiTranslator.App\RimWorldAiTranslator.App.csproj -c Release
dotnet run --project .\tests\RimWorldAiTranslator.UiHarness\RimWorldAiTranslator.UiHarness.csproj -c Release
```

UI harness는 고유한 임시 데이터 루트를 만들고 종료 시 그 루트만 정리한다. UI 변경은 최소 900x600과 일반 최대화 화면에서 밝음·어두움, 대시보드·설정·검수 화면, 글자 잘림, 목록 스크롤, 키보드와 중지 버튼을 확인한다.

## 자체 포함 패키지

실제로 확인한 개발 보조 명령:

```powershell
& "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File .\build-package.ps1
```

스크립트는 다음을 순서대로 강제한다.

1. Release 솔루션 빌드와 30개 회귀.
2. `dotnet publish -r win-x64 --self-contained true` 단일 EXE.
3. EXE `FileVersion=1.0.0.0`, `ProductVersion=1.0.0`.
4. EXE, 원본+DLC 용어집, Def 규칙, 문서·라이선스·버전만 ZIP에 포함.
5. `.ps1`, `.psm1`, `.cmd`, `.bat`, `powershell.exe`, `pwsh.exe` 패키지 유입 차단.
6. 격리 데이터 루트에서 창 표시, PowerShell 자식 0개, 10초 안 정상 종료와 ExitCode 0.

`build-package.ps1`은 개발 도구일 뿐 배포 ZIP이나 사용자 실행 경로에 포함되지 않는다.

## 정적·릴리스 검사

```powershell
rg -n -i "csk-|sk-[A-Za-z0-9_-]{20,}|api[_ -]?key" --glob '!dist/**' --glob '!bin/**' --glob '!obj/**'
rg -n -i "powershell|pwsh|\.ps1" src
git diff --check
```

- 키 검색 결과는 UI 라벨·테스트용 가짜 값·보안 규칙만 수동 판독한다. 실제 키 형태 값은 0개여야 한다.
- `src`의 PowerShell 런타임 호출은 0개여야 한다.
- `git diff --check` 오류는 0개여야 한다.
- ZIP을 별도 임시 폴더에 풀어 EXE 버전, 필수 8개 항목, 금지 런타임 0개와 SHA-256을 다시 확인한다.

## 2026-07-13 측정 스냅샷

호스트 `CHANCE`, .NET 8.0.28, Release, 5,000행 합성 fixture. 측정치는 같은 호스트에서의 회귀 비교용이다.

| 항목 | 이전 PowerShell 기준 | C# v1.0.0 |
|---|---:|---:|
| 첫 창 표시 | 약 3.02~3.37초 | 2.239초 |
| 같은 패키지 재실행 | 미측정 | 0.517초 |
| 검수 프로젝트 열기 5,000행 | 840.7ms | 중앙 88.613ms, p95 124.435ms |
| 원문 XML 추출 5,000행 | 별도 동일 측정 없음 | 중앙 19.292ms, p95 25.598ms |
| 키 검색 5,000행 | 첫 1,153.9ms, 중앙 979.5ms | 중앙 9.229ms, p95 11.935ms |
| 상태 필터 5,000행 | 결과만 검증 | 중앙 0.446ms, p95 0.578ms |
| 사전 취소 추출 | 미측정 | 1.157ms |
| 앱 작업 집합 | 238.9MiB | 첫 167.6MiB, 재실행 146.1MiB |
| 정상 종료 | 미측정 | 0.313~0.395초, ExitCode 0 |
| 배포 크기 | 약 356KiB(시스템 PowerShell 의존) | ZIP 66,369,738바이트(자체 포함) |

검색 최대값 202.708ms는 JIT/GC가 포함된 단일 표본이므로 P2 계측 대상으로 남기고 중앙·p95와 분리한다.

## 릴리스 완료 조건

- 빌드 경고 0/오류 0.
- 오프라인 회귀 30/30.
- 5,000행 벤치마크 행·검색 결과 일치.
- UI harness 및 실제 배포 EXE 정상 표시·취소·종료.
- ZIP 금지 런타임 0개, PowerShell 자식 0개, 정확한 버전과 SHA-256.
- README, 패키지 안내, 릴리스 노트, GitHub 설명과 태그가 같은 `v1.0.0`을 가리킨다.
