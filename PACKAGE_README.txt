RimWorld AI Translator 패키지

실행:
1. RimWorldAiTranslator.exe 를 더블클릭합니다.
2. 모드 폴더 위치를 선택합니다.
3. API 키 입력 칸에 Cerebras API 키를 입력합니다. 여러 키는 Enter로 줄을 나눠 넣습니다. 키 파일은 만들 필요가 없습니다.
4. 필요하면 추가 프롬프트나 sample-glossary.txt를 로드합니다.
5. 배치 크기는 40, 60, 80 중 고릅니다. 안정 기본값은 40입니다.
6. 처음에는 Dry run 또는 비교/검토 모드로 확인하는 것을 권장합니다.
7. 비교/검토 모드가 끝나면 검토 폴더가 열립니다. _TranslationAudit 안의 comparison.csv로 비교합니다.
8. 비교/검토 모드 결과가 괜찮으면 검토 결과 적용 버튼으로 safeToApply=true 후보만 적용할 수 있습니다.

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
- 검토 결과 적용은 비교 보고서의 안전 후보만 적용하며, 기존 번역 덮어쓰기 체크가 꺼져 있으면 기존 키는 유지합니다.
- DLL/C# 코드에 직접 박힌 inspect 패널/gizmo/상태 문구는 XML 번역만으로 바뀌지 않을 수 있습니다. 모드가 번역 키를 쓰지 않는 경우 원본 코드 수정이나 Harmony 패치가 필요합니다.
