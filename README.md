# RimWorld AI Translator

RimWorld 모드 하나를 로컬 프로젝트 하나로 관리하며 원문 추출, AI 초벌 번역, 직접 편집, 검수, 업데이트 추적, `Languages\Korean` 적용과 RMK 작업 클론 내보내기를 처리하는 Windows 데스크톱 도구입니다.

**v1.0.0부터 메인 프로그램과 번역 엔진은 C#/.NET 8로 실행됩니다. 사용자 실행 과정에서 PowerShell, Python, Node.js, Git 또는 별도 .NET 설치가 필요하지 않습니다.**

## 설치

1. [Releases](https://github.com/chance496/RimWorldAiTranslator/releases)에서 `RimWorldAiTranslator-v1.0.0.zip`을 받습니다.
2. ZIP을 원하는 폴더에 완전히 압축 해제합니다.
3. `RimWorldAiTranslator.exe`를 실행합니다.

배포본은 Windows 10/11 x64용 자체 포함 실행 파일입니다. Microsoft SmartScreen은 서명되지 않은 개인 배포 프로그램에 경고할 수 있습니다.

## 주요 기능

- Steam 라이브러리와 RimWorld 설치 위치에서 Workshop 및 로컬 모드를 자동 탐색합니다.
- 프로젝트 하나에 모드 하나를 연결하고 검수 상태, 메모, 번역 출처와 과거 원문을 로컬에 저장합니다.
- `Keyed`, `DefInjected`와 검증된 Def 표시 필드를 추출합니다.
- 내부 식별자, 경로, 렌더 트리 기술 필드와 안전하지 않은 Def 필드는 번역 목록에서 제외하고 감사 자료에 기록합니다.
- 영어, 중국어, 일본어, 스페인어 등 실제 언어 폴더를 감지하며 여러 원문 언어가 있으면 프로젝트 생성 시 선택합니다.
- Cerebras, OpenAI, Gemini, DeepSeek, Qwen, Groq, Mistral, OpenRouter, Z.AI와 사용자 지정 OpenAI 호환 API를 지원합니다.
- API 키를 줄마다 하나씩 입력하면 키별 제한을 적용해 순환 사용합니다. API 키가 없으면 Google 번역 후보를 사용합니다.
- 기존 번역이 있을 때 `전체 덮어쓰기`, `미번역만 번역`, `취소` 중 하나를 선택합니다.
- 요청 실패 재시도, 손상된 JSON 배치 분할, 완료 배치 체크포인트, 취소와 재개를 지원합니다.
- RimWorld 변수, 태그, 포맷 토큰, 문법 접두어와 `(은)는`, `(이)가` 자동 조사 형식을 번역·검수·적용 단계에서 검사합니다.
- 같은 원문의 수동 번역을 프로젝트 전체에 일괄 적용할 수 있습니다.
- 미번역, 번역됨, 검토됨, 업데이트로 변경됨, 주의, RMK 가져옴, 내 번역 상태를 검색·필터·정렬합니다.
- `Def Class`와 `Node`의 의미를 현재 항목 옆에 표시합니다.
- 원본 RimWorld+DLC 용어집을 기본 제공하고 설정에서 TXT/TSV/CSV/JSON 추가 용어집을 선택할 수 있습니다.
- 빈 번역, 변경된 원문과 안전 경고를 제외한 항목을 한 번에 검토 완료 처리합니다.
- 원문 업데이트 시 기존 번역은 보존하고 바뀐 문자열을 `업데이트로 변경됨 + 미번역`으로 내려 적용을 차단합니다.
- 검토됨만 적용하거나 번역됨까지 포함해 모드의 `Languages\Korean`에 원자적으로 기록합니다.
- RMK 구독본을 읽기 전용 참고 번역으로 자동 탐색하고, 사용자가 선택한 RMK Git 작업 클론에 XML과 림추출기 호환 XLSX를 병합합니다.
- 품질 문제 목록과 개인정보를 제외한 HTML 품질 보고서, 진단 ZIP을 제공합니다.
- 프로젝트 삭제는 앱이 소유한 검수 폴더만 제거하며 원본 모드와 모드 안의 Korean 폴더는 보존합니다.

## 기본 사용법

1. `프로젝트`에서 자동 감지된 모드를 고르거나 `폴더 선택`으로 모드 폴더를 지정합니다.
2. `프로젝트 만들기`를 누릅니다. 원문 언어가 여러 개면 기준 언어를 선택합니다.
3. 검수 작업실에서 `AI 번역`을 누르거나 번역문을 직접 입력합니다.
4. 원문, 기존 번역, AI 후보, Def Class, Node, 용어, 메모와 문제 목록을 확인합니다.
5. 항목을 `번역됨` 또는 `검토 완료`로 표시합니다.
6. `검토됨 적용` 또는 `번역됨까지 적용`을 누릅니다. `RMK에 적용`을 켜면 모드 대신 RMK 작업 클론에 기록합니다.

AI 번역 결과는 즉시 모드에 쓰지 않습니다. 먼저 `%LOCALAPPDATA%\RimWorldAiTranslator\reviews` 아래의 검수 프로젝트에 저장됩니다.

## API 키와 설정

- API 키는 메모리에만 있으며 `settings.json`, 로그, 진단 ZIP 또는 검수 파일에 저장하지 않습니다.
- 키는 설정 화면에 한 줄에 하나씩 입력합니다.
- 기본 제공자는 Cerebras이며 기본 모델은 `gemma-4-31b`입니다.
- `Temperature`는 표현의 무작위성을 조절합니다. 번역 일관성을 위해 기본값 `0.10`을 권장합니다.
- 추가 용어집은 설정의 `추가 용어집`에서 선택하며 원본+DLC 기본 용어집에 없는 항목만 우선 용어로 더합니다.
- 제공자 URL은 HTTPS만 허용하며, 개발용 로컬 테스트 주소만 HTTP loopback을 허용합니다.

## 로컬 데이터와 쓰기 경계

| 위치 | 용도 | 기본 동작 |
|---|---|---|
| `%LOCALAPPDATA%\RimWorldAiTranslator` | 프로젝트, 설정, 검수, 로그, 캐시 | 앱 소유 데이터 |
| Workshop/로컬 모드 | 원문과 기존 번역 참조 | 읽기 전용, 사용자가 적용을 확인한 경우에만 `Languages\Korean` 기록 |
| RMK 구독본 | 기존 번역과 XLSX 원문 이력 참조 | 항상 읽기 전용 |
| RMK Git 작업 클론 | 검수 결과 XML/XLSX 내보내기 | 사용자가 `RMK에 적용`을 선택한 경우에만 기록 |

상태 JSON과 번역 출력은 임시 파일에 기록한 뒤 검증·flush하고 교체합니다. 기존 파일을 바꿀 때는 `.bak`을 남기며 여러 파일 적용 중 실패하면 전체를 롤백합니다.

## 단축키

- `Ctrl+S`: 현재 편집 저장
- `Ctrl+F`: 검색으로 이동
- `Ctrl+Enter`: 검토 완료 후 다음 항목
- `Alt+Left` / `Alt+Right`: 이전 / 다음 항목
- `Ctrl+1` / `Ctrl+2` / `Ctrl+3`: 미번역 / 번역됨 / 검토됨
- `Ctrl+Shift+3`: 안전한 번역 전체 검토
- `Ctrl+Shift+P`: 명령 메뉴

## 소스 빌드

.NET SDK 8.0.422 이상이 필요합니다.

```powershell
dotnet build .\RimWorldAiTranslator.sln -c Release
dotnet run --project .\tests\RimWorldAiTranslator.Tests\RimWorldAiTranslator.Tests.csproj -c Release
dotnet run --project .\tests\RimWorldAiTranslator.Benchmarks\RimWorldAiTranslator.Benchmarks.csproj -c Release -- --rows 5000 --iterations 5
dotnet run --project .\src\RimWorldAiTranslator.App\RimWorldAiTranslator.App.csproj -c Release
```

자체 포함 릴리스 패키지는 개발용 보조 스크립트로 만듭니다.

```powershell
& "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File .\build-package.ps1
```

`build-package.ps1`은 C# 회귀 테스트, `dotnet publish`, 패키지 실행·종료, PowerShell 자식 프로세스 비호출 검사를 통과한 경우에만 `dist\RimWorldAiTranslator-v1.0.0.zip`을 만듭니다. 이 스크립트는 빌드 도구이며 배포 ZIP에는 포함되지 않습니다.

## 보안과 제한

- XML DTD와 외부 엔터티를 차단하고 입력 크기와 출력 경로를 제한합니다.
- API URL의 쿼리 자격 증명과 비HTTPS 원격 주소를 거부합니다.
- 진단 ZIP과 품질 보고서는 원문, 번역문, 번역 키, API 키, 절대 경로와 원시 로그를 포함하지 않습니다.
- Harmony/C#에 하드코딩된 런타임 문구는 일반 RimWorld 언어 XML만으로 번역할 수 없습니다.
- RimWorld 패치 XML은 게임 안의 최종 병합 결과를 확정할 수 없어 자동 번역하지 않습니다.
- 서명 인증서가 없어 SmartScreen 경고를 완전히 제거할 수 없습니다.

## 라이선스

[MIT License](LICENSE)
