# 저장·복구 단순화 설계

## 범위와 위협 모델

이 문서는 로컬 RimWorld 번역기의 저장·적용 경로를 일반적인 앱 충돌, 취소,
잠금, 권한·공간 오류, 일반적인 외부 편집으로부터 보호하는 데 필요한 수준으로
축소하기 위한 기준이다. 관리자 또는 악성 로컬 프로세스의 namespace 공격,
hardlink/junction/reparse point의 정교한 교체, 파일 ID·PID 재사용, 매 쓰기 지점의
전원 차단, 데이터베이스 수준의 다중 파일 commit, 복구 journal 변조는 필수 위협
모델에서 제외한다.

## 변경 전 호출 흐름

| 호출 경로 | 현재 구현 | 실제 보호 목적 | 판정 |
|---|---|---|---|
| `TranslationEngine` → `FileTransaction.RecoverPending` | 대상 루트별 authority shard를 찾고 journal을 자동 판정·복구 | 강제 종료 후 자동 복구 | **단순화**: 자동 판정·commit을 금지하고, 앱 소유 미완료 작업을 감지해 사용자 복구 선택을 요구한다. |
| `ApplyService`/`RmkExportService`/`RmkWorkspaceService`/`TranslationEngine` → `AcquireRecoveryLease` | 대상 루트 identity와 guard를 고정하고 장기 recovery session을 연다 | 동시 실행 방지와 복구 작업 소유권 | **단순화**: 앱 소유 작업 디렉터리와 단일 실행 manifest만 유지한다. |
| `FileTransaction` → `FileSnapshotJournal` | 대상과 `.bak`의 volume serial, file index, 길이, 시각, SHA-256을 기록하고 다시 검증한다 | 오류·취소 rollback 및 적대적 파일 교체 감지 | **단순화**: 원본 snapshot, 실행 중 실제 교체 목록, 일반 외부 변경 감지만 유지한다. |
| `FileTransactionRecoverySession` | target별 reserve/intent/ready/applied/done과 rollback plan/ready/applied/done JSON을 발행한다 | 모든 강제 종료 지점의 자동 판정 | **제거**: 실행당 manifest와 started marker로 대체한다. |
| `FileTransactionRecoveryArtifacts` | artifact 연쇄 SHA-256, 선행 artifact hash, 파일 identity를 검증한다 | journal 변조 및 재배치 공격 방어 | **제거**: 복구 journal 공격은 제품 위협 모델 밖이다. |
| `PathSafety.AcquireTrustedWriteBoundary` | 경로의 모든 디렉터리 handle을 delete-sharing 없이 유지하고 guard lock file을 만든다 | rename/reparse 교체 경쟁 방어 | **단순화**: `GetFullPath`, 로컬 경로, 허용 루트, 현재 reparse 경로 검사와 쓰기 직전 충돌 검사만 유지한다. |
| `TrustedWriteBoundary` leaf handle | target handle, volume/file index, hardlink count, content hash를 반복 검증한다 | hardlink 및 동일 이름 파일 교체 공격 방어 | **제거**: 일반 외부 편집은 길이·시각·필요 시 hash 비교로 감지한다. |
| `AtomicFile`/`AtomicTemporaryFiles` | 임시 파일 identity를 pin하고 handle 기반 rename/delete를 수행한다 | 임시 이름 탈취와 namespace 경쟁 방어 | **단순화**: 앱이 생성한 고유 sibling temp를 flush·형식 검증한 후 `File.Replace`/`File.Move`한다. |
| `LanguageFileService` | 자체 snapshot journal과 recovery session을 중복 운용한다 | 언어 파일 다중 저장 rollback | **단순화**: 공통 `FileTransaction`의 reverse rollback만 사용한다. |
| `RmkWorkspaceService` Builder stage | 앱 소유 staging에서 XML/TSV를 만든 뒤 검증하고 두 결과를 transaction으로 공개한다 | 외부 도구의 부분·잘못된 출력 방지 | **유지**: staging·형식 검증·두 파일 rollback은 유지하고 identity publication artifact만 제거한다. |
| `ProjectCleanupService`/RMK stage → `ExactDirectoryCleanup` | 앱 소유 marker와 디렉터리 identity를 확인해 프로젝트·stage를 격리 삭제한다 | 앱 소유 경로만 삭제 | **유지/후속 단순화 후보**: 소유 marker와 허용 루트 검사는 유지한다. 저장·복구 transaction에서는 호출하지 않으며, 적대적 namespace용 세부 identity 잠금은 별도 삭제 경계 작업에서 다시 축소할 수 있다. |
| `AtomicJsonStore` | 주 JSON 손상 시 단일 `.bak`을 검증해 복구하고 둘 다 손상되면 쓰기를 막는다 | 사용자 작업 데이터 복구 | **유지** |
| 적용·내보내기 경로 검사 | Workshop/RMK 구독본 차단, 로컬·허용 루트·경로 이탈 검사 | 원본·구독 데이터 보호 | **유지** |
| XML reader 설정 | DTD와 외부 entity 금지, 크기 제한 | XXE와 과도한 입력 방지 | **유지** |

## 목표 저장 모델

1. 앱 소유 실행 디렉터리에 원본 snapshot과 단일 manifest를 만든다.
2. JSON/XML/XLSX 결과는 고유한 대상 sibling temp에 완전히 기록하고 flush한 뒤 형식 검증한다.
3. 정확한 대상 목록을 transaction 시작 전에 고정하고, 각 대상이 로컬 허용 루트 안인지 검사한다.
4. 기존 대상은 교체 직전에 `.bak`으로 보존한다.
5. 검증한 sibling temp는 같은 볼륨에서 `File.Replace` 또는 `File.Move`로 교체한다.
6. 메모리에 실제 교체된 대상과 교체 직후 fingerprint를 기록한다.
7. 오류·취소 시 현재 파일이 교체 직후 fingerprint와 같을 때만 실제 교체 목록을 역순 복원한다. 다른 프로그램의 후속 변경은 덮어쓰지 않고 충돌로 보고한다.
8. 정상 완료 또는 완전한 rollback 후 실행 디렉터리를 제거한다.
9. 다음 실행에서 `started` 흔적이 없는 앱 소유 준비 폴더만 자동 정리한다. `started` 흔적이 있으면 자동 commit·자동 추론하지 않고 백업 복구 여부를 사용자에게 묻는다.

## 기존 테스트 판정

다음 검증은 새 위협 모델 밖이므로 제거하거나 일반 로컬 충돌 검증으로 대체한다.

- `Phase08ProcessRecoveryTests`: 모든 publication 지점 강제 종료, artifact hash chain 변조,
  PID/file identity 재사용, reserve/intent/ready/applied/done authority, 자동 forward commit.
- `Phase08.ForcedExitRecovery`의 1,000-target 60초 durable-journal 계약. 1,000개는 비교
  측정으로 유지하되 60초 pass/fail 계약은 제거한다.
- `Phase07ArchiveBoundaryTests` 및 관련 테스트의 hardlink 수, directory handle rename,
  동일 file ID/leaf substitution 경쟁 공격.
- `Phase08StorageFaultTests`, `Phase08ReliabilityTests`, `RmkBuilderTests`의 recovery
  publication test hook 개수·순서를 구현 세부 계약으로 삼는 검증.

대체 characterization은 새 파일 저장, 기존 파일 backup 저장, 두 번째 파일 실패 및
취소 rollback, 잠금·read-only 실패, 허용 루트·Workshop 차단, XML 검증 전 대상 불변,
JSON 주/backup 손상 조합, 앱 소유 미완료 작업 감지와 무단 commit 금지, 일반 외부
편집 충돌, 로그 redaction을 직접 검증한다.

## 변경 전 규모와 기준 성능

2026-07-14 `main` (`c1c443e3a897d22ac93ffcdc36078597c3bf4f65`) 기준 핵심
저장·복구 9개 파일은 8,127줄, 타입 61개, test hook 29개다. 전체 회귀의
`Phase08.ForcedExitRecovery` 1,000-target 측정은 71.961초였고 기존 60초 계약을
초과했다. 이 수치는 동일 synthetic fixture의 변경 후 비교 기준으로만 사용한다.

현실적인 목표는 100개 소형 파일의 stage·검증·backup·교체·정리가 일반 SSD에서
상호작용을 방해하지 않는 수 초 이내에 끝나며, 모든 파일 I/O를 UI 스레드 밖에서
실행하는 것이다. 1,000개 결과는 회귀 관찰값으로 기록하되 제품 pass/fail 계약으로
사용하지 않는다.

## 의도적으로 남는 위험

- 권한 있는 로컬 프로세스가 검사 직후 path namespace를 바꾸는 공격은 막지 않는다.
- 실행 manifest나 snapshot을 공격자가 변조하는 경우 복구의 진위를 보장하지 않는다.
- 강제 종료 뒤 자동으로 어느 파일까지 적용되었는지 추론하지 않는다. 사용자가
  복구를 선택할 때 실행 시작 전 snapshot으로 되돌린다.
- 다중 파일 적용은 정상 오류·취소에는 rollback하지만 전원 차단까지 포함한
  데이터베이스 수준의 원자성은 제공하지 않는다.

## 구현 결과와 검증

- 핵심 저장·복구 파일은 9개 8,127줄에서 7개 2,781줄로 5,346줄(65.8%) 감소했다.
  recovery 관련 타입은 61개에서 22개, test hook은 29개에서 4개로 줄었다.
- `AtomicCommitRecoveryPlan`, `FileTransactionRecoveryArtifacts`와 target별
  reserve/intent/ready/applied/done/rollback artifact, 연쇄 hash 검증, 자동 recovery
  authority 판정을 제거했다. `AtomicTemporaryFiles`의 중복 Windows file-identity
  P/Invoke와 hardlink-count 검증도 제거했다.
- 새 `Storage.SimpleRecoveryThreatModel`은 새 파일, 기존 파일과 `.bak`, 두 번째 파일
  실패, 취소, XML 사전 검증, 외부 편집 충돌, 강제 종료 흔적 감지, 사용자 확인 전
  무단 commit 금지와 명시적 복구를 직접 검증한다. 잠금·read-only, 허용 루트·Workshop,
  JSON 단일 백업/이중 손상, 로그 redaction은 기존 결과 기반 회귀와 함께 유지했다.
- 기존 `Phase08ProcessRecoveryTests`, `Phase08ExportArtifactBoundaryTests`, 1,000-target
  60초 합격 계약과 identity/hardlink/namespace 공격 전용 테스트를 제거했다. 결과 기반
  취소·rollback·RMK builder·Atomic JSON 테스트가 이를 대체한다.
- 동일 synthetic 1,000-target 측정은 71.961초에서 20.673초로 71.3% 감소했고,
  100-target은 1.649초였다. 현실적인 관찰 기준은 일반 SSD에서 100개 소형 파일을
  수 초 이내 처리하는 것이며 1,000개는 pass/fail 계약이 아닌 추세 값으로만 남긴다.
- strict Release build는 경고/오류 0/0, 전체 회귀는 80/80 PASS(최종 59.025초)다.
  복구 선택은 `Task.Run`으로 실행되어 UI 스레드에서 파일 복원을 수행하지 않는다.
- 최종 데이터 손실 검토에서 정상 오류·취소는 이번 실행이 실제로 교체한 파일만
  역순 복원하고, 교체 뒤 다른 프로그램이 변경한 파일은 조용히 덮어쓰지 않음을
  확인했다. 강제 종료 뒤에는 자동 commit하지 않으며 사용자가 선택해야 시작 전
  snapshot으로 복구한다. 이 선택 전 현재 파일과 snapshot은 모두 보존된다.
