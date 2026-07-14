RimWorld AI Translator v1.0.1-rc.1 (Local Release Candidate)
================================

설치
----
1. ZIP을 원하는 폴더에 완전히 압축 해제합니다.
2. RimWorldAiTranslator.exe를 실행합니다.

Windows 10/11 x64용 자체 포함 C# 프로그램입니다.
PowerShell, Python, Node.js, Git 또는 별도 .NET 설치가 필요하지 않습니다.
이 패키지는 로컬 검증 후보이며 공개 Release가 아닙니다.
이 도구는 비공식 커뮤니티 프로젝트이며 Ludeon Studios가 개발·보증·지원하지 않습니다.
RimWorld와 관련 명칭은 각 권리자에게 속합니다.

공식 RimWorld Core/DLC localization에서 파생된 기본 용어집은 재배포 권리가 확인되지 않아 ZIP에서 제외됩니다.
따라서 기본 용어 제안과 glossary-bearing AI 요청은 Golden Master와 다르며 기능·요청 동등성 판정은 BLOCKED입니다.

빠른 시작
---------
1. 프로젝트 화면에서 모드를 선택합니다.
2. 프로젝트 만들기를 누르고 필요하면 원문 언어를 고릅니다.
3. AI 번역을 누르거나 번역문을 직접 입력합니다.
4. 항목을 검토 완료로 표시합니다.
5. 검토됨 적용 또는 번역됨까지 적용을 누르고 dry-run의 대상·제외·파일 수를 확인합니다.
6. 기본값 아니요인 최종 확인에서 대상을 다시 확인한 뒤에만 Languages\Korean에 기록합니다.

API 키
------
- 설정 화면에 한 줄에 하나씩 입력합니다.
- 키는 메모리에만 있으며 디스크, 로그, 진단 파일에 저장하지 않습니다.
- 키는 설정 입력 draft와 현재 프로세스, 번역 실행 복사본에 남습니다. 15초 숨김은 화면만 가리며 메모리를 지우지 않습니다. 키를 비워 저장하거나 앱을 끝내면 해당 문자열은 GC 대상이 됩니다.
- 키가 없으면 Google 번역 후보를 사용합니다.
- 제공자 URL은 HTTPS만 허용합니다. fragment는 금지하며 query는 제한된 `api-version=<version>`, `format=json`, `version=<version>`만 허용합니다. user-info, host, path, query, fragment의 credential 형태는 저장·실행 전에 거부됩니다.
- 이전 설정 또는 백업에서 credential 형태가 감지되면 `settings.json`과 `settings.json.bak`이 모두 안전해질 때까지 수정 필요 경고가 유지됩니다. 확실히 제거하려면 앱을 종료한 뒤 두 파일을 함께 삭제하고 설정을 다시 입력하세요.

개인정보와 외부 전송
--------------------
- 앱 텔레메트리와 자동 로그/진단 업로드는 없습니다. AI 번역을 시작할 때만 선택한 공급자로 전송합니다. Dry-run과 원문 분석은 전송하지 않습니다.
- OpenAI 호환 공급자는 HTTPS POST로 API 키, 모델 옵션, 번역 지침, 선택 용어집, 요청 ID·번역 키·종류·Def Class·필드·원문을 받습니다. 기존 번역, 메모, 프로젝트 이름과 절대 경로는 보내지 않습니다.
- 키가 없으면 Google에 원문 조각을 HTTPS GET `q=` query로 보냅니다. Google에는 용어집·번역 키·Def 문맥·키를 보내지 않지만, 실패한 같은 query를 기본 최대 4회 다시 보낼 수 있습니다.
- 재시도와 배치 분할은 공급자가 같은 원문을 여러 번 받고 비용이 늘게 할 수 있습니다.
- 번역 준비 화면에서 대상 host를 확인하세요. 사용자 지정 origin은 별도 Yes/No 경고를 표시하지만, 앱은 그 운영자의 개인정보·학습·보존·관할·비용 정책을 검증하지 않습니다.

로그와 진단
-----------
- 날짜별 로그: %LOCALAPPDATA%\RimWorldAiTranslator\logs\RimWorldAiTranslator-YYYYMMDD.log
- 로그에는 시각, 작업 상태·건수, 일부 프로젝트/모드 표시 이름, 제한된 오류 유형/HResult가 들어갈 수 있습니다. 키·인증 값과 Windows 절대 경로는 가리고 원문·요청/응답 body는 의도적으로 기록하지 않습니다.
- 14일 정리 등 자동 보존 기간은 구현되어 있지 않습니다. 로그는 직접 삭제할 때까지 남습니다.
- 진단 ZIP은 사용자가 선택한 로컬 경로에 집계 JSON 6개만 만들며 자동 업로드하지 않습니다. OS/.NET/문화권, 설정·공급자 범주, 프로젝트·검수·오류 수, 제품 파일 해시/버전은 포함합니다. 원문, 번역문, 번역 키, API 키, 원시 로그, 전체 URL/host, 프로젝트 이름, 메모와 절대 경로는 제외합니다. ZIP은 자동 삭제하지 않습니다.

데이터 삭제
-----------
- 설정: 앱 종료 후 %LOCALAPPDATA%\RimWorldAiTranslator\settings.json 및 settings.json.bak 삭제
- 검수: 프로젝트 삭제 기능으로 해당 앱 소유 검수 폴더를 정리하거나, 앱 종료 후 reviews 폴더 삭제
- 로그: 앱 종료 후 logs 폴더 또는 날짜별 로그 삭제
- 전체 초기화: 앱 종료 후 %LOCALAPPDATA%\RimWorldAiTranslator 전체 삭제

프로젝트 삭제 뒤에도 projects.json.bak과 성공 로그에 직전 프로젝트 정보가 남을 수 있습니다. 다른 위치에 저장한 진단 ZIP/보고서, 모드 적용 결과와 RMK 출력은 별도로 삭제해야 합니다.

용어집
------
- 공식 RimWorld Core/DLC localization에서 파생된 기본 용어집은 이 ZIP에 포함되지 않습니다.
- 재배포 권리가 확인되지 않았기 때문에 제외했으며, 이 상태는 Golden 기능·요청 parity BLOCKED입니다.
- 설정에서 사용자가 권리를 확인한 TXT/TSV/CSV/JSON 추가 용어집을 선택할 수 있습니다.
- sample-glossary.txt의 항목은 형식 설명용 합성 placeholder이며 공식 용어집 대체물이나 parity 증거가 아닙니다.

데이터 위치
-----------
프로젝트와 검수 데이터: %LOCALAPPDATA%\RimWorldAiTranslator

프로젝트 삭제는 앱이 만든 검수 폴더만 정리합니다.
원본 모드와 모드 안의 Korean 폴더는 삭제하지 않습니다.
프로그램 제거는 앱을 종료한 뒤 압축을 푼 폴더를 삭제합니다. %LOCALAPPDATA% 데이터와 다른 위치의 출력은 위 절차로 별도 삭제해야 합니다.

RMK
---
RMK 구독본은 읽기 전용 참고 자료입니다.
RMK에 적용을 선택한 경우에만 설정한 RMK Git 작업 클론에 XML/XLSX를 기록합니다.
RMK Builder는 sandbox가 아니며 현재 Windows 사용자 권한으로 파일과 네트워크에 접근할 수 있습니다. 앱은 선택한 Builder EXE의 canonical 경로·크기·SHA-256을 실행 직전에 다시 확인하지만, 인접 DLL/config와 작업 클론 전체 내용까지 고정하지는 않습니다. 출처와 전체 클론을 신뢰할 때만 실행하세요.

주의
----
- AI 결과는 자동으로 게임 모드에 적용되지 않습니다. 검수 후 적용 버튼을 누르세요.
- 원문 변경, 토큰 손실, 잘못된 조사 형식과 내부 식별자 경고를 확인하세요.
- Authenticode 서명이 없으므로 Microsoft SmartScreen 경고가 나타날 수 있습니다. 경고 유무는 안전성을 보증하지 않습니다.
- 이 self-contained 프로그램에는 .NET 8.0.28 runtime 코드가 포함됩니다. 시스템 .NET 업데이트는 포함 runtime을 갱신하지 않으므로 보안 업데이트에는 새 앱 패키지의 재빌드·재검증이 필요합니다.
- 공식 파생 기본 용어집을 제외한 로컬 RC이며 기능·요청 parity가 BLOCKED입니다. 공개 배포물로 취급하거나 재배포하지 마세요.

보안과 문서
-----------
- 취약점은 SECURITY.md의 비공개 절차로 신고하세요. 외부 private-reporting 링크의 가용성은 이 로컬 감사에서 확인하지 않았습니다.
- 공개 이슈에 API 키, 개인정보, 사용자 경로, 비공개 원문, 로그, 진단 번들이나 exploit 세부사항을 올리지 마세요.
- 개인정보 처리와 삭제: PRIVACY.md
- .NET runtime·자산 inventory와 라이선스: THIRD_PARTY_NOTICES.md
- 프로젝트 코드 라이선스: LICENSE

프로젝트: https://github.com/chance496/RimWorldAiTranslator
라이선스: MIT
