# RimWorld AI Translator

RimWorld 모드 하나를 로컬 프로젝트 하나로 관리하며 원문 추출, AI 초벌 번역, 직접 편집, 검수, 업데이트 추적, `Languages\Korean` 적용과 RMK 작업 클론 내보내기를 처리하는 Windows 데스크톱 도구입니다.

이 프로젝트는 비공식 커뮤니티 도구이며 Ludeon Studios가 개발·보증·지원하는 제품이 아닙니다. RimWorld와 관련 명칭은 각 권리자에게 속합니다.

**공개된 v1.0.0은 그대로 보존됩니다. 현재 소스의 로컬 후속 후보는 v1.0.1-rc.1이며, 메인 프로그램·번역 엔진·빌드·테스트·용어집·패키징 경로는 C#/.NET 8로 실행됩니다. 사용자 실행에는 PowerShell, Python, Node.js, Git 또는 별도 .NET 설치가 필요하지 않습니다.**

**v1.0.1-rc.1은 로컬 검증 후보이며 공개 Release가 아닙니다. 공식 RimWorld Core/DLC localization에서 파생된 기본 용어집은 재배포 권리가 확인되지 않아 RC ZIP에서 제외됩니다. 그 결과 기본 용어 제안과 glossary-bearing AI 요청이 Golden Master와 달라지므로 기능·요청 동등성과 전체 공개 품질 판정은 `BLOCKED`입니다.**

## 설치

1. 공개 v1.0.0은 [Releases](https://github.com/chance496/RimWorldAiTranslator/releases)의 기존 `RimWorldAiTranslator-v1.0.0.zip`을 사용합니다. 이 저장소에서 만든 v1.0.1-rc.1은 로컬 검증용이며 공개 배포물이 아닙니다.
2. ZIP을 원하는 폴더에 완전히 압축 해제합니다.
3. `RimWorldAiTranslator.exe`를 실행합니다.

로컬 후보는 Windows 10/11 x64용 자체 포함 실행 파일입니다. Authenticode 서명이 없으므로 Microsoft SmartScreen 경고가 나타날 수 있습니다. 경고 유무는 안전성을 보증하지 않습니다.

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
- 설정에서 사용자가 권리를 확인한 TXT/TSV/CSV/JSON 추가 용어집을 선택할 수 있습니다. 공식 파생 기본 용어집은 이 RC ZIP에 포함되지 않습니다.
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
6. `검토됨 적용` 또는 `번역됨까지 적용`을 누르고 dry-run 미리보기의 대상·제외·파일 수를 확인합니다. 실제 기록 확인의 기본값은 `아니요`입니다. `RMK에 적용`을 켜면 모드 대신 RMK 작업 클론에 기록합니다.

AI 번역 결과는 즉시 모드에 쓰지 않습니다. 먼저 `%LOCALAPPDATA%\RimWorldAiTranslator\reviews` 아래의 검수 프로젝트에 저장됩니다.

## API 키와 설정

- API 키는 메모리에만 있으며 `settings.json`, 로그, 진단 ZIP 또는 검수 파일에 저장하지 않습니다.
- 키는 설정 화면에 한 줄에 하나씩 입력합니다.
- 키 문자열은 설정 입력창과 공급자별 임시 draft, 번역 실행용 복사본에 남아 있다가 사용자가 지우거나 바꾸거나 앱 프로세스가 끝나면 GC 대상이 됩니다. 15초 뒤 화면을 숨기는 기능은 표시만 가리며 메모리를 즉시 지우거나 zeroize하지 않습니다.
- 기본 제공자는 Cerebras이며 기본 모델은 `gemma-4-31b`입니다.
- `Temperature`는 표현의 무작위성을 조절합니다. 번역 일관성을 위해 기본값 `0.10`을 권장합니다.
- 추가 용어집은 설정의 `추가 용어집`에서 명시적으로 선택합니다. RC ZIP에는 공식 파생 기본 용어집이 없으므로 선택한 로컬 용어집만 사용할 수 있습니다.
- 제공자 URL과 번역 실행은 HTTPS만 허용하며 HTTP loopback도 거부합니다. URL fragment는 허용하지 않고 query는 `api-version=<version>`, `format=json`, `version=<version>`의 제한된 형식만 허용합니다. user-info, host, path, query, fragment 어디에든 credential 이름·값·token 형태가 있으면 저장과 실행 전에 거부합니다.
- 이전 버전의 `settings.json` 또는 `settings.json.bak`에서 credential 형태가 발견되면 두 파일이 모두 안전해질 때까지 수정 필요 경고가 유지됩니다. 확실히 제거하려면 앱을 종료한 뒤 두 파일을 함께 삭제하고 설정을 다시 입력하세요.

## 개인정보와 외부 전송

전체 안내는 [PRIVACY.md](PRIVACY.md)를 확인하세요.

- 앱 자체 텔레메트리, 사용량 분석, 자동 로그 업로드, 자동 진단 업로드는 없습니다. 네트워크 전송은 사용자가 AI 번역을 시작했을 때 선택한 번역 공급자로만 발생합니다. `Dry-run`과 원문 분석만 수행하는 작업은 공급자 요청을 보내지 않습니다.
- OpenAI 호환 기본/사용자 지정 공급자에는 HTTPS POST로 API 키(`Authorization: Bearer`), 모델·생성 옵션, 고정 번역 지침, 선택된 기본/추가 용어집과 추가 지침, 각 항목의 요청 ID·번역 키·종류·Def Class·필드·원문이 전송됩니다. 프로젝트 이름, 절대 경로, 기존 번역, 메모와 검수 이력은 요청 payload에 넣지 않습니다.
- 키가 없으면 Google 번역 대체 경로가 각 원문을 최대 3,500자 조각으로 나누어 HTTPS GET의 `q=` query에 넣습니다. Google 요청에는 용어집, 번역 키, Def 문맥, 메모와 API 키를 넣지 않습니다. 실패한 조각은 기본 최대 4회까지 같은 query로 다시 전송될 수 있으므로 Google 또는 프록시의 URL 보존 정책을 확인해야 합니다.
- 일반 공급자도 실패 재시도와 배치 분할 때문에 같은 원문을 다시 받을 수 있으며 실제 요청 수와 비용이 늘 수 있습니다.
- 번역 준비 화면은 대상 host를 보여 줍니다. `Custom` 또는 기본 공급자와 origin이 다른 주소에는 원문·문맥·용어집·키 전송을 알리는 별도 Yes/No 확인이 표시됩니다. 앱은 임의의 사용자 지정 HTTPS host의 운영자, 개인정보·학습·보존·관할 정책, 모델 품질이나 요금을 검증하지 않으므로 전체 URL과 제3자 정책·비용을 직접 확인해야 합니다.

## 로그, 진단과 데이터 삭제

- 로그는 `%LOCALAPPDATA%\RimWorldAiTranslator\logs\RimWorldAiTranslator-YYYYMMDD.log`에 날짜별로 append됩니다. 시각, 수준, 작업 상태·건수, 일부 프로젝트/모드 표시 이름, 제한된 오류 유형/HResult가 들어갈 수 있습니다. 기록 전에 API 키·인증 값과 Windows 절대 경로를 가리고 제어 문자를 평탄화하며 긴 메시지를 자릅니다. 요청/응답 body와 원문을 의도적으로 기록하지 않습니다.
- **14일 정리나 다른 자동 로그 보존 기간은 아직 구현되어 있지 않습니다. 로그는 사용자가 삭제할 때까지 남습니다.**
- `진단 번들 저장`은 사용자가 고른 로컬 경로에만 ZIP을 만들며 업로드하지 않습니다. ZIP에는 OS/.NET/문화권, 설정·공급자 범주, 프로젝트·언어 수, 검수 상태/출처 수, 오류 범주 수, 제품 파일 이름·크기·해시·버전의 집계 JSON 6개가 들어갑니다. 원문, 번역문, 번역 키, API 키/인증 헤더, 원시 로그, 전체 공급자 URL/host, 프로젝트 이름, 메모와 절대 경로는 포함하지 않습니다. 저장한 ZIP과 교체 백업은 자동 삭제하지 않습니다.
- 설정을 지우려면 앱을 종료한 뒤 `%LOCALAPPDATA%\RimWorldAiTranslator\settings.json`과 `settings.json.bak`을 삭제합니다.
- 검수 데이터는 앱의 `프로젝트 삭제`로 해당 프로젝트의 소유 확인된 검수 폴더를 정리하거나, 앱 종료 뒤 `%LOCALAPPDATA%\RimWorldAiTranslator\reviews`를 삭제합니다. 프로젝트 삭제 뒤에도 직전 `projects.json.bak`과 성공 로그에 프로젝트 정보가 남을 수 있으므로 완전 삭제에는 전체 로컬 데이터 정리가 필요합니다.
- 로그는 앱 종료 뒤 `logs` 폴더 또는 개별 날짜 파일을 삭제합니다. 모든 앱 로컬 데이터를 초기화하려면 앱 종료 뒤 `%LOCALAPPDATA%\RimWorldAiTranslator` 전체를 삭제합니다. 다른 위치에 저장한 진단 ZIP/품질 보고서, 이미 모드에 적용한 번역과 RMK 작업 클론 출력은 별도로 삭제해야 합니다.
- 프로그램을 제거하려면 앱 종료 뒤 압축을 푼 로컬 RC 폴더를 삭제합니다. 앱 폴더 삭제만으로 `%LOCALAPPDATA%` 데이터나 다른 위치의 출력은 삭제되지 않습니다.

## 로컬 데이터와 쓰기 경계

| 위치 | 용도 | 기본 동작 |
|---|---|---|
| `%LOCALAPPDATA%\RimWorldAiTranslator` | 프로젝트, 설정, 검수, 로그, 캐시 | 앱 소유 데이터 |
| Workshop 구독본 | 원문과 기존 번역 참조 | 항상 읽기 전용 |
| 사용자가 지정한 로컬 모드 | 원문과 기존 번역 참조, 명시적 적용 대상 | 미리보기와 확인 뒤에만 `Languages\Korean` 기록 |
| RMK 구독본 | 기존 번역과 XLSX 원문 이력 참조 | 항상 읽기 전용 |
| RMK Git 작업 클론 | 검수 결과 XML/XLSX 내보내기 | 사용자가 `RMK에 적용`을 선택한 경우에만 기록 |

상태 JSON과 번역 출력은 임시 파일에 기록한 뒤 검증·flush하고 교체합니다. 기존 파일을 바꿀 때는 `.bak`을 남기며 여러 파일 적용 중 실패하면 전체를 롤백합니다.

RMK Builder는 sandbox가 아닙니다. 현재 Windows 사용자 권한으로 파일과 네트워크에 접근할 수 있습니다. 앱은 선택한 Builder EXE의 canonical 경로·크기·SHA-256을 실행 직전에 다시 확인하지만, 같은 클론의 인접 DLL/config와 작업 클론 전체 내용까지 고정하지는 않습니다. 따라서 출처와 전체 클론을 신뢰하는 경우에만 실행하세요.

## 단축키

- `Ctrl+S`: 현재 편집 저장
- `Ctrl+F`: 검색으로 이동
- `Ctrl+Enter`: 검토 완료 후 다음 항목
- `Alt+Left` / `Alt+Right`: 이전 / 다음 항목
- `Ctrl+1` / `Ctrl+2` / `Ctrl+3`: 미번역 / 번역됨 / 검토됨
- `Ctrl+Shift+3`: 안전한 번역 전체 검토
- `Ctrl+Shift+P`: 명령 메뉴

## 소스 빌드

저장소의 기준 SDK는 .NET SDK 8.0.422입니다. 로컬 RC 패키징 도구는 실제로 해석된 SDK도 정확히 8.0.422인지 확인하며, 다른 patch SDK를 동등한 패키징 증거로 간주하지 않습니다.

```text
dotnet restore RimWorldAiTranslator.sln --configfile NuGet.config
dotnet build RimWorldAiTranslator.sln -c Release --no-restore
dotnet run --project tests/RimWorldAiTranslator.Tests/RimWorldAiTranslator.Tests.csproj -c Release --no-build --no-restore
dotnet run --project tests/RimWorldAiTranslator.Benchmarks/RimWorldAiTranslator.Benchmarks.csproj -c Release --no-build --no-restore -- --rows 5000 --iterations 5
dotnet run --project src/RimWorldAiTranslator.App/RimWorldAiTranslator.App.csproj -c Release --no-build --no-restore
```

용어집 생성기는 입력 경로를 명시적으로 받아 읽기 전용으로 처리합니다. 출력과 `.bak`은 모든 입력 루트 밖에 지정해야 합니다.

생성된 용어집의 로컬 사용·수정·재배포 권리는 입력 자료의 권리와 약관에 따라 별도로 확인해야 합니다. 도구 실행이나 구조 self-test는 재배포 권리를 부여하거나 증명하지 않습니다.

```text
dotnet run --project tools/RimWorldAiTranslator.GlossaryTool -c Release --no-build --no-restore -- self-test
dotnet run --project tools/RimWorldAiTranslator.GlossaryTool -c Release --no-build --no-restore -- build --rimworld-data-root <RimWorld-Data> --output <output.json>
```

로컬 자체 포함 후보는 C# 패키지 도구로만 만듭니다.

```text
dotnet run --project tools/RimWorldAiTranslator.Tooling -c Release --no-build --no-restore -- verify-zero
dotnet run --project tools/RimWorldAiTranslator.Tooling -c Release --no-build --no-restore -- package
```

패키지 명령은 오프라인 복원, 경고를 오류로 처리한 Release 빌드, 회귀, `win-x64` 단일 EXE publish, 정확한 파일 허용목록, 격리 탐색 smoke와 정상 종료를 모두 통과한 경우에만 `dist\RimWorldAiTranslator-v1.0.1-rc.1-win-x64.zip`과 해시 manifest를 교체합니다.

## 보안과 제한

- XML DTD와 외부 엔터티를 차단하고 입력 크기와 출력 경로를 제한합니다.
- API URL은 HTTPS만 허용하고 fragment와 비허용 query를 거부하며, 모든 URI 구성요소의 자격 증명 형태를 차단합니다. 허용 query는 제한된 `api-version`, `format=json`, `version`뿐입니다.
- 진단 ZIP과 품질 보고서는 원문, 번역문, 번역 키, API 키, 절대 경로와 원시 로그를 포함하지 않습니다.
- Harmony/C#에 하드코딩된 런타임 문구는 일반 RimWorld 언어 XML만으로 번역할 수 없습니다.
- RimWorld 패치 XML은 게임 안의 최종 병합 결과를 확정할 수 없어 자동 번역하지 않습니다.
- 서명 인증서가 없어 SmartScreen 경고를 완전히 제거할 수 없습니다. 자체서명을 공인 서명처럼 표현하지 않습니다.
- self-contained EXE에는 .NET 8.0.28 runtime 코드가 포함됩니다. 시스템의 .NET을 업데이트해도 포함 runtime은 바뀌지 않으므로 보안 업데이트에는 새 앱 패키지의 재빌드·재검증이 필요합니다. 정확한 inventory와 고지는 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)에 있습니다.
- `glossary.generated.ko.json`은 3,359개 용어와 3,833개의 RimWorld 공식 Core/DLC 유래 관측을 포함하지만 재배포 권리 증거가 없습니다. RC ZIP에서 제외되며 기능·요청 parity는 `BLOCKED`입니다.
- 취약점은 [SECURITY.md](SECURITY.md)의 비공개 절차로 신고하세요. 공개 이슈에 API 키, 개인정보, 사용자 경로, 비공개 원문, 로그나 진단 번들을 올리지 마세요.

## 라이선스

- 프로젝트 코드: [MIT License](LICENSE)
- 타사 runtime·콘텐츠: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)
- 개인정보: [PRIVACY.md](PRIVACY.md)
- 보안 신고: [SECURITY.md](SECURITY.md)
