# 04 — C# 구현, 구조 개선과 PowerShell 0%

## 1. 기본 전략

- Golden Master의 사용자 흐름·화면·파일·번역 동작을 먼저 보존한다.
- 현재 C# Core·저장·번역·RMK 구현이 맞으면 재사용한다.
- 잘못 만든 UI와 결합 구조는 필요하면 다시 작성한다.
- UI 재설계, 새 기능, 프롬프트 개선, framework 업그레이드를 동시에 섞지 않는다.
- 기능 ID 단위로 구현하고 대응 테스트가 통과한 뒤 다음 기능으로 이동한다.

## 2. UI 프레임워크

기존 화면 자산과 C# 후보가 사용하는 프레임워크를 조사한 뒤 가장 적은 회귀 위험을 선택한다.

- 기존 XAML이 재사용 가능한 WPF UI라면 C# WPF 이전을 우선 검토한다.
- 기존 안정판 또는 검증된 C# 후보가 WinForms라면 UI 동등성을 보존하는 WinForms가 합리적일 수 있다.
- WPF↔WinForms↔Avalonia 등 프레임워크 교체는 공개 품질 작업의 기본 해법이 아니다.
- 프레임워크 선택 근거와 포기한 대안을 DECISIONS에 기록한다.

## 3. 권장 코드 경계

현재 규모에 맞춰 최소한 다음 책임을 분리한다.

```text
App/UI
  Forms/Views, Controls, Presenter/ViewModel, UI orchestration
Application/Core
  Projects, Extraction, Translation, Review, Quality, Apply, RMK
Domain/Models
  Project, TranslationEntry, state transitions, validation results
Infrastructure
  FileSystem, JSON/XML/XLSX, HTTP, Logging, Platform
Tools
  fixture, benchmark, package, audit utilities
Tests
  Unit, Integration, UI Harness, Package Smoke
```

프로젝트를 무리하게 많이 쪼개지 않아도 되지만 다음은 금지한다.

- UI 이벤트에서 직접 파일 저장·HTTP·외부 프로세스 호출
- 모든 상태를 static 전역 변수로 유지
- 수천~수만 줄의 단일 Form/Window/ViewModel
- 모든 서비스를 하나의 거대 Service 객체에 집약
- 핵심 로직이 UI 생성 없이는 테스트 불가

## 4. 비동기·취소·상태

- 파일·XML·XLSX·API·대량 검증을 UI 스레드에서 실행하지 않는다.
- `async/await`를 끝까지 전달하고 `.Result`, `.Wait()`, `Thread.Sleep()`을 UI 경로에서 사용하지 않는다.
- 이벤트 핸들러를 제외한 불필요한 `async void`를 금지한다.
- 긴 작업은 `CancellationToken`과 명시적 진행 상태를 지원한다.
- 취소는 “요청됨”, “안전한 지점에서 중단됨”, “완료됨”을 구분한다.
- 취소·실패 후 상태를 완료로 표시하지 않는다.
- `IDisposable`, stream, timer, token source, event subscription을 해제한다.
- 앱 종료 후 작업·자식 프로세스가 남지 않게 한다.

## 5. 오류와 로그

- 사용자 메시지와 개발자 상세 로그를 분리한다.
- 빈 catch와 예외 삼키기를 금지한다.
- 실패를 로그만 남기고 성공으로 반환하지 않는다.
- 오류는 원인, 영향, 복구 방법을 사용자에게 설명한다.
- 로그에 API 키, 인증 헤더, 전체 원문과 민감한 경로를 남기지 않는다.
- 로그 쓰기 실패가 제품 데이터를 손상시키지 않게 한다.

## 6. 컴파일·SDK 정책

- `Nullable=enable`
- deterministic build
- 지원되는 정식 SDK를 `global.json`으로 고정
- preview SDK·preview package 금지
- 공개 시점에 충분한 지원 기간이 남은 LTS 우선
- `TreatWarningsAsErrors=true` 또는 동등한 엄격한 정책
- Microsoft 권장 분석기 활성화
- Release 빌드 경고 0 목표

framework 업그레이드는 UI·기능 동등성 후 별도 변경으로 수행하고 전체 회귀를 다시 실행한다.

## 7. 기능 이전 루프

각 기능 ID마다:

1. 기준 동작 재현
2. C# 차이 테스트 작성
3. 최소 구현 또는 수정
4. 성공·취소·오류·재시작 테스트
5. 관련 UI·데이터·보안 검사
6. 기능표·STATE 갱신

완료 금지 사례:

- 버튼은 있지만 실제 로직이 없음
- 성공만 있고 취소·실패가 없음
- 설정이 재실행 후 사라짐
- 검색·필터 결과나 기본 선택이 다름
- 적용은 되지만 백업·롤백이 없음
- XLSX 결과는 나오지만 스타일·주석·추가 열이 사라짐
- API 호출은 되지만 프롬프트·토큰·배치가 다름

## 8. PowerShell 제거 순서

PowerShell 파일을 먼저 삭제하지 않는다.

1. 추적 `.ps1`, `.psm1`, `.psd1` 전수 목록
2. 파일별 기능 ID와 호출자 확인
3. C# 대응 코드와 테스트 확인
4. 빌드·테스트·패키징 대체 확인
5. 파일 삭제
6. 문서·CI·프로젝트 파일의 호출 제거
7. 전수 검색
8. PowerShell 없이 clean build/test/package/run

검색 범위:

```text
*.ps1, *.psm1, *.psd1
powershell.exe, pwsh.exe, powershell, pwsh
Process.Start, UseShellExecute, cmd.exe
shell: powershell, shell: pwsh
MSBuild Exec, batch/cmd, 문자열·리소스·압축 파일
```

`Process.Start` 자체는 절대 금지가 아니지만 실행 파일 allowlist, 경로, 인자, 종료, shell 사용을 검토한다.

## 9. 개발 도구 대체

- PowerShell 회귀 runner → `dotnet test` 또는 C# test host
- UI audit → C# UI Automation/harness
- benchmark → C# benchmark/console tool
- 패키징 → MSBuild target 또는 C# tool
- CI run step → PowerShell 없는 action 또는 `cmd`
- 사용자 실행 → EXE 직접 실행

## 10. 금지 우회

- PowerShell 코드를 C# 문자열·리소스로 임베드
- `.gitattributes`로 언어 통계만 조작
- `.ps1` 확장자만 변경
- C# EXE가 내부에서 PowerShell을 호출
- 테스트에서만 PowerShell을 남기고 0%라고 주장
- Git 이력 보존을 이유로 현재 후보에 스크립트 복사본 유지

## Exit gate

- 기능표 필수 행 전부 구현·테스트 연결
- 단일 거대 UI 클래스와 직접 I/O·HTTP 결합 없음
- Release 빌드 경고 0 또는 blocker로 명확히 기록
- 추적 PowerShell 소스 0
- runtime/build/test/package/CI 후보 PowerShell 호출 0
- clean C# build·test·앱 실행 성공
