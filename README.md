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

GUI에서 API 키는 `API 키 입력` 칸에 한 줄에 하나씩 입력합니다. 여러 키를 넣으면 입력 순서를 기준으로 요청 수와 무료 티어 제한 상태에 맞춰 순환 사용합니다. 키 파일을 만들거나 따로 지정할 필요는 없고, 입력한 키 값은 로그에 남기지 않습니다.

추가 프롬프트는 오른쪽 입력칸에 적고, 모드별 수동 용어집이 필요할 때만 `추가 용어집 로드`로 TXT/TSV/CSV/JSON 파일을 선택합니다.

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

## 기존 번역과 비교만 하기

기존 번역을 덮어쓰지 않고 AI 후보와 비교하려면 `-ReviewOnly`를 사용합니다. 후보 XML과 비교 보고서는 `tools\RimWorldAiTranslator\reviews\...` 아래에 생성됩니다.

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

보고서에는 원문, 기존 번역, 후보 번역, 토큰 누락 여부, 비정상 개행 후보 여부, 한글 포함 여부, 안전 적용 가능 여부가 같이 들어갑니다.

## 주요 옵션

```powershell
-RequestsPerMinutePerKey 5          # 무료 티어 요청 제한
-InputTokensPerMinutePerKey 30000   # 무료 티어 입력 토큰 제한
-DailyTokenBudgetPerKey 1000000     # 실행 중 키별 일일 예산 가드
-MaxInputTokensPerBatch 5500        # 5 RPM에서 30k TPM을 넘기지 않기 위한 기본 배치 크기
-MaxCompletionTokens 32000          # 무료 티어 최대 출력
-BatchSize 80
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
