# EdoProDeckImageAndroid

덱 리스트 이미지 생성기 (Android).

> **빌드 준비 — `_shared` 폴더가 필요합니다**
> 이 저장소는 `..\_shared\DeckImageSkia\DeckImageSkiaRenderer.cs` 를 **링크 컴파일**합니다.
> 클론했다면 형제 폴더에 `_shared` 를 두세요: `<작업폴더>\EdoProDeckImageAndroid` + `<작업폴더>\_shared\DeckImageSkia\DeckImageSkiaRenderer.cs`
> (배포 백업 ZIP 에는 `_shared/` 가 들어 있어 그대로 풀면 바로 빌드됩니다.)

## v1.8 (2026-11-08)

- 이름 변경: **EdoProDeckImageAndroid** (앱 표시명 "EDOPro 덱 이미지", 패키지는 그대로)
- 이미지 폴백: 같은 ID → **cdb 원본 ID(alias)** → **cdb 영문 이름 검색** → Yugipedia
- **cards.cdb / cards-en.cdb 내장**(assets → files/cdb 1회 추출) + 기기 EDOPro cdb 함께 사용
- 여러 이미지 폴더(`;` 구분, "찾은 폴더 모두 사용") · 3회 재시도 · HTML 오류 거름
