# 08 — 안정성, 성능, 자동화 테스트와 CI 후보

## 1. 오류 주입과 복구

필수 시나리오:

- 첫 실행·반복 실행·정상 종료·강제 종료
- 프로젝트 로딩·번역·적용·RMK 쓰기 중 취소
- 저장 직전·임시 기록·flush·교체·백업·롤백 실패
- 디스크 공간 부족·권한 거부·파일 잠금
- 네트워크 없음·timeout·API 오류·부분 응답
- 손상 설정·프로젝트·review state·XML·XLSX
- 앱 재시작·시스템 시간 변경
- 중복 실행·같은 프로젝트를 두 인스턴스가 여는 상황

확인:

- 실패를 성공으로 오인하지 않음
- 사용자 메시지가 원인과 복구 방법을 설명
- 상세 로그는 stack trace를 보존하되 비밀정보 없음
- 마지막 정상 파일과 백업 유지
- 재실행 후 작업 계속 가능
- 미완료 작업을 완료로 표시하지 않음
- temp 파일은 정리되거나 안전한 복구 대상으로 인식
- 종료 후 child process·background task 0

## 2. 성능 측정 원칙

- 같은 컴퓨터·전원 설정·fixture·출력 조건
- Release build
- cold와 warm 구분
- 최소 5회, median과 p95 또는 worst 기록
- Golden Master와 C# 후보 모두 측정
- 측정 도구 오버헤드 기록
- 전후 코드와 결과 정확성 동일

측정 항목:

- cold/warm startup과 첫 usable 화면
- idle working set
- 프로젝트 생성·열기
- 5,000행 로드
- XML 추출
- 검색·필터·정렬·상태 변경
- 자동 저장·전체 저장
- DryRun·적용·롤백
- RMK XLSX 읽기·병합·쓰기
- 취소 시각 반응·안전 중단 완료
- 종료와 종료 후 프로세스
- 20회 열기/닫기 후 메모리
- publish·ZIP 크기

## 3. 회귀 기준

하드웨어 차이를 고려해 Golden Master 대비 상대 기준을 우선한다.

- 10% 이상 느린 주요 작업: 원인 분석 필수
- 20% 이상 느린 주요 작업: 해결 또는 명시적 blocker
- 5,000행 검색·필터 median 200ms 이하 목표
- 일반 입력에서 200ms 이상 UI stall 반복 시 FAIL
- 1초 이상 UI stall은 장시간 작업에서도 허용하지 않음
- 취소 버튼 시각 반응 250ms 이내 목표
- 안전 중단 완료 2초 이내 목표; 불가능한 I/O는 이유와 안전 지점 명시
- 20회 반복 후 안정화 메모리가 최초 안정화 대비 15% 이상 계속 증가하면 누수 조사

절대 수치를 못 맞춰도 Golden Master보다 개선되고 사용성 문제가 없다면 원자료와 이유를 사용자가 판단할 수 있게 보고한다.

## 4. 최적화 순서

```text
기능 동등성
→ 정확성·데이터 안전
→ 측정
→ 병목 확인
→ 영향 큰 병목 수정
→ 집중 benchmark
→ 전체 기능·보안 회귀
→ 전후 보고
```

검토 대상:

- 반복 파일 읽기·XML 파싱·직렬화
- 전체 목록 재생성·불필요한 객체·문자열 복사
- 반복 regex 생성
- UI virtualization과 incremental update
- `HttpClient` 재사용·동시성 제한
- cache와 올바른 invalidation
- 과도한 로그 I/O
- event·timer·stream 누수

금지:

- 검증·로그·복구 제거
- 무제한 병렬 요청
- 정확도 저하
- 모든 파일 무조건 메모리 적재
- 가독성을 크게 훼손하는 미세 최적화

## 5. 테스트 계층

### Unit

- 안정적 ID·상태 전이
- 토큰·태그·변수 보존
- 번역 검증·용어집 선택·prompt 구성
- path safety·설정·파일 형식 버전
- retry·backoff·batch 분할

### Integration

- atomic JSON·XML·XLSX
- project repository
- apply·backup·rollback
- fake HTTP·partial success·cancel·resume
- diagnostics redaction
- 기존 작업 파일 round-trip

### End-to-End

- 앱 실행·프로젝트 생성/열기
- 5,000행 표시
- mock 번역·검토·저장·재실행
- DryRun·적용·RMK export·종료

### UI Harness

- 화면·컨트롤·접근성 이름
- 탭 흐름·해상도·DPI·테마
- screenshot·잘림·로딩·오류·취소·완료

### Package Smoke

- 깨끗한 폴더 압축 해제
- EXE 직접 실행·정상 종료
- PowerShell child 0
- 한글·공백 경로
- SDK 없는 환경 가정
- 읽기 전용 실패 메시지
- 설정·로그 위치
- 불필요 파일 없음

## 6. 테스트 품질

- Release build 경고 0
- 전체 필수 테스트 3회 연속 통과
- 확인된 flaky test 0
- 실제 네트워크·유료 API 0
- 실제 사용자 데이터 0
- raw coverage 숫자보다 critical branch 검증을 우선
- Core/Application coverage 보고서를 남기고 중요 저장·경로·롤백·토큰 분기는 높은 커버리지를 확보
- 무의미한 assertion과 구현 복제 테스트 금지
- 실패 test skip으로 완료 금지

## 7. CI 후보

로컬에서 workflow 파일을 작성·검토하되 push·실행하지 않는다.

필수 job:

- restore
- Release build
- unit/integration/UI-independent regression
- dependency vulnerability·secret scan
- package manifest·PowerShell 잔존 검사
- analyzers·format 정책
- 로컬과 동일한 artifact 생성

규칙:

- 최소 권한, 기본 `contents: read`
- action 버전 고정, 가능하면 commit SHA pin
- PowerShell shell·인라인 PowerShell 금지
- PR 검증과 release workflow 분리
- main push로 Release 자동 생성 금지
- 기존 asset clobber 금지
- release 권한은 기본 미부여

## Exit gate

- 주요 오류 주입에서 데이터 손실·silent failure·zombie process 0
- 전체 필수 테스트 3회 연속 통과
- flaky 0
- 성능 원자료와 Golden Master 비교표 완성
- 20% 이상 중대한 회귀 0 또는 blocker
- PowerShell 없는 로컬 CI 후보와 package smoke 준비
