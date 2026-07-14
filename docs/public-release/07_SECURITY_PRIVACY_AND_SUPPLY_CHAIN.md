# 07 — 보안, 개인정보와 공급망 감사

기존 PowerShell판의 보안 통과 결과를 C#판에 승계하지 않는다. 최종 소스와 로컬 publish 결과를 새로 검사한다.

## 1. 위협 모델

`SECURITY_MODEL.md` 또는 동등한 문서에 기록한다.

- 보호 대상: 사용자 프로젝트·번역·API 키·원문·RMK 데이터
- 신뢰 경계: UI, 파일 시스템, 외부 API, ZIP/XLSX, 로그, child process
- 공격자와 오용 시나리오
- 입력 표면·쓰기 표면·네트워크 표면
- 위험도·대응·잔여 위험

## 2. 비밀정보

검사 위치:

- 소스·Git 추적 파일·diff
- 설정·프로젝트·로그·예외·진단
- 환경 변수 출력
- 테스트 결과·fixture·스크린샷
- publish 폴더·ZIP·PDB·manifest

규칙:

- API 키를 디스크 평문에 저장하지 않는다.
- 인증 헤더와 key를 구조화 로그에서 마스킹한다.
- 예외 객체나 HTTP request 전체를 그대로 로그하지 않는다.
- 진단 번들은 포함 항목을 사용자에게 보여주고 redaction한다.
- 실제 비밀정보를 발견하면 값을 다시 출력하지 않는다.

## 3. 경로와 파일 쓰기

필수 공격 입력:

- `..`, 절대 경로, 다른 drive, UNC
- 공백·한글·따옴표·괄호·wildcard
- reserved name·긴 경로
- symbolic link·junction·reparse point
- 읽기 전용·권한 거부·파일 잠금
- path race·교체 공격
- 디스크 공간 부족

확인:

- canonical path가 허용 root 안인지 쓰기 직전에 재검증
- Workshop·RMK 구독본은 읽기 전용
- 사용자가 명시한 대상만 적용
- 임시 파일과 백업도 허용 root 안에 위치
- 임의 파일 덮어쓰기 불가
- DryRun·대상 수·확인·실패 요약 제공

## 4. XML

- DTD·외부 엔터티 비활성화
- XXE·entity expansion 방어
- 파일 크기·깊이·노드 수·처리 시간 제한
- malformed·잘못된 encoding 오류 처리
- 과도한 자원 사용 방지
- 허용된 field만 추출

## 5. XLSX와 ZIP

- ZIP Slip: `../`, 절대 경로, drive path 금지
- 압축 폭탄과 비정상 크기 제한
- entry 수·압축 해제 크기 제한
- 손상 package part 오류 처리
- 외부 relationship·link 처리 검토
- 기존 package part 보존
- package 내 실행 파일·스크립트 자동 실행 금지

## 6. HTTP와 외부 API

- HTTPS 기본
- endpoint allowlist 또는 엄격한 validation
- redirect 정책과 host 변경 검토
- bounded timeout·retry·response size
- 429·5xx backoff와 동시성 제한
- 인증 헤더 redaction
- TLS·proxy 오류 처리
- 사용자 취소와 앱 종료 연동
- 전체 사용자 원문을 불필요하게 로그하거나 재전송하지 않음

커스텀 endpoint를 지원한다면 위험 고지와 명시적 사용자 입력이 필요하다.

## 7. 외부 프로세스

- 불필요한 `Process.Start` 제거
- `UseShellExecute`와 shell 실행 최소화
- 실행 파일 allowlist와 절대 경로 확인
- 인자를 문자열 연결하지 않고 안전한 argument API 사용
- 사용자 입력이 명령·실행 파일 경로가 되지 않게 함
- timeout·exit code·stdout/stderr 크기·취소 처리
- 종료 시 child process 정리
- 관리자 권한 불필요

## 8. 직렬화와 로그 인젝션

- 타입 정보 기반 unsafe deserialization 금지
- 알 수 없는 필드 정책 명시
- 크기 제한 없는 JSON/XML 읽기 금지
- 사용자 입력의 개행·제어문자가 로그 구조를 위조하지 않게 함
- 오류 화면에서 내부 path·stack trace를 과도하게 노출하지 않음

## 9. NuGet과 SDK

현재 SDK가 지원하는 공식 `dotnet` 명령으로 직접·전이 의존성의 다음을 기록한다.

- 패키지 이름·버전·라이선스
- 알려진 취약점과 심각도
- deprecated·지원 종료 여부
- 불필요한 package

정책:

- Critical: 0
- High: 0
- Medium: 우선 수정, 불가하면 영향·완화·사용자 승인
- Low: 문서화

새 package는 기존 BCL로 해결 가능한지 먼저 검토한다.

## 10. 정적 분석

- compiler·nullable·.NET analyzers
- 위험 API와 `async void`
- `.Result`, `.Wait`, `Thread.Sleep`
- 빈 catch·광범위 catch
- unbounded collection·response·retry
- path handling·unsafe deserialization
- 로그 redaction
- dead code·중복 코드
- CodeQL workflow 후보
- secret scan

도구 결과는 원본 형식으로 증거에 남기고 false positive 판단 근거를 기록한다.

## 11. 개인정보와 사용자 고지

문서에 다음을 명시한다.

- 텔레메트리 존재 여부와 기본값
- API 키 저장 여부·위치·수명
- 원문·번역문을 전송하는 제공자와 시점
- 로그에 포함되는 정보와 위치
- 진단 번들 내용과 redaction
- 프로젝트·설정·로그 삭제 방법
- 제3자 API의 별도 개인정보·비용 정책

사용자 동의 없는 원격 측정이나 자동 업로드를 추가하지 않는다.

## 12. 최종 패키지 검사

- 소스·test fixture·키·로그·사용자 데이터 미포함
- PowerShell·개발 스크립트 미포함
- PDB 정책과 민감정보 검토
- ZIP entry 안전성
- checksum·manifest
- 로컬 Defender 검사
- 온라인 스캐너 자동 업로드 금지

## Exit gate

- Critical·High 0
- 비밀정보 노출 0
- 데이터 파괴 가능한 경로·XML·ZIP 취약점 0
- 관리자 권한 불필요
- 의존성·라이선스 inventory 존재
- 개인정보 안내와 보안 모델 완성
- 미해결 Medium은 명확한 위험·완화·승인 필요 상태로 기록
