# Fixture 지침

- 이 디렉터리에는 합성 데이터나 재배포 가능한 최소 fixture만 둔다. 실제 사용자 모드·번역·API 키를 복사하지 않는다.
- fixture는 결정적이어야 하고 네트워크, Steam 설치 위치와 `%LOCALAPPDATA%` 실제 상태에 의존하지 않는다.
- 번역 안전성 테스트에는 Keyed, DefInjected, 허용/거부 Def 필드, 중복 키, 토큰, 원문 변경, 손상 JSON과 RMK XLSX 왕복 사례를 포함한다.
- 테스트 출력은 임시 디렉터리에 만들고 원본 fixture는 읽기 전용으로 사용한다.
