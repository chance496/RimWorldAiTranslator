# Worklog

## 2026-07-12 - 안정화 작업 시작 체크포인트

- 원래 브랜치/HEAD: `audit/def-safety-ui-v0.2.1` / `7ec133500600017f02688c990f6513f5498466d0`.
- 백업 브랜치: `codex/backup/20260712-073630`.
- 예정 백업 태그: `codex-backup-20260712-073630`.
- 기존 8개 수정 파일과 새 운영 문서·Def 규칙만 보존하며 ignored 실행 파일, `dist`, `reviews`, 로컬 용어집은 제외한다.
- 사전 PowerShell Parser 8/8 통과, `git diff --check` 오류 없음, 후보 파일에서 API 키 형태 값 없음.
- 이 체크포인트는 기존 작업의 보존용이며 기능 승인 판정이 아니다.

## 2026-07-12 - 자율 개발 운영 체계 점검

### 범위

- 제품 소스코드는 수정하지 않음.
- AGENTS 용량과 저장소 경계를 확인하고 운영 문서를 분리함.
- 빌드, 테스트, 실행, 패키징 명령과 현재 기능/위험을 코드에서 조사함.

### 확인한 상태

- 상위 기존 `AGENTS.md`: 31,349바이트, 385줄.
- Codex 기본 프로젝트 문서 한도: 32,768바이트. 기존 사용률 95.7%.
- 정리 후 상위 `AGENTS.md`: 1,492바이트, 17줄. 제품 루트 `AGENTS.md`: 5,281바이트, 54줄.
- 제품 Git: `audit/def-safety-ui-v0.2.1`, HEAD `7ec1335`, 8개 수정 파일과 1개 새 규칙 파일.
- 자동 테스트/린트/CI 없음.

### 실행한 검사

- `git status --short --branch`, `git remote -v`, `git log`, `git diff --stat`, `git diff --name-status`, `git diff --check`.
- 루트 `*.ps1` 8개를 `System.Management.Automation.Language.Parser.ParseFile`로 검사: 8 PASS, 0 FAIL.
- `build-package.ps1`, README, 실행기, UI 저장/삭제, 추출, 배치 재시도, 적용, RMK XML/XLSX 경로를 읽기 전용으로 추적.

### 확인한 주요 위험

- 프로젝트 상태 주 파일과 백업이 모두 손상되면 빈 목록으로 조용히 대체됨.
- 적용 XML과 RMK XLSX에 성공 후 남는 롤백 백업이 없음.
- 현재 대규모 제품 변경이 커밋되지 않음.
- 회귀 테스트와 패키지 자동 게이트가 없음.
- 직접 PowerShell 실행기의 한 오류 문구가 깨져 있음.

### 보류한 검사

- 패키지 빌드, GUI 실행, 실제 모드/RMK 쓰기, 네트워크 번역.
- 사유: 문서 전용 범위와 최근 호스트 불안정. 자율 제품 개발 시작 전 `ROADMAP.md`의 차단 요소를 먼저 해결해야 함.

### 문서 검증

- 상위/제품/범위별 `AGENTS.md`가 모두 32,768바이트 미만임을 확인.
- 필수 운영 문서와 모든 코드 근거 대상의 존재를 확인.
- 새 지침·문서에서 trailing whitespace와 API 키 형태 문자열이 없음을 확인.
- Markdown 외부 링크가 HTTPS임을 확인.
- 기존 추적 제품 소스 diff가 점검 전과 같은 8개 파일 `+752/-199`임을 확인. 이번 작업에서 제품 소스는 수정하지 않음.
