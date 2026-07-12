RimWorld AI Translator v1.0.0
================================

설치
----
1. ZIP을 원하는 폴더에 완전히 압축 해제합니다.
2. RimWorldAiTranslator.exe를 실행합니다.

Windows 10/11 x64용 자체 포함 C# 프로그램입니다.
PowerShell, Python, Node.js, Git 또는 별도 .NET 설치가 필요하지 않습니다.

빠른 시작
---------
1. 프로젝트 화면에서 모드를 선택합니다.
2. 프로젝트 만들기를 누르고 필요하면 원문 언어를 고릅니다.
3. AI 번역을 누르거나 번역문을 직접 입력합니다.
4. 항목을 검토 완료로 표시합니다.
5. 검토됨 적용 또는 번역됨까지 적용을 눌러 Languages\Korean에 기록합니다.

API 키
------
- 설정 화면에 한 줄에 하나씩 입력합니다.
- 키는 메모리에만 있으며 디스크, 로그, 진단 파일에 저장하지 않습니다.
- 키가 없으면 Google 번역 후보를 사용합니다.

용어집
------
- 원본 RimWorld와 DLC 용어집은 기본 포함됩니다.
- 설정에서 TXT/TSV/CSV/JSON 추가 용어집을 선택할 수 있습니다.

데이터 위치
-----------
프로젝트와 검수 데이터: %LOCALAPPDATA%\RimWorldAiTranslator

프로젝트 삭제는 앱이 만든 검수 폴더만 정리합니다.
원본 모드와 모드 안의 Korean 폴더는 삭제하지 않습니다.

RMK
---
RMK 구독본은 읽기 전용 참고 자료입니다.
RMK에 적용을 선택한 경우에만 설정한 RMK Git 작업 클론에 XML/XLSX를 기록합니다.

주의
----
- AI 결과는 자동으로 게임 모드에 적용되지 않습니다. 검수 후 적용 버튼을 누르세요.
- 원문 변경, 토큰 손실, 잘못된 조사 형식과 내부 식별자 경고를 확인하세요.
- 서명되지 않은 배포본이므로 Microsoft SmartScreen 경고가 나타날 수 있습니다.

프로젝트: https://github.com/chance496/RimWorldAiTranslator
라이선스: MIT
