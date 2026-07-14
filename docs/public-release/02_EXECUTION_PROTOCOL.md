# 02 — 장기 작업 실행 프로토콜

## 1. 시작 절차

1. 루트와 적용 범위의 모든 `AGENTS.md`·`AGENTS.override.md`를 읽는다.
2. 저장소의 `PROJECT_STATE`, `ROADMAP`, `QUALITY_GATES`, `ARCHITECTURE`, `DECISIONS`, `WORKLOG` 문서를 찾고 읽는다.
3. 다음을 기록한다.
   - 현재 시각·시간대, OS, CPU, RAM, 디스플레이, DPI
   - 설치 .NET SDK/runtime, Git 버전
   - 현재 브랜치, HEAD, `git status --short --branch`
4. 사용자 작업을 보존한 채 가능한 경우 두 worktree를 만든다.
   - baseline: Golden Master commit
   - candidate: 현재 로컬 C# 후보
5. `docs/release-readiness/`와 증거 디렉터리를 초기화한다.
6. `PLANS_TEMPLATE.md`와 templates를 사용해 STATE·PLAN·DECISIONS·ISSUES·COMMAND_LOG를 만든다.

worktree 생성이 사용자 변경 때문에 안전하지 않으면 파괴적 정리를 하지 말고 현재 상태를 보존하는 대안을 사용하거나 `BLOCKED`로 기록한다.

## 2. 상태 파일 규칙

`STATE.md`에는 항상 다음이 있어야 한다.

- 기준 commit과 후보 HEAD/작업 트리 식별자
- 현재 Phase와 상태: `NOT_STARTED`, `IN_PROGRESS`, `PASS`, `FAIL`, `BLOCKED`
- 이번 Phase의 entry condition
- 실행한 명령과 결과 경로
- 수정 파일
- 발견 이슈와 잔여 위험
- 다음 정확한 작업
- 외부 GitHub 변경 여부

Phase를 바꾸기 전에 STATE를 먼저 갱신한다. 세션이 종료돼도 STATE만 읽으면 재개 가능해야 한다.

## 3. 계획 작성

코드를 수정하기 전에 `PLAN.md`를 작성한다.

- 현재 구조와 PowerShell 책임
- C# 후보 구조와 재사용 가능한 코드
- 기능·UI·데이터·API 차이
- Phase별 작업, 검증, 예상 위험
- 변경 순서와 롤백 지점
- 사용자가 결정해야 하는 항목과 Codex가 보수적으로 선택할 항목

계획은 고정 문서가 아니라 증거에 따라 갱신한다. 다만 범위를 임의로 기능 추가·UI 재설계로 확장하지 않는다.

## 4. Phase 실행 루프

각 Phase마다 다음을 반복한다.

```text
해당 Phase 문서 읽기
→ entry condition 확인
→ 최소 재현·기준 증거 생성
→ 구현 또는 수정
→ 집중 테스트
→ 관련 회귀
→ 전체 중요 게이트
→ diff 자체 검토
→ 증거 저장
→ STATE·ISSUES·DECISIONS 갱신
→ exit gate 판정
```

첫 실패에서 종료하지 않는다. 수정 가능한 결함이면 원인을 고치고 반복한다.

## 5. 기능 ID 중심 작업

Golden Master에서 발견한 모든 기능은 기능 ID를 갖는다. 한 기능을 완료하려면 다음이 모두 연결돼야 한다.

- 기준 구현 위치
- 입력·출력·상태 변화
- 성공·취소·오류·재시작 동작
- C# 구현 위치
- 테스트 이름
- 증거 경로
- 판정

버튼이나 메서드가 존재하는 것만으로 PASS가 아니다.

## 6. 증거 규칙

증거 디렉터리 예:

```text
artifacts/release-readiness/20260713-120000/
├─ environment/
├─ baseline/
│  ├─ feature/
│  ├─ ui/
│  ├─ payload/
│  └─ performance/
├─ candidate/
│  ├─ ui/
│  ├─ compatibility/
│  ├─ security/
│  ├─ performance/
│  └─ tests/
├─ package/
└─ reports/
```

모든 PASS는 다음 중 하나 이상을 가리켜야 한다.

- 자동 테스트 결과
- 재현 명령과 종료 코드
- 전후 해시·manifest
- 기준/후보 screenshot·diff
- canonical payload diff
- benchmark 원자료
- 사람이 확인할 수 있는 로그·보고서

동적 값과 비밀정보는 마스킹한다. 원문 전체를 증거에 복사하지 않는다.

## 7. 변경 규칙

- 기존 C# 핵심 로직이 올바르면 재사용한다.
- 잘못 만든 UI·서비스·저장 코드는 “이미 작성됨”을 이유로 보존하지 않는다.
- 한 Phase에서 관련 없는 대규모 리팩터링을 섞지 않는다.
- 새 production dependency는 필요성·라이선스·취약점·대안과 함께 DECISIONS에 기록한다.
- 데이터 형식 변경은 마지막 수단이며 버전·마이그레이션·백업·다운그레이드 정책이 필요하다.
- framework 변경은 기능·UI 동등성 확보 후 별도 단계로 수행한다.

## 8. 질문과 자율 진행

사용자가 설명하지 않아도 다음은 직접 조사한다.

- 화면 배치와 컨트롤
- 버튼·메뉴 동작
- 프롬프트와 API 요청
- 파일 형식과 저장 동작
- 오류·취소·복구 흐름

가역적 선택은 호환성과 안전을 우선해 결정하고 기록한다. 다음과 같이 사용자만 결정할 수 있는 진짜 blocker만 질문한다.

- 법적 권리 또는 라이선스 확인
- 코드 서명 인증서 사용
- 실제 공개 버전 번호 승인
- UI의 미묘한 디자인 판단
- 실제 사용자 파일이 없으면 재현할 수 없는 호환 문제

질문을 기다리는 동안 진행 가능한 다른 Phase는 계속한다.

## 9. 실패 처리

```text
실패 증거 저장
→ 최소 재현 작성
→ 원인과 영향 범위 확인
→ 최소 수정
→ 집중 테스트
→ 관련 회귀
→ 전체 중요 게이트
→ 보고서 갱신
```

테스트를 삭제·skip·완화하거나 기능·검증·복구를 제거해 실패를 숨기지 않는다.

즉시 `BLOCKED`:

- 사용자 원본 손상 위험
- 실제 비밀정보 발견
- 시스템·디스크 불안정
- 외부 배포가 필요한 단계
- 인증서 private key 또는 법적 권리 확인 필요
- 파괴적 Git 작업 없이는 진행 불가

## 10. 완료 전 자체 검토

각 Phase 종료 시 전체 diff를 다음 관점으로 다시 읽는다.

- 기능 누락과 상태 전이
- 데이터 무결성·원자 저장·복구
- 비밀정보·경로·외부 입력
- async·취소·자원 해제
- UI 동등성·접근성
- 성능 회귀와 중복 I/O
- 테스트가 실제 동작을 검증하는지

메인 구현이 끝나면 `10_INDEPENDENT_AUDIT_AND_FINAL_GATE.md`에 따라 가정을 버리고 자체 적대적 감사를 수행한다. 이후 새 세션 독립 감사가 권장된다.
