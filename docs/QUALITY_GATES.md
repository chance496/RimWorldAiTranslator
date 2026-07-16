# Quality Gates

모든 명령은 저장소 루트 `tools/RimWorldAiTranslator`에서 실행한다. 기본 테스트는 `%TEMP%` 합성 fixture만 사용하며 실제 Workshop, RMK, `%LOCALAPPDATA%\RimWorldAiTranslator`, API 키와 외부 네트워크를 쓰지 않는다.

## 개발 환경

- Windows 10/11 x64
- .NET SDK 8.0.422 이상
- 릴리스 사용자에게는 SDK나 .NET Runtime이 필요하지 않다.

## 복원과 빌드

```text
dotnet restore RimWorldAiTranslator.sln --configfile NuGet.config
dotnet build RimWorldAiTranslator.sln -c Release --no-restore -p:TreatWarningsAsErrors=true
```

완료 조건: 모든 프로젝트가 빌드되고 경고 0, 오류 0.

## 오프라인 회귀

이 저장소의 회귀 runner는 테스트 SDK가 아니라 의존성 없는 콘솔 프로그램이다.

```text
dotnet run --project tests/RimWorldAiTranslator.Tests/RimWorldAiTranslator.Tests.csproj -c Release --no-build --no-restore
```

완료 조건: 출력된 전체 테스트가 모두 PASS이고 종료 코드가 0이다. 고정된 과거 개수를 완료 근거로 사용하지 않는다.

릴리스 후보의 연속 회귀 게이트는 빌드된 동일 Release DLL을 서로 다른 프로세스로 세 번 실행한다. 다음 세 명령이 모두 전체 PASS와 종료 코드 0이어야 하며, 한 번의 성공으로 대체하지 않는다.

```text
dotnet tests\RimWorldAiTranslator.Tests\bin\Release\net8.0\RimWorldAiTranslator.Tests.dll
dotnet tests\RimWorldAiTranslator.Tests\bin\Release\net8.0\RimWorldAiTranslator.Tests.dll
dotnet tests\RimWorldAiTranslator.Tests\bin\Release\net8.0\RimWorldAiTranslator.Tests.dll
```

## 성능 측정

```text
dotnet run --project .\tests\RimWorldAiTranslator.Benchmarks\RimWorldAiTranslator.Benchmarks.csproj -c Release --no-build -- --rows 5000 --iterations 5
```

완료 조건: 추출·검수 로드 결과가 정확히 5,000행, 키 검색 1행, 상태 필터 5,000행이며 JSON 측정 결과와 종료 코드 0을 출력한다. 동등한 전후 측정에서 20%를 넘는 중대한 회귀, 반복되는 200ms 초과 UI 검색·필터 지연, 문서화된 취소 응답 목표 위반은 릴리스 게이트다. 서로 동등하지 않은 경로의 수치와 그 밖의 숫자는 설명용 참고치로만 사용한다.

## 격리 UI harness

```text
dotnet run --project tests/RimWorldAiTranslator.UiHarness/RimWorldAiTranslator.UiHarness.csproj -c Release --no-build --no-restore -- --slow-bootstrap
dotnet run --project tests/RimWorldAiTranslator.UiHarness/RimWorldAiTranslator.UiHarness.csproj -c Release --no-build --no-restore -- --single-instance-probe
```

원시 App 명령은 기본 `%LOCALAPPDATA%`와 실제 discovery를 열 수 있으므로 품질 게이트로 직접 실행하지 않는다. UI harness는 고유한 임시 data/discovery root와 합성 fixture를 만들고 종료 시 그 루트만 정리한다. 실제 배포 EXE 시작·종료는 아래 C# package 명령의 격리 smoke에서만 검증한다. UI 변경은 최소 900x600과 일반 최대화 화면에서 밝음·어두움, 대시보드·설정·검수 화면, 글자 잘림, 목록 스크롤, 키보드와 중지 버튼을 확인한다.

## 자체 포함 패키지

검증된 자체 포함 릴리스 패키지를 만드는 개발 명령:

```text
dotnet run --project tools/RimWorldAiTranslator.Tooling -c Release --no-build --no-restore -- package
```

C# 도구는 다음을 순서대로 강제한다.

1. 외부 package source가 없는 복원, Release 솔루션 빌드와 전체 회귀.
2. `dotnet publish -r win-x64 --self-contained true` 단일 EXE.
3. EXE `FileVersion`과 `ProductVersion`이 `VERSION`에서 파생된 현재 버전과 일치.
4. EXE, 원본+DLC 용어집, Def 규칙, 문서·라이선스·버전만 ZIP에 포함.
5. `.ps1`, `.psm1`, `.cmd`, `.bat`, `powershell.exe`, `pwsh.exe` 패키지 유입 차단.
6. 격리 data/discovery root에서 탐색 완료 ACK와 창 표시, 자식 프로세스 0개, 정상 종료와 ExitCode 0.

용어집 도구의 합성 self-test도 패키지 전에 별도로 통과해야 한다.

```text
dotnet run --project tools/RimWorldAiTranslator.GlossaryTool -c Release --no-build --no-restore -- self-test
```

## 정적·릴리스 검사

```text
rg -n -i "csk-|sk-[A-Za-z0-9_-]{20,}|api[_ -]?key" --glob '!dist/**' --glob '!bin/**' --glob '!obj/**'
dotnet run --project tools/RimWorldAiTranslator.Tooling -c Release --no-build --no-restore -- verify-zero
git diff --check
```

- 키 검색 결과는 UI 라벨·테스트용 가짜 값·보안 규칙만 수동 판독한다. 실제 키 형태 값은 0개여야 한다.
- `src`의 PowerShell 런타임 호출은 0개여야 한다.
- `git diff --check` 오류는 0개여야 한다.
- ZIP을 별도 임시 폴더에 풀어 EXE 버전, 현재 allowlist의 모든 필수 항목, 금지 런타임 0개와 SHA-256을 다시 확인한다.

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
- 같은 Release DLL의 전체 오프라인 회귀를 별도 프로세스로 3회 연속 실행해 매번 전체 PASS와 종료 코드 0.
- 5,000행 벤치마크 행·검색 결과 일치.
- UI harness 및 실제 배포 EXE 정상 표시·취소·종료.
- ZIP 금지 런타임 0개, PowerShell 자식 0개, 정확한 버전과 SHA-256.
- README, 패키지 안내, 릴리스 노트, VERSION, PE와 ZIP이 같은 현재 버전을 가리킨다.
- 이미 공개된 tag/Release/asset은 조용히 교체하지 않으며 외부 배포는 사용자가 명시적으로 승인한 경우에만 수행한다.
