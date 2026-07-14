# 05 — 작업 파일, 저장 안전성과 번역 동등성

## 1. 호환성 원칙

- C# 전환은 사용자 파일 형식 변경을 자동으로 정당화하지 않는다.
- 기존 작업 파일은 가능한 그대로 읽고 쓴다.
- 읽기만 했을 때 자동 마이그레이션·저장을 하지 않는다.
- 형식 변경이 정말 필요하면 버전, 백업, 원자 마이그레이션, 실패 복구, 다운그레이드 정책을 제공한다.
- 테스트는 원본 복사본에서 수행하고 전후 해시를 남긴다.

## 2. fixture 세트

익명화 또는 합성 fixture에 다음을 포함한다.

- 소형·중형·5,000행 프로젝트
- 미번역·번역·승인·변경됨·오류 상태 혼합
- 메모·이력·중복 원문·원문 변경
- 긴 문자열·한글·영문·다국어
- XML 태그·RimWorld 토큰·`{}`·`[]`·`$` 변수·줄바꿈
- 누락 필드·`null`·알 수 없는 추가 필드
- UTF-8 BOM 있음/없음, CRLF/LF
- 손상 JSON·XML·XLSX
- RMK workbook 스타일·주석·추가 열·메타데이터·Required Mods
- 읽기 전용·잠긴 파일·공백·한글·괄호·긴 경로

fixture manifest에는 파일 해시, 크기, 의도, 민감정보 없음 여부를 기록한다.

## 3. 읽기 전용 열기

각 기존 파일에 대해:

1. 파일 복사본과 기준 해시 생성
2. C#판에서 열기
3. 번역·상태·메모·이력·경로를 기준과 비교
4. 아무 변경 없이 닫기
5. 파일 목록·크기·해시 비교
6. 자동 생성된 파일이 있다면 필요성과 위치 검토

읽기만 했는데 기존 작업 파일이 변경되면 FAIL이다.

## 4. round-trip

1. 하나의 명확한 값을 수정
2. 저장
3. 앱 종료·재실행
4. 수정값과 나머지 데이터 보존 확인
5. 알려지지 않은 필드·노드·XLSX package part 보존 확인
6. 가능하면 PowerShell판에서 다시 열어 양방향 호환 확인

양방향 호환이 불가능하면:

- 정확히 어떤 파일·필드가 달라지는지
- 이전 버전에서 열 때의 동작
- 백업·내보내기·다운그레이드 방법
- 버전 호환 범위

을 문서화하고 사용자 승인을 받는다.

## 5. 저장 안전성

중요 파일 저장 절차:

```text
같은 파일시스템의 임시 파일에 전체 기록
→ flush와 필요 시 fsync 동등 처리
→ 형식 재검증
→ 기존 파일 백업
→ 원자 교체
→ 실패 시 원본 유지
→ 복구 가능 상태 기록
```

오류 주입:

- 임시 파일 생성 실패
- 쓰기 중 예외
- flush 실패
- 검증 실패
- 교체 실패
- 백업 실패
- 파일 잠금
- 권한 거부
- 디스크 공간 부족
- 프로세스 강제 종료

각 시나리오에서 사용자 원본과 마지막 정상 상태가 유지돼야 한다.

## 6. XML

- `defName`, 키, 경로, 식별자, 태그와 보호 토큰을 보존한다.
- DTD·외부 엔터티를 금지한다.
- 불필요한 노드 순서·주석·줄바꿈 변경을 피한다.
- 허용된 표시 필드만 번역하고 `Patches` 등 불확정 구조를 자동 번역하지 않는다.
- source change 시 기존 번역과 이전 원문을 보존하되 적용 대상에서 내린다.

## 7. XLSX/RMK

읽기·병합·쓰기에 대해 다음을 확인한다.

- 기존 row와 안정적인 key 대응
- 새 row 삽입과 기존 row 갱신
- styles, comments, extra columns, metadata, relationships 보존
- Required Mods 보존
- unresolved source change의 과거 원문 보존
- 손상 workbook 오류 처리
- 기존 package part 전후 해시 비교

## 8. 번역 API 동등성

실제 외부 API와 실제 키를 사용하지 않는다. fake HTTP handler 또는 loopback 서버로 PowerShell판과 C#판을 캡처한다.

비교 항목:

- 제공자·endpoint·HTTP method
- 모델·timeout·요청 옵션
- system/user prompt
- glossary 삽입과 정렬
- Def Class, Node, 파일·키, 앞뒤 문맥
- batch 크기·항목 순서
- temperature·token 제한·schema
- 보호 토큰·XML 태그·변수·줄바꿈·한국어 조사 문법
- 응답 파싱·검증·후처리
- retry·backoff·rate limit·실패 batch 분할
- partial success·checkpoint·resume

동적 값, request ID, 시각, 비밀정보를 제거한 canonical JSON을 만든다.

```text
baseline/request.canonical.json
candidate/request.canonical.json
baseline/response.fixture.json
candidate/output.json
diff/report.md
```

문자열의 단순 공백 차이도 의미가 있는지 검토한다. 기능적 차이는 승인된 명시적 변경이 아니면 FAIL이다.

## 9. API 실패 fixture

- 400, 401, 403, 408, 429, 500, 502/503
- timeout·연결 끊김·빈 응답·응답 크기 초과
- 잘린 JSON·잘못된 JSON
- 일부 항목 누락·중복·순서 변경
- 보호 토큰 훼손
- 사용자 취소
- 앱 재시작 후 재개

각 시나리오에서 데이터 손실·중복 적용·완료 오표시가 없어야 한다.

## Exit gate

- 기존 파일 read-only open에서 해시 변경 0
- 필수 fixture round-trip 통과
- 저장 실패 주입에서 원본 보존
- XML·XLSX 구조와 메타데이터 보존
- canonical payload 차이 0 또는 승인된 문서화 차이만 존재
- 동일 fake response의 최종 결과 동등
- 보호 토큰 손실 0
- 호환성 보고서와 잔여 제한 완성
