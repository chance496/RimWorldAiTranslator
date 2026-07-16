# Roadmap

기준: 공개 v1.1.0. 상태는 `DONE`, `OPEN`, `INVESTIGATE`, `DEFERRED`를 사용한다.

## P0 - 데이터와 실행 안전

| ID | 상태 | 항목 | 완료 조건 |
|---|---|---|---|
| P0-01 | DONE | JSON 원자 저장·백업 복구·이중 손상 쓰기 차단 | `Storage.RoundTrip`, `Storage.BackupRecovery`, `Storage.DoubleCorruptionBlocksWrite` |
| P0-02 | DONE | 로컬 Korean과 RMK 다중 파일 백업·롤백 | `Translation.DirectOutputRollback`, `Apply.Local`, `Export.RmkTransaction` |
| P0-03 | DONE | API 키 비저장·로그 마스킹·진단 개인정보 제외 | `Security.ApiKeysNotPersisted`, `Security.LogRedaction`, `Diagnostics.Privacy` |
| P0-04 | DONE | 사용자 실행 경로의 PowerShell/실행 정책 제거 | 자체 포함 ZIP 허용 목록, 실행·종료 smoke, PowerShell 자식 0개 |
| P0-05 | DONE | 경로 이탈, XML DTD/외부 엔터티와 위험 Def 차단 | `Project.CleanupBoundary`, `Source.DefSafety`, 적용 회귀 |

## P1 - 핵심 정확성·복구·안정성

| ID | 상태 | 항목 | 완료 조건 |
|---|---|---|---|
| P1-01 | DONE | Keyed/DefInjected/허용 Def 추출과 중복 identity | `Source.Extraction`, `Source.DefSafety`, `Source.DuplicateIdentity` |
| P1-02 | DONE | 다중 키 순환, 제한, 재시도와 깨진 배치 분할 | `Translation.ApiRetryAndKeyRotation` |
| P1-03 | DONE | 취소·체크포인트·남은 항목 재개 | `Translation.CancellationAndResume` |
| P1-04 | DONE | 토큰·태그·조사·문법 접두어 안전 검사 | `Validation.TokensAndParticles`, `Apply.TokenSafety` |
| P1-05 | DONE | 원문 변경 감지와 기존 번역 보존·강등 | `Review.SourceChangeInheritance`, `Export.RmkSourceHistory` |
| P1-06 | DONE | RMK 언어 불일치로 인한 전체 변경 오판 방지 | `Translation.RmkLanguageMismatch` |
| P1-07 | INVESTIGATE | 외부 제공자별 최신 모델·제한 호환성 표 | 무료/격리 계정 또는 사용자가 제공한 키로 명시적 수동 검증. 자동 유료 호출 금지 |

## P2 - 성능·사용성·UI

| ID | 상태 | 항목 | 완료 조건 |
|---|---|---|---|
| P2-01 | DONE | 프로젝트 카드 통계 캐시와 5,000행 가상 목록 | 캐시 무효화 회귀, 5,000행 벤치마크 기록 |
| P2-02 | DONE | 장시간 작업 비동기화와 항상 접근 가능한 중지 | 취소 회귀, 원문 갱신·번역 중 UI 유지 확인 |
| P2-03 | DONE | 첫 페인트 원자 공개와 최대화 초기 레이아웃 보정 | 시작/재시작 실제 화면 확인, 미완성 컨트롤 노출 없음 |
| P2-04 | OPEN | 다중 모니터·100/125/150/200% DPI 자동 UI 매트릭스 | 글자 잘림·겹침·키보드 접근성 결과를 CI 산출물로 기록 |
| P2-05 | OPEN | 첫 실행 단일 EXE 초기화 비용 추적 | 릴리스별 첫 표시·재실행·메모리 수치를 같은 호스트 조건으로 기록 |
| P2-06 | OPEN | 검색 최악 지연 원인 계측 | 5,000행 검색 p95와 최대값을 분리하고 GC/JIT 워밍업 조건 명시 |

## P3 - 선택 기능

| ID | 상태 | 항목 | 완료 조건 |
|---|---|---|---|
| P3-01 | DEFERRED | Authenticode 코드 서명 | 인증서·보관·갱신 비용과 릴리스 절차 확정 |
| P3-02 | DEFERRED | Windows arm64 배포 | 실제 장치에서 UI·RMK XLSX·단일 파일 배포 검증 |
| P3-03 | DEFERRED | 하드코딩 런타임 문구용 별도 Harmony 보조 도구 | XML 번역과 분리된 안전 모델·옵트인 설계 완료 |

다음 자율 작업은 P1-07을 실제 키 없이 추측해 구현하지 않는다. 우선순위는 P2-04의 재현 가능한 UI 매트릭스와 P2-06의 검색 측정 분리다.
