# RimWorld AI Translator

RimWorld 모드를 프로젝트별로 불러와 AI 초벌 번역, 수동 번역, 검수, 업데이트 추적, 게임용 한국어 파일 적용과 RMK 로컬 작업까지 한 화면에서 처리하는 Windows용 도구입니다.

기본 AI 모델은 Cerebras Chat Completions API의 `gemma-4-31b`입니다. API 키가 없으면 Google 번역으로 초벌 후보를 만들 수 있습니다.

## 주요 기능

- 프로젝트 하나당 RimWorld 모드 하나를 연결해 작업 상태를 로컬에 보존합니다.
- Steam 라이브러리와 RimWorld 설치 위치를 검색해 Workshop 및 로컬 모드를 자동으로 찾습니다.
- 영어, 중국어, 일본어 등 원본 언어 폴더를 감지하고, 여러 언어가 있으면 프로젝트 생성 때 기준 원문을 선택합니다.
- `Keyed`, `DefInjected`, 번역 가능한 일부 `Defs` XML 필드를 수집합니다.
- Cerebras API 키를 여러 줄로 입력하면 키별 무료 사용량을 고려해 순서대로 순환 사용합니다.
- 기존 번역이 있으면 AI 번역 전에 전체 덮어쓰기, 미번역 부분만 번역하기 또는 취소를 선택할 수 있습니다.
- AI 후보, 기존 한국어 번역, 직접 입력한 번역을 나란히 비교할 수 있습니다.
- 미번역, 번역됨, 검토됨, 업데이트로 변경됨 상태와 텍스트·키·Def Class·Node 검색 필터를 제공합니다.
- 같은 원문을 직접 번역했을 때 프로젝트 전체의 동일 원문에 일괄 적용할지 확인합니다.
- 모드 업데이트 후에도 아직 게임 모드에 적용하지 않은 번역과 검수 상태를 이어받습니다.
- 같은 키의 원문이 바뀌면 기존 번역은 보존하되 `변경됨 + 미번역`으로 내려 다시 확인하게 합니다.
- 검토됨만 적용하거나 번역됨과 검토됨을 함께 `Languages\Korean`에 적용할 수 있습니다.
- RMK 구독본의 기존 번역을 자동으로 참고하고, 선택 시 검수 결과를 RMK `bus` 작업 클론에 키 기준으로 병합합니다.
- RimWorld 본편과 공식 DLC에서 생성한 한국어 용어집을 기본으로 사용합니다.

## 설치 및 실행

1. [Releases](https://github.com/chance496/RimWorldAiTranslator/releases)에서 최신 `RimWorldAiTranslator.zip`을 받습니다.
2. ZIP 파일을 원하는 폴더에 압축 해제합니다.
3. `RimWorldAiTranslator.exe`를 실행합니다.

Windows 10/11에서 동작하며 Python, Node.js, Git 같은 별도 개발 도구는 필요하지 않습니다. Windows 기본 PowerShell은 필요합니다.

직접 소스 폴더에서 실행하려면:

```powershell
.\Start-RimWorldAiTranslatorGui.cmd
```

## 기본 작업 흐름

1. 프로젝트 화면에서 자동 검색된 모드를 선택하거나 폴더를 직접 지정합니다. 원문 언어가 여러 개면 번역 기준 언어를 선택합니다.
2. 프로젝트를 열고 `원문 갱신`을 눌러 번역 가능한 문자열을 불러옵니다.
3. API 키를 입력한 뒤 `AI 번역`을 누르거나, 번역 칸에 처음부터 직접 입력합니다. 기존 번역이 있으면 덮어쓰기 또는 미번역 부분만 번역하기를 선택합니다.
4. 항목별로 원문, 번역 후보, 기존 번역, 용어, 메모, 문제 경고를 확인합니다.
5. 상태를 `번역됨` 또는 `검토 완료`로 지정합니다.
6. `RMK에 적용`을 끄면 원본 모드의 `Languages\Korean`에, 켜면 RMK 작업 클론에 `검토 적용` 또는 `전체 적용` 결과를 기록합니다.

AI 번역은 바로 게임 모드에 쓰지 않습니다. 먼저 로컬 검수 프로젝트에 후보를 저장하므로 적용 전에 내용을 수정할 수 있습니다.

## 단축키

- `F2`: 번역문 입력으로 이동
- `F3` / `Shift+F3`: 다음 / 이전 문자열
- `Alt+1` / `Alt+2` / `Alt+0`: AI 후보 사용 / 기존 번역 사용 / 편집 되돌리기
- `Ctrl+1` / `Ctrl+2` / `Ctrl+3`: 미번역 / 번역됨 / 검토 완료로 표시
- `Ctrl+Shift+Enter`: 검토 완료로 표시
- `Ctrl+Enter`: 검토 완료 후 다음 문자열
- `Ctrl+F` / `Ctrl+S`: 검색 / 저장
- `F5`: 원문 갱신
- `F9` / `Shift+F9`: AI 번역 시작 / 중지
- `F6` / `Shift+F6`: 작업 영역 순방향 / 역방향 이동
- `Esc`: 검색어와 상태 필터 초기화

## 번역 상태

- `미번역`: 번역이 없거나 원문 변경 후 다시 확인해야 하는 항목입니다.
- `번역됨`: AI 또는 사람이 번역했지만 최종 검토가 끝나지 않은 항목입니다.
- `검토됨`: 사람이 확인한 항목입니다.
- `변경됨`: 모드 업데이트로 같은 키의 원문 내용이 달라진 항목입니다. 적용 대상에서 제외되며 다시 번역하거나 검토해야 합니다.

`검토 적용`은 검토됨 항목만 기록합니다. `전체 적용`은 검토됨과 안전한 번역됨 항목을 함께 기록합니다.

## RMK 로컬 연동

[RimWorldKorea/RMK](https://github.com/RimWorldKorea/RMK)를 구독했거나 로컬 Git 클론을 가지고 있으면 다음 기능을 사용할 수 있습니다.

- Steam Workshop의 RMK 구독본은 기존 번역 참조에만 사용하며 절대 수정하지 않습니다.
- 설정의 `RMK 기존 번역 자동 사용`이 켜져 있으면 원문 갱신 때 RMK 번역을 기본 번역으로 불러옵니다.
- AI 번역을 누르면 기존 검수·원본 모드·RMK 번역을 감지해 `덮어씌우기`, `미번역 부분만 번역하기`, `취소` 중에서 고를 수 있습니다.
- `미번역 부분만 번역하기`는 모드에 아직 적용하지 않은 수동 검수 번역도 보존해 API로 다시 보내지 않습니다.
- 상단 `RMK에 적용`을 끄면 기존처럼 원본 모드의 `Languages\Korean`에 적용합니다.
- `RMK에 적용`을 켜면 같은 `검토 적용`·`전체 적용` 버튼이 RMK 작업 클론으로 내보냅니다.

RMK에 적용하려면 `bus` 브랜치의 Git 클론을 준비하고 설정에서 해당 루트를 지정해야 합니다. 프로그램은 기존 키를 RMK의 원래 XML 파일에서 수정하고 새 키만 대응 경로에 추가한 뒤 `LoadFoldersBuilder.exe`를 실행합니다. 원문이 변경됐거나 토큰 보존 검사에 실패한 항목은 제외합니다.

프로그램은 Git 커밋과 푸시를 자동으로 하지 않습니다. RMK 탭에서 대상 경로, 버전, 브랜치와 변경 파일을 확인한 뒤 RMK 규칙에 맞춰 직접 커밋하면 됩니다.

## 원문 갱신과 업데이트 추적

원문 갱신은 새 작업 기록을 만들되 이전 프로젝트의 번역문, 메모, 상태를 이어받습니다. 아직 `Languages\Korean`에 적용하지 않은 번역도 사라지지 않습니다.

승계 기준은 다음과 같습니다.

1. 같은 파일과 같은 키
2. 프로젝트 안에서 하나만 존재하는 같은 키

실행할 때마다 바뀔 수 있는 순번형 내부 ID는 업데이트 승계에 사용하지 않습니다. 새 키는 새 미번역 항목으로 추가되고, 삭제된 키는 새 원문 목록에서 제외됩니다.

같은 키의 원문 값이 달라지면 기존 번역문과 이전 원문은 기록에 남지만 상태는 `변경됨 + 미번역`이 됩니다. 번역문을 수정하거나 상태를 다시 확정하면 변경 표시가 해제됩니다.

## 동일 원문 일괄 번역

사람이 번역문을 수정한 뒤 저장, 상태 변경, 다른 항목 이동, 프로젝트 전환 등을 하면 같은 원문이 더 있는지 확인합니다.

- `예`: 프로젝트 전체의 동일 원문 번역을 같은 값으로 통일합니다.
- `아니요`: 현재 항목만 변경합니다.

줄바꿈 형식만 통일한 뒤 원문이 완전히 같은 경우에만 묶습니다. 메모는 복사하지 않습니다. 이미 같은 번역으로 검토된 항목은 유지하며, 다른 번역이 바뀐 항목은 안전하게 `번역됨` 상태로 돌아갑니다.

## Cerebras API

무료 사용량 기준 기본값:

- 모델: `gemma-4-31b`
- 엔드포인트: `https://api.cerebras.ai/v1/chat/completions`
- 요청: 키당 분당 5회
- 입력: 키당 분당 30,000토큰
- 일일 토큰: 키당 1,000,000토큰
- 최대 출력: 32,000토큰
- 배치 크기: 40개

API 키는 한 줄에 하나씩 입력합니다. 여러 키를 넣으면 입력 순서대로 순환하며, 각 키의 요청 및 토큰 제한을 따로 계산합니다.

GUI에 입력한 API 키는 설정 파일, 프로젝트 파일, 명령행, 로그에 저장하지 않습니다. 프로그램을 다시 실행하면 다시 입력해야 합니다. API 키가 비어 있으면 Google 번역 후보를 생성합니다.

번역 원문은 선택한 공급자의 외부 서버로 전송됩니다. 비공개 문자열을 외부 서비스에 보내면 안 되는 환경에서는 네트워크 번역을 사용하지 마십시오.

## 용어집

`glossary.generated.ko.json`은 RimWorld 본편과 공식 DLC의 영어·한국어 LanguageData를 기준으로 생성한 기본 용어집입니다.

- Core
- Royalty
- Ideology
- Biotech
- Anomaly
- Odyssey

모드별 추가 용어집은 `sample-glossary.txt` 형식을 참고해 TXT, TSV, CSV 또는 JSON으로 만들 수 있습니다. 같은 원문이 있으면 공식 생성 용어집을 우선합니다.

추가 용어집 예시:

```text
source term=한국어 번역
another term => 다른 번역
third term<Tab>세 번째 번역<Tab>메모
```

## 번역 범위와 한계

지원하는 주요 범위:

- `Languages\<원본 언어>\Keyed`
- `Languages\<원본 언어>\DefInjected`
- LanguageData가 없는 모드의 일부 `Defs` XML 텍스트 필드
- 선택 시 `Patches` 폴더

`defName`, XML 키, 클래스명, 텍스처 경로처럼 게임 동작에 쓰이는 식별자는 번역하지 않습니다. `{0}`, `{PAWN_nameDef}`, `$variable`, `<color=...>` 같은 토큰도 보존 여부를 검사합니다.

DLL/C# 코드가 화면 문자열을 직접 반환하는 경우에는 XML 번역만으로 바뀌지 않습니다. 이 경우 원본 모드 수정이나 별도 Harmony 패치가 필요할 수 있습니다.

## 로컬 데이터 위치

프로젝트와 검수 데이터는 실행 파일 폴더가 아니라 다음 위치에 저장됩니다.

```text
%LOCALAPPDATA%\RimWorldAiTranslator
```

주요 파일:

- `projects.json`: 프로젝트와 연결된 모드 경로, 선택한 원문 언어, 작업 기록
- `reviews\<모드ID-시간>\review-decisions.json`: 번역문, 상태, 메모, 원문 해시
- `settings.json`: 테마, 화면 설정과 RMK 작업 클론 경로
- `mod-catalog.json`: 자동 검색한 모드 목록 캐시

API 키는 이 폴더에 저장하지 않습니다. 프로젝트를 백업하려면 `RimWorldAiTranslator` 폴더 전체를 복사하면 됩니다.

GUI에서 입력한 API 키는 번역 프로세스의 환경 변수로만 전달하며 명령행, 인수 파일, 디버그 로그에 기록하지 않습니다. 번역 프로세스 실행도 `cmd.exe` 셸을 거치지 않고 허용된 매개변수만 전달하는 로컬 실행 래퍼를 사용합니다.

## CLI 사용

자동화나 진단이 필요할 때 PowerShell 스크립트를 직접 실행할 수 있습니다.

```powershell
powershell -ExecutionPolicy Bypass -File ".\Invoke-RimWorldAiTranslation.ps1" `
  -ModRoot "E:\SteamLibrary\steamapps\workshop\content\294100\모드ID" `
  -ApiKey "csk-..." `
  -ReviewOnly
```

검수 결과 적용:

```powershell
powershell -ExecutionPolicy Bypass -File ".\Apply-RimWorldAiReviewResults.ps1" `
  -ModRoot "E:\SteamLibrary\steamapps\workshop\content\294100\모드ID" `
  -ReviewRoot ".\reviews\모드ID-날짜시간" `
  -Overwrite `
  -ApplyStatus ApprovedOnly
```

RMK 항목으로 검수 결과 병합:

```powershell
powershell -ExecutionPolicy Bypass -File ".\Export-RimWorldAiReviewToRmk.ps1" `
  -RmkEntryRoot "E:\RimWorld\Mods\RMK\Data\제작자\모드명 - 모드ID\1.6" `
  -ReviewRoot ".\reviews\모드ID-날짜시간" `
  -Overwrite `
  -ApplyStatus ApprovedOnly
```

## 패키지 빌드

```powershell
powershell -ExecutionPolicy Bypass -File ".\build-package.ps1"
```

결과물:

- `dist\RimWorldAiTranslator`
- `dist\RimWorldAiTranslator.zip`

## 라이선스

[MIT License](LICENSE)
