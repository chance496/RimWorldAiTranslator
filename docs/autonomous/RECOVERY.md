# Autonomous Recovery

- 마지막 검증된 제품 코드 체크포인트: `88b1a4b` (`fix: report preserved cleanup folders`)
- 기준 백업: 브랜치 `codex/backup/20260712-073630`, 태그 `codex-backup-20260712-073630`, 커밋 `aff52cc`
- 현재 작업 브랜치: `codex/autonomous/20260712-073630`
- 최종 문서 체크포인트는 현재 브랜치의 최신 `docs:` 커밋으로 확인한다.
- 패키지: `dist\RimWorldAiTranslator`와 `dist\RimWorldAiTranslator.zip`; 생성물이므로 Git 체크포인트에는 포함하지 않는다.
- 재개 시 `git status --short`, `git log -3 --oneline`, 이 파일, `../ROADMAP.md`, `../PROJECT_STATE.md`를 먼저 확인한다.
- 복구가 필요하면 백업 참조를 읽기 전용 기준으로 비교하고, 사용자 변경을 reset/clean/checkout으로 제거하지 않는다.

남은 외부 검증은 실제 125/150/200% DPI뿐이다. P0-P3 로컬 작업은 완료됐고 실행 가능한 `READY` 항목은 없다.
