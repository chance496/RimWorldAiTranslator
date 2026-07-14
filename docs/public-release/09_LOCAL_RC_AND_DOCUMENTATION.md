# 09 — 로컬 RC, 깨끗한 환경과 공개 문서 준비

이 Phase는 로컬 검증용 결과물만 만든다. GitHub 업로드·태그·Release는 금지다.

## 1. framework와 runtime

- 공개 시점에 지원되는 정식 .NET을 사용한다.
- preview SDK·runtime·package를 사용하지 않는다.
- 충분한 지원 기간이 남은 LTS를 우선한다.
- `global.json`으로 SDK를 고정한다.
- framework 변경은 기능·UI 동등성 후 별도 변경으로 하고 전체 회귀를 다시 실행한다.

사용자에게 별도 .NET 설치를 요구하지 않는 목표라면 self-contained `win-x64`를 우선 검토한다. 포함 runtime의 보안 업데이트 책임을 문서화한다.

single-file은 시작 속도, native/content 파일, Defender, 임시 추출, 진단과 crash dump가 검증될 때만 선택한다. 그렇지 않으면 소수 파일의 폴더 배포가 더 안전할 수 있다.

## 2. 버전 후보

- 기존 `1.0.0` 재사용 금지
- 기존 공개 릴리스 수정 금지
- 호환되는 내부 구현 전환이면 `1.1.0-rc.1` 같은 후보를 제안할 수 있음
- 데이터·사용 흐름 호환이 깨지면 major 후보를 검토
- 실제 버전은 보고서에서 제안만 하고 사용자 승인 전 확정·태그 생성 금지

## 3. clean local publish

- 새 임시 출력 폴더
- Release, Windows x64
- restore/build/test 통과한 정확한 commit/작업 트리
- product name·description·version·copyright·icon
- app manifest·DPI·requestedExecutionLevel
- 콘솔 창이 불필요하게 나타나지 않음
- PowerShell·Python·Node 등 개발 도구를 사용자 실행에 요구하지 않음

## 4. package allowlist

패키지에는 의도한 파일만 포함한다.

금지:

- source·tests·fixture·build cache·`.git`
- API key·token·사용자 data·log·diagnostics
- PowerShell·개발 스크립트
- 불필요한 PDB·temporary file
- 내부 benchmark·audit output

포함 검토:

- EXE와 필요한 DLL·runtime
- 설정 기본값 또는 schema
- README·LICENSE·THIRD_PARTY_NOTICES
- checksum
- 필요한 glossary·content와 라이선스

파일별 경로·크기·SHA-256 manifest를 만든다. ZIP entry에 `..`, 절대 경로, 중복 경로가 없는지 검사한다.

## 5. 깨끗한 환경 검증

우선순위:

1. Windows Sandbox
2. 깨끗한 VM
3. 새 Windows 사용자
4. 개발 SDK·저장소가 없는 별도 폴더와 제한된 환경

확인:

- ZIP 압축 해제
- EXE 직접 실행
- 첫 실행·프로젝트 fixture 열기·저장·종료
- 한글·공백 경로
- PowerShell child 0
- 종료 후 child 0
- 관리자 권한 불필요
- 설정·로그가 문서 위치에 생성

환경이 없으면 자동 PASS가 아니라 `BLOCKED: clean-machine manual test required`다.

## 6. Defender와 서명

- 최종 publish 폴더와 ZIP을 로컬 Windows Defender로 검사
- 엔진 버전·시각·결과 기록
- 온라인 스캐너에 자동 업로드하지 않음
- 탐지 시 원인 분석 전 공개 금지

코드 서명:

- 유효한 Authenticode 인증서가 있으면 안전하게 서명하고 timestamp 사용
- 인증서 private key를 repo·로그·artifact에 남기지 않음
- 인증서가 없으면 자체서명을 공인 서명처럼 표현하지 않음
- unsigned 배포와 SmartScreen 가능성을 README·release note 후보에 명시
- 서명 없음 자체는 자동 FAIL이 아니지만 사용자 승인 항목

## 7. 재현성

- 같은 source state에서 clean publish 2회
- manifest·해시 비교
- 차이가 있으면 timestamp·runtime 등 nondeterministic 원인 기록
- ZIP timestamp 정규화 가능성 검토
- 최종 ZIP SHA-256과 크기 기록

## 8. 필수 사용자 문서

### README

- 목적과 비공식 도구 고지
- 지원 OS·설치·첫 실행
- 프로젝트·번역·검토·DryRun·적용·RMK 흐름
- AI 제공자·API 키·원문 외부 전송
- 데이터·설정·로그·백업 위치
- 제거 방법·알려진 제한·문제 보고
- 서명 여부·SmartScreen·라이선스

### SECURITY.md

- 지원 버전
- 비공개 취약점 신고 방법
- 공개 이슈에 키·개인정보 업로드 금지
- 보안 업데이트 정책과 응답 기대

### THIRD_PARTY_NOTICES.md

- NuGet package
- icon·font·image·glossary·sample data
- 코드·도구·라이선스·출처

### 개인정보 안내

- 텔레메트리 여부
- API 키 저장·수명
- 전송되는 데이터와 제공자
- 로그·진단 정보
- 데이터 삭제 방법

## 9. 자산과 명칭

- RimWorld 게임 파일·아이콘·아트·사운드 무단 포함 금지
- 프로젝트가 Ludeon Studios 공식 제품이 아님을 명시
- 타사 상표 소유처럼 표현하지 않음
- glossary와 모든 자산의 출처·사용 권한 확인
- 법적 확신 없는 자산은 패키지에서 제거하거나 대체하고 `BLOCKED`로 보고

## Exit gate

- clean local publish·ZIP·manifest·checksum 생성
- package allowlist와 smoke 통과
- Defender 위협 0
- PowerShell child 0
- 지원 문서·라이선스 inventory 완성
- clean-machine 결과 PASS 또는 명확한 수동 blocker
- 공개 GitHub 변경 0
