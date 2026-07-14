# 10 — 독립 감사, 최종 게이트와 사용자 승인

## 1. 독립 감사 원칙

구현자의 완료 보고를 최종 판정으로 사용하지 않는다. 가능하면 새 Codex 세션·다른 검수자·clean worktree에서 수행한다.

감사자는 다음을 불신하고 원본 증거를 확인한다.

- “기능 동등성 완료” 주장
- “UI가 동일” 주장
- “PowerShell 0%” 주장
- “호환성 유지” 주장
- “보안·최적화 완료” 주장
- “전체 테스트 통과” 주장

## 2. 필수 재검증

100% 재검증:

- 저장·원자 교체·백업·롤백
- apply·DryRun·RMK 쓰기
- API 키·로그 redaction
- 경로 validation·XXE·ZIP Slip
- 취소·부분 실패·재시작
- PowerShell 잔존 전수 검색
- package manifest·EXE 실행·child process

표본 재검증:

- 기능표 최소 20%, 위험도 가중 무작위
- UI 주요 화면과 각 상태
- 데이터 fixture 유형별 최소 1개
- canonical payload 제공자·배치 유형별 최소 1개
- 성능 benchmark 대표 5개

전체 자동 test suite와 package smoke는 다시 실행한다.

## 3. 발견 결함 처리

- 재현과 증거를 먼저 남긴다.
- 영향 범위를 확인한다.
- 로컬에서 최소 범위로 수정한다.
- 집중 테스트와 관련 전체 회귀를 다시 실행한다.
- 테스트 기준을 낮추거나 기능을 제거하지 않는다.
- 수정 후 구현자 보고와 감사 보고를 구분한다.

## 4. 최종 게이트 표

| 영역 | 필수 조건 | 결과 | 증거 |
|---|---|---|---|
| Git 보호 | 원격 변경 0 |  |  |
| 기능 | 필수 누락·스텁 0 |  |  |
| UI | Golden Master 구조·동작 동등 |  |  |
| 수동 UI | 사용자 확인 대기/완료 |  |  |
| PowerShell | candidate 0% |  |  |
| 데이터 | read-only·round-trip·복구 통과 |  |  |
| 번역 | canonical payload·결과 동등 |  |  |
| 보안 | Critical/High 0 |  |  |
| 개인정보 | key·민감정보 노출 0 |  |  |
| 안정성 | 취소·실패·재시작 통과 |  |  |
| 성능 | 중대 회귀 0 |  |  |
| 빌드 | Release 경고 0 |  |  |
| 테스트 | 필수 suite 3회 연속 PASS |  |  |
| CI 후보 | 최소 권한·release 자동화 없음 |  |  |
| package | clean smoke·allowlist 통과 |  |  |
| Defender | 위협 0 |  |  |
| 문서 | README·SECURITY·privacy·notices |  |  |
| 자산 권리 | 확인 또는 blocker |  |  |
| clean machine | PASS 또는 수동 blocker |  |  |
| 독립 감사 | PASS |  |  |

## 5. 최종 판정

- `PASS`: 모든 자동 필수 게이트 통과, 사용자가 확인할 로컬 RC 준비, 원격 변경 0
- `FAIL`: 실행 가능한 필수 게이트 실패
- `BLOCKED`: 자동 게이트는 가능한 범위에서 완료됐지만 수동 UI·clean machine·서명·법적 권리 등 외부 조건 필요

증거가 없으면 PASS가 아니다. 수동 확인 대기 상태를 “공개 완료”로 표현하지 않는다.

## 6. 최종 보고

`FINAL_REPORT.md`에 최소 다음을 포함한다.

1. 판정과 기준 commit·candidate 상태
2. Git staged·unstaged·untracked, 사용자 변경 보존 여부
3. 기능 PASS/FAIL/BLOCKED 수와 누락
4. UI 비교 수·남은 차이·스크린샷 경로
5. PowerShell 파일·runtime·build/test/package/CI 검색 결과
6. 데이터 read-only·round-trip·양방향·실패 주입·백업/롤백
7. 번역 canonical payload·prompt·batch·token·retry·output
8. dependency·secret·path/XML/XLSX/ZIP/HTTP/process 보안 결과
9. 취소·실패·재시작·손상 파일·종료 후 프로세스
10. 성능 환경·원자료·회귀·수정 병목
11. restore·build·warnings·tests·3회 연속·flaky
12. 로컬 package 경로·버전 후보·파일 수·크기·SHA-256·Defender
13. README·SECURITY·privacy·third-party·자산 권리
14. 수정한 결함과 남은 결함
15. 실행하지 못한 검사·이유·사용자 작업
16. 공개 GitHub push/PR/tag/Release/asset가 모두 0인지
17. 사용자가 실행할 RC와 수동 체크리스트

## 7. 사용자 수동 확인

최소:

- EXE 시작·종료
- 첫 화면과 주요 UI가 Golden Master와 같은지
- 기존 작업 파일 복사본 열기
- 번역·메모·상태·검색·필터
- mock/무료 검증 경로의 번역 흐름
- 저장 후 재실행
- 복사본 대상 DryRun·적용·롤백
- RMK 복사본 import/export
- 오류 메시지·취소·UI 멈춤
- 종료 후 process
- Defender·SmartScreen 경험
- README만 보고 설치·사용 가능 여부

사용자가 승인해도 이는 공개 승인과 다르다.

## 8. 공개 절차

권장 승인 게이트:

```text
로컬 RC PASS
→ 사용자 실행 승인
→ 로컬 커밋 승인
→ 원격 branch push 승인
→ PR 생성 승인
→ CI 통과·병합 승인
→ 새 버전과 Draft Release 승인
→ Draft asset·notes·hash 검토
→ Publish 승인
```

기존 태그 이동·기존 Release 수정·asset clobber는 금지다. 문제가 발견되면 같은 버전을 덮어쓰지 않고 새 patch/RC를 만든다.

공개 단계는 반드시 별도 세션과 별도 명시적 승인으로 진행한다.
