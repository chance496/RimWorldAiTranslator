# RimWorld AI Translator 프로젝트 지침

## 시작 절차

1. `docs/PROJECT_STATE.md`, `docs/ROADMAP.md`, `docs/QUALITY_GATES.md`, `docs/ARCHITECTURE.md`를 읽는다.
2. `git status --short --branch`와 staged/unstaged diff를 확인한다. 기존 변경은 사용자 소유로 보존한다.
3. 현재 문제와 직접 관련된 가장 작은 범위를 수정하고, 관련 품질 게이트를 실행한다.

## 제품 불변 조건

- 프로젝트 하나는 모드 하나와 연결한다. 안정적인 항목 식별자는 파일+키 또는 검증된 `Def Class + Node`를 사용한다.
- Workshop 원본과 RMK 구독본은 읽기 전용이다. 로컬 `Languages\Korean` 또는 사용자가 지정한 RMK 작업 클론만 명시적 적용 대상이다.
- 원문 변경 시 기존 번역과 과거 원문은 보존하되 상태를 미번역·변경됨으로 내려 적용을 차단한다.
- XML `defName`, 경로·식별자, 렌더 트리·색상 채널, 패치 조작 필드는 번역하지 않는다. 허용된 표시 필드만 추출한다.
- RimWorld 토큰, 태그, 문법 접두어, 줄바꿈 이스케이프와 `(은)는` 형식의 자동 조사를 보존한다.
- API 키는 메모리에만 두며 설정, 프로젝트, 로그, 진단, fixture와 Git에 기록하지 않는다.
- 상태와 결과는 임시 파일에 쓴 뒤 검증·flush·원자 교체한다. 기존 출력 갱신은 백업과 롤백을 제공한다.

## 코드 경계

- `src/RimWorldAiTranslator.App`: WinForms 화면, 사용자 상호작용, 비동기 오케스트레이션. 긴 작업은 `CancellationToken`을 전달하며 UI 스레드를 막지 않는다.
- `src/RimWorldAiTranslator.Core`: 저장, 탐색, 추출, 번역, 검수, 품질, 적용, RMK와 진단. WinForms에 의존하지 않고 fixture로 검증 가능해야 한다.
- `src/RimWorldAiTranslator.Native`와 `native/`: XML/XLSX 저수준 구현. DTD 금지, 크기 제한, 기존 XLSX 구조 보존 규칙을 유지한다.
- 사용자 실행 경로에서 PowerShell, `pwsh`, 콘솔 또는 실행 정책에 의존하지 않는다. PowerShell은 빌드·용어집 생성 보조 스크립트에만 허용한다.
- 기능을 삭제해 성능을 맞추지 않는다. 같은 fixture와 결과 조건으로 전후를 측정한다.

## 검증과 안전

- 테스트는 고유한 `%TEMP%` fixture만 사용한다. 실제 `%LOCALAPPDATA%\RimWorldAiTranslator`, Workshop, RMK, 사용자 API 키를 쓰지 않는다.
- 기본 회귀에서 외부 네트워크와 유료 API를 호출하지 않는다. 제공자 동작은 loopback fake handler를 사용한다.
- 저장·적용·RMK 변경에는 정상, 취소, 실패, 재시도, 부분 성공, 롤백을 함께 검증한다.
- UI 변경은 밝음·어두움, DPI, 최소 창 크기, 키보드 흐름, 글자 잘림과 중지 버튼 접근성을 확인한다.
- 실제 명령과 릴리스 기준은 `docs/QUALITY_GATES.md`를 따른다. 실행하지 못한 고위험 검증은 완료로 처리하지 않는다.

## Git과 외부 작업

- `git reset --hard`, `git clean -fd[x]`, 광범위한 restore/checkout, `git add .`와 `git add -A`를 사용하지 않는다.
- 커밋, push, PR, release, 외부 업로드, 유료 API, 시스템 설정 변경과 종료·재시작은 사용자의 현재 명시적 요청이 있을 때만 수행한다.
- 프리즈, WHEA, 블루스크린 또는 저장장치 오류가 나타나면 무거운 빌드·분석을 중단하고 마지막 안전 상태를 `docs/WORKLOG.md`에 남긴다.
