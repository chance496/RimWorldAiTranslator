# RimWorld AI Translator

Cerebras `gemma-4-31b` free tier 기준의 RimWorld 모드 번역 보조 도구입니다.

무료 티어 기본 설계값:

- `5` requests/min/key
- `30,000` input tokens/min/key
- `1,000,000` total tokens/day/key
- `32,000` max output tokens
- `gemma-4-31b`
- `https://api.cerebras.ai/v1/chat/completions`

## 빠른 실행

릴리즈 패키지를 받았다면 압축을 풀고 폴더 안의 `RimWorldAiTranslator.exe`를 실행합니다.

소스 체크아웃에서 바로 실행하려면:

```powershell
.\Start-RimWorldAiTranslatorGui.cmd
```

GUI에서 Cerebras API 키는 선택 사항입니다. 키를 넣으면 `gemma-4-31b`를 사용하고, 비워두면 Google Translate로 후보 번역을 만듭니다. 여러 키를 넣으면 입력 순서를 기준으로 요청 수와 무료 티어 제한 상태에 맞춰 순환 사용합니다. 키 파일을 만들거나 따로 지정할 필요는 없고, 입력한 키 값은 로그에 남기지 않습니다.

GUI에 입력한 API 키는 설정 파일이나 명령줄에 저장하지 않고 번역 자식 프로세스의 환경변수로만 전달합니다. CLI에서 `-ApiKey`를 직접 사용하면 운영체제의 프로세스 명령줄에 노출될 수 있으므로 민감한 키는 환경변수를 사용하세요. Cerebras 번역은 원문을 Cerebras API로, Google fallback은 원문을 Google Translate 엔드포인트로 전송하므로 비공개 문자열을 외부 서비스에 보내면 안 되는 환경에서는 네트워크 번역을 실행하지 마세요. 현재 배포 EXE와 PowerShell 스크립트는 상용 코드 서명 인증서로 서명되지 않았습니다.

GUI는 모드 화면으로 시작합니다. 기존 모드 작업을 카드로 열 수 있고, Steam 라이브러리와 RimWorld 설치 폴더에서 감지한 모드를 바로 불러올 수 있습니다. 감지 결과는 로컬에 캐시되며 모드 루트의 추가·삭제가 없으면 다음 실행에서 즉시 재사용합니다. 못 찾은 모드는 `폴더 선택`으로 직접 지정하고, `새로고침`으로 언제든 강제 재검색할 수 있습니다. `활동` 탭에는 번역 실행, 적용, 검수 변경 기록이 모이고, `설정` 탭에서 API 키를 한 줄에 하나씩 넣습니다.

모드별 수동 용어집이나 추가 프롬프트가 필요하면 CLI 옵션 `-UseCuratedGlossary`, `-CuratedGlossaryPath`, `-ExtraPrompt`, `-ExtraPromptFile`을 사용합니다. 추가 프롬프트와 수동 용어집은 Cerebras AI 번역에 적용되며, Google Translate fallback에는 적용되지 않습니다.

모드를 불러오면 번역칸은 `AI 후보 → 기존 한국어 번역 → 빈칸` 순서로 기본값을 채웁니다. 후보가 없어도 기존 번역을 바로 다듬을 수 있고, 둘 다 없는 항목은 사람이 처음부터 직접 입력할 수 있습니다. `번역 시작`은 아직 후보가 없는 항목에 초벌 번역을 만듭니다. GUI 실행 결과는 항상 검수 폴더에 먼저 생성됩니다. `검토됨 적용`은 사람이 확인한 항목만 반영하고, `번역됨 적용`은 AI 후보 중 안전한 번역됨 항목까지 실제 `Languages\Korean`에 반영합니다.

배치 크기는 안정성을 위해 `40`으로 고정됩니다.

CLI 직접 실행은 자동화나 디버깅용입니다. 이 경우에도 키 파일 대신 `-ApiKey` 값으로 직접 넘깁니다.

```powershell
powershell -ExecutionPolicy Bypass -File ".\Invoke-RimWorldAiTranslation.ps1" `
  -ModRoot "E:\SteamLibrary\steamapps\workshop\content\294100\모드ID" `
  -ApiKey "csk-..."
```

CLI에서 여러 계정/키를 쓰려면 `-ApiKey "csk-첫번째","csk-두번째"`처럼 배열로 넘기거나 `RIMWORLD_TRANSLATOR_API_KEYS` 환경변수에 줄바꿈으로 넣으면 됩니다.

```powershell
$env:RIMWORLD_TRANSLATOR_API_KEYS = @"
csk-첫번째키
csk-두번째키
"@
```

## 출력 위치

기본 출력은 입력한 모드 폴더 안의 `Languages\Korean`입니다.

- 원본 언어 폴더는 자동 감지합니다. 우선순위는 `English`, `ChineseSimplified`, `ChineseTraditional`, `Japanese`, 그 외 비한국어 Language 폴더입니다.
- 감지된 원본 `Languages\<언어>\Keyed` / `DefInjected`가 있으면 같은 구조로 번역합니다.
- 영어 LanguageData가 없어도 `Defs`에서 `label`, `description`, `jobString`, `reportString`, `gizmoLabel`, `letterText` 같은 항목을 뽑아 `DefInjected\<DefType>\CodexAI.xml`로 만듭니다.
- 실행 감사 파일은 모드 폴더의 `_TranslationAudit\cerebras-*.json`에 남깁니다.

이미 `Korean (한국어)` 폴더를 쓰는 모드라면:

```powershell
-LanguageFolderName "Korean (한국어)"
```

## 번역 범위와 한계

이 도구는 림월드의 일반적인 LanguageData 구조를 우선 처리합니다.

- `Languages\<언어>\Keyed`와 `DefInjected` 원문이 있으면 같은 구조로 한국어 파일을 만듭니다.
- 원본 LanguageData가 없어도 `Defs` 안의 `label`, `description`, `jobString`, `reportString`, `gizmoLabel`, `letterText` 같은 XML 필드는 추출해 번역합니다.
- DLL/C# 코드가 `GetInspectString()`, gizmo, 상태 표시문에서 영어 문장을 직접 반환하는 경우에는 단순 XML 키 생성만으로 번역할 수 없습니다. 예를 들어 선택창 왼쪽 아래 inspect 패널에 뜨는 `Tactical marker: searching` 같은 런타임 문자열이 여기에 해당할 수 있습니다.
- 모드 코드가 `"SomeKey".Translate()`처럼 번역 키를 조회하도록 만들어져 있다면 `Keyed` XML 추가로 해결할 수 있지만, 코드에 원문이 하드코딩되어 있으면 원본 DLL 수정, 모드 제작자의 번역 키 추가, 또는 별도 Harmony 패치가 필요합니다.

따라서 자동 번역 후에는 `-DryRun`, `-ReviewOnly`, 게임 안 확인을 같이 사용하는 것을 권장합니다.

## 공식 용어집

`glossary.generated.ko.json`은 림월드 본편과 공식 DLC의 한국어 tar에서 생성한 용어집입니다. RMK/워크샵 모드 용어는 기본적으로 포함하지 않습니다.

다시 생성하려면:

```powershell
powershell -ExecutionPolicy Bypass -File ".\Build-RimWorldGlossary.ps1" `
  -RimWorldDataRoot "E:\SteamLibrary\steamapps\common\RimWorld\Data" `
  -OutputPath ".\glossary.generated.ko.json"
```

현재 생성 기준:

- Core, Royalty, Ideology, Biotech, Anomaly, Odyssey
- 공식 원문/한국어 관측치 `3,833`개
- 최종 용어 `3,359`개
- 충돌 후보 `glossary.generated.ko.conflicts.csv`

번역기는 기본적으로 `glossary.generated.ko.json`만 읽습니다. 모드별 수동 보정 용어는 `sample-glossary.txt` 형식을 참고해서 별도 TXT/TSV/CSV/JSON 파일로 만들고, `-UseCuratedGlossary -CuratedGlossaryPath <파일>`을 켰을 때만 함께 읽습니다. 같은 원문 용어가 있으면 공식 생성 용어가 수동 보정보다 우선합니다. 무료 API 입력 토큰을 아끼기 위해 생성 용어집 전체를 매번 보내지 않고, 현재 배치 원문에 등장한 용어만 최대 `140`개까지 골라 보냅니다.

## 검수 후 적용하기

GUI는 기본적으로 기존 번역을 바로 덮어쓰지 않고 후보 XML과 비교 보고서를 `tools\RimWorldAiTranslator\reviews\...` 아래에 생성합니다. CLI에서 같은 흐름을 쓰려면 `-ReviewOnly`를 사용합니다.

밀리라 제국 원본과 RMK 기존 번역 비교 예시:

```powershell
powershell -ExecutionPolicy Bypass -File ".\Invoke-RimWorldAiTranslation.ps1" `
  -ModRoot "E:\SteamLibrary\steamapps\workshop\content\294100\3588393755\1.6" `
  -ExistingLanguageRoot "E:\SteamLibrary\steamapps\workshop\content\294100\3079466972\Data\Ancot\Milira\Milira Faction Milira Imperium - 3588393755\1.6\Languages\Korean (한국어)" `
  -LanguageFolderName "Korean (한국어)" `
  -ReviewOnly `
  -ApiKey "csk-..." `
  -Limit 24
```

비교 보고서:

- `_TranslationAudit\*-comparison.json`
- `_TranslationAudit\*-comparison.csv`

보고서에는 원문, 기존 번역, 후보 번역, 토큰 누락 여부, 비정상 개행 후보 여부, 한글 포함 여부, 안전 적용 가능 여부가 같이 들어갑니다. GUI에서는 번역이 끝나면 항목별 검수 화면이 자동으로 열리고, 모드 화면에서 같은 작업을 다시 열 수 있습니다. 실제 비교표는 `_TranslationAudit\*-comparison.csv`입니다.

로컬 검수 화면에서는 파일별 목록, 문자열 목록, 상태 필터, 검색, 원문/기존 번역/번역 후보, 수정 번역, 기록, 관련 용어, 메모, 문제 경고를 한 화면에서 다룹니다. 목록 위의 `전체`, `미번역`, `번역됨`, `검토됨`, `변경됨` 버튼으로 상태별 문자열을 바로 걸러낼 수 있고, 옆 드롭다운에서는 주의·후보 있음·기존 있음 조건까지 선택할 수 있습니다. 모드 업데이트로 같은 키의 원문이 바뀐 항목은 `업데이트로 변경됨` 표식과 전용 필터로 따로 확인할 수 있습니다. `Ctrl+Enter`로 검토 후 다음 항목으로 이동할 수 있습니다. 편집 내용은 기본적으로 잠시 뒤 자동 저장되며, 설정에서 테마, 본문 글자 크기, 고대비, 자동 저장 여부를 바꿀 수 있습니다. 검수 상태는 검토 폴더의 `review-decisions.json`에 저장됩니다.

GUI의 `검토 적용` 또는 `전체 적용` 버튼을 누르면 API를 다시 호출하지 않고 검토 결과를 원래 모드의 `Languages\Korean`에 적용합니다. `검토 적용`은 `검토됨` 상태만 적용합니다. `전체 적용`은 `검토됨`과 `번역됨` 상태를 함께 적용하되, 검토되지 않은 `번역됨` 항목은 `safeToApply=true`인 후보만 적용합니다. 업데이트로 같은 키의 원문이 바뀐 항목은 이전 원문을 기록에 보존하고 `미번역`으로 내려가며, `업데이트로 변경됨` 표식이 붙습니다. 다시 번역하거나 검토 상태로 바꾸기 전에는 두 적용 방식 모두에서 제외됩니다.

CLI에서 검토 결과를 적용하려면:

```powershell
powershell -ExecutionPolicy Bypass -File ".\Apply-RimWorldAiReviewResults.ps1" `
  -ModRoot "E:\SteamLibrary\steamapps\workshop\content\294100\모드ID" `
  -ReviewRoot ".\reviews\모드ID-20260709-120000"
```

번역됨 상태까지 CLI로 적용하려면:

```powershell
powershell -ExecutionPolicy Bypass -File ".\Apply-RimWorldAiReviewResults.ps1" `
  -ModRoot "E:\SteamLibrary\steamapps\workshop\content\294100\모드ID" `
  -ReviewRoot ".\reviews\모드ID-20260709-120000" `
  -ApplyStatus TranslatedAndApproved
```

## 주요 옵션

```powershell
-RequestsPerMinutePerKey 5          # 무료 티어 요청 제한
-InputTokensPerMinutePerKey 30000   # 무료 티어 입력 토큰 제한
-DailyTokenBudgetPerKey 1000000     # 실행 중 키별 일일 예산 가드
-MaxInputTokensPerBatch 5500        # 5 RPM에서 30k TPM을 넘기지 않기 위한 기본 배치 크기
-MaxCompletionTokens 32000          # 무료 티어 최대 출력
-BatchSize 40
-TranslationProvider Auto           # Auto: 키가 있으면 Cerebras, 없으면 Google
-GoogleTranslateUrl
-Overwrite
-DryRun
-MockTranslations
-IncludePatches
-NoStructuredOutputs
-ReviewOnly
-ExistingLanguageRoot
-SourceLanguageFolder Auto
-GeneratedGlossaryPath
-UseCuratedGlossary
-CuratedGlossaryPath
-ExtraPrompt
-ExtraPromptFile
-MaxGeneratedGlossaryTermsPerBatch 140
```

검토 결과 적용:

```powershell
.\Apply-RimWorldAiReviewResults.ps1 -ModRoot <모드폴더> -ReviewRoot <검토결과폴더> -Overwrite -ApplyStatus ApprovedOnly
```

## 패키지 빌드

Windows에서 릴리즈용 폴더와 ZIP을 다시 만들려면:

```powershell
powershell -ExecutionPolicy Bypass -File ".\build-package.ps1"
```

결과물은 `dist\RimWorldAiTranslator`와 `dist\RimWorldAiTranslator.zip`에 생성됩니다.

## 주의점

- `defName`, XML 키, 클래스명, 텍스처 경로는 번역하지 않습니다.
- `{0}`, `{PAWN_nameDef}`, `[pawn_nameDef]`, `$variable`, `<color=...>` 같은 토큰은 보존하도록 검사합니다.
- Steam 워크샵 폴더에 직접 쓰면 Steam 업데이트 때 덮어써질 수 있습니다.
- 대형 모드는 먼저 `-DryRun -Limit 50`으로 추출 상태를 확인하는 편이 좋습니다.

수동 보정 용어집은 `sample-glossary.txt`를 복사해서 모드별로 고치면 됩니다. 기본 실행에는 들어가지 않으므로, 모드별 고정 번역이 필요할 때만 `-UseCuratedGlossary`를 같이 사용하세요.
