# Native XML/XLSX 코드 지침

- XML은 DTD와 외부 엔터티를 금지하고 문서·압축 항목 크기 제한을 유지한다.
- Def 추출은 명시적 허용 목록을 우선하며 내부 식별자를 기본 제외한다. 규칙 변경에는 허용·거부 fixture가 필요하다.
- RMK XLSX 갱신은 비대상 시트·스타일·주석·추가 열과 `Required Mods`를 보존해야 한다.
- 파일 갱신은 같은 디렉터리의 임시 파일, flush, 재개방 검증, 원자 교체와 지속 가능한 백업을 사용한다.
- 이 소스는 `src/RimWorldAiTranslator.Native`가 링크해 .NET 8로 빌드한다. Core와 중복 구현을 추가하지 않는다.
