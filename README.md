# RimWorld AI Translator

## 프로그램 소개

RimWorld 모드의 번역할 문장을 찾고, 번역·검토·적용을 한곳에서 처리하는 Windows 프로그램입니다.

## 주요 기능

- Workshop 및 로컬 모드 검색
- 원문 분석과 변경 내용 확인
- 기본 RimWorld 용어집과 사용자 용어집 지원
- AI 번역과 Google 번역 대체 사용
- 번역 직접 편집과 검토 상태 관리
- 검토한 번역을 `Languages\Korean`에 적용
- RMK 작업 폴더로 XML 및 XLSX 내보내기
- 기존 번역, 메모, 검토 상태 보존

## 다운로드

[GitHub Releases](https://github.com/chance496/RimWorldAiTranslator/releases/latest)에서 `RimWorldAiTranslator-v1.1.0-win-x64.zip`을 받으세요.

## 실행 방법

1. ZIP 파일의 압축을 완전히 풉니다.
2. `RimWorldAiTranslator.exe`를 실행합니다.

Windows 10/11 64비트를 지원합니다. 별도의 .NET 설치는 필요하지 않습니다.

## 간단한 사용법

1. `프로젝트`에서 모드 폴더를 선택하고 프로젝트를 만듭니다.
2. 원문 분석이 끝나면 `설정`에서 번역 서비스와 모델을 선택합니다.
3. API 키를 입력하거나 Google 번역 대체 기능을 사용합니다.
4. `AI 번역`을 누르고 완료된 문장을 검토합니다.
5. `검토 적용` 또는 `전체 적용`으로 결과를 저장합니다.

Workshop 원본과 RMK 구독본은 읽기 전용입니다. 번역은 사용자가 선택한 로컬 출력 폴더에만 저장됩니다.

## 알려진 문제

- 프로그램에 디지털 서명이 없어 Windows SmartScreen 경고가 표시될 수 있습니다.
- 무료 API 모델은 호출 제한이나 일시적인 공급자 장애로 번역이 늦어질 수 있습니다.
- 사용 가능한 모델은 공급자 정책에 따라 바뀔 수 있습니다.
- API 키는 저장되지 않으므로 프로그램을 다시 실행하면 다시 입력해야 합니다.
