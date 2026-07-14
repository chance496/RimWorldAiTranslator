# 00 — 시작점과 문서 지도

## 프로젝트 상수

- 저장소: `chance496/RimWorldAiTranslator`
- Golden Master: `4c7d11b49126ba3987e9d49bd16944d4376ba0bc`
- 참고용 C# 마이그레이션: `891d135bb9b37e7d56dd4c29336bb20d277841bc`
- 현재 로컬 C# 작업이 참고 커밋보다 최신일 수 있으므로 작업 트리를 먼저 보존한다.
- 기존 공개 `v1.0.0`, 태그, 릴리스 노트와 자산은 불변으로 취급한다.

## 작업 목표

기존 PowerShell 안정판의 기능·UI·작업 파일·번역 요청을 보존하면서 다음을 충족하는 C# Windows 데스크톱 앱을 로컬 RC로 만든다.

- 사용자 실행, 빌드, 테스트와 로컬 패키징에서 PowerShell 0%
- 기존 프로젝트·설정·검토 JSON, XML, 용어집, RMK XLSX 호환
- 실제 외부 API 없이 번역 요청·후처리 동등성 증명
- 데이터 손실 방지, 보안·개인정보·공급망 검사
- 취소·실패·재시작·백업·롤백 검증
- 대형 프로젝트 UI 반응성과 성능 측정
- 로컬 `win-x64` RC와 사용자 확인 자료 생성
- 공개 GitHub 변경 0건

## 문서 읽기 순서

Codex는 모든 파일을 한 번에 읽고 기억하려 하지 않는다.

1. 매 세션 시작 시 읽기
   - `00_START_HERE.md`
   - `01_GOVERNANCE_AND_DONE.md`
   - `02_EXECUTION_PROTOCOL.md`
   - `docs/release-readiness/STATE.md`가 있으면 함께 읽기
2. 현재 Phase 시작 직전에 해당 파일 읽기
   - `03_BASELINE_AND_PARITY.md`
   - `04_CSHARP_MIGRATION_AND_POWERSHELL_ZERO.md`
   - `05_DATA_AND_TRANSLATION_COMPATIBILITY.md`
   - `06_UI_AND_ACCESSIBILITY.md`
   - `07_SECURITY_PRIVACY_AND_SUPPLY_CHAIN.md`
   - `08_RELIABILITY_PERFORMANCE_AND_TESTS.md`
   - `09_LOCAL_RC_AND_DOCUMENTATION.md`
3. 구현 종료 후 읽기
   - `10_INDEPENDENT_AUDIT_AND_FINAL_GATE.md`

## 실행 지도

| Phase | 목적 | 주요 산출물 |
|---|---|---|
| 0 | 환경·worktree·상태 확립 | STATE, ENVIRONMENT, COMMAND_LOG |
| 1 | Golden Master 기능·UI 명세 | FEATURE_PARITY_MATRIX, 기준 스크린샷 |
| 2 | C# 차이 분석과 실행 계획 | PLAN, DECISIONS, GAP_REPORT |
| 3 | C# 기능 이전과 구조 정리 | 구현, 단위·통합 테스트 |
| 4 | PowerShell 완전 제거 | 잔존 검색 보고서, C# 도구 대체 |
| 5 | 데이터·번역 동등성 | round-trip, canonical payload diff |
| 6 | UI·접근성 동등성 | 비교 이미지, UI matrix |
| 7 | 보안·개인정보·의존성 | threat model, findings |
| 8 | 안정성·성능·자동화 테스트 | fault tests, benchmark, CI 후보 |
| 9 | 로컬 RC·문서 | publish, ZIP, checksum, 사용자 문서 |
| 10 | 독립 감사·최종 판정 | FINAL_REPORT, PASS/FAIL/BLOCKED |

## 작업의 단일 진실 원천

다음 파일을 생성해 계속 갱신한다.

```text
docs/release-readiness/
├─ STATE.md
├─ PLAN.md
├─ DECISIONS.md
├─ ISSUES.md
├─ COMMAND_LOG.md
├─ FEATURE_PARITY_MATRIX.md
├─ UI_PARITY_MATRIX.md
├─ SECURITY_FINDINGS.md
├─ PERFORMANCE_REPORT.md
└─ FINAL_REPORT.md
```

템플릿은 `docs/public-release/templates/`에 있다. 실행 증거는 다음에 둔다.

```text
artifacts/release-readiness/<yyyyMMdd-HHmmss>/
```

`artifacts/`에는 사용자 원본·API 키·민감한 원문을 넣지 않고 Git 추적에서 제외한다.

## 비기능 원칙

- 기존 기능이 정답이고 새로운 기능은 범위 밖이다.
- UI가 더 현대적이라는 이유로 기존 흐름을 바꾸지 않는다.
- C# 전환과 프레임워크 업그레이드, UI 재설계를 한 번에 섞지 않는다.
- 성능은 측정 후 병목을 고친다.
- 테스트와 복구를 삭제해 속도나 PASS를 만들지 않는다.
- 사용자가 설명할 수 있는 내용을 먼저 Golden Master에서 직접 조사한다.
- 첫 실패에서 멈추지 않고 수정·재검증 루프를 계속한다.
- 실제로 진행할 수 없는 조건만 `BLOCKED`로 남긴다.

## 최종 결과

메인 세션의 최종 결과는 공개가 아니라 다음이다.

```text
검증된 로컬 C# RC
+ 비교 가능한 기준 증거
+ 사용자 수동 확인 체크리스트
+ 공개 전 남은 blocker 목록
+ GitHub 변경 0건
```
