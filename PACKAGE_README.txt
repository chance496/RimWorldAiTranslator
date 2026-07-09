RimWorld AI Translator 패키지

실행:
1. RimWorldAiTranslator.exe 를 더블클릭합니다.
2. 모드 폴더 위치를 선택합니다.
3. API 키 입력 칸에 Cerebras API 키를 입력합니다. 여러 키는 Enter로 줄을 나눠 넣습니다. 키 파일은 만들 필요가 없습니다.
4. 필요하면 추가 프롬프트나 sample-glossary.txt를 로드합니다.
5. 처음에는 Dry run 또는 비교/검토 모드로 확인하는 것을 권장합니다.

필요 환경:
- Windows 10/11
- Windows 기본 PowerShell
- 인터넷 연결
- Cerebras API 키

추가 용어집 TXT 형식:
원문=번역
원문 => 번역
원문<Tab>번역<Tab>메모

기본 용어집:
- glossary.generated.ko.json
- 림월드 본편과 공식 DLC 기준 용어집입니다.
- RMK/모드 용어는 기본 포함하지 않습니다.

주의:
- Steam 워크샵 원본 모드 폴더에 직접 쓰면 Steam 업데이트로 덮어써질 수 있습니다.
- API 키는 화면 공유나 스크린샷에 노출되지 않게 주의하세요.
- DLL/C# 코드에 직접 박힌 inspect 패널/gizmo/상태 문구는 XML 번역만으로 바뀌지 않을 수 있습니다. 모드가 번역 키를 쓰지 않는 경우 원본 코드 수정이나 Harmony 패치가 필요합니다.