# ARTEMIS `session_report.v1`

서버가 한 세션의 마지막 trial을 완료하고 모든 PNG 파일 생성을 성공한 뒤 Unity WebSocket으로 한 번 전송하는 계약이다.

## 전송 조건

- 동일한 `session_id`에서 설정된 발사 수(현재 3발)가 완료되어야 한다.
- 각 trial의 `RELEASE` 뒤 `SCORE_UPDATED`가 처리되어야 한다.
- 모든 `pages[].url`의 PNG 생성이 완료된 다음 메시지를 전송한다.
- 배열 순서는 `page_index`와 같으며 `0`부터 끊김 없이 증가한다.
- 첫 페이지는 반드시 `summary`다.

## 페이지 종류

| `kind` | `trial_number` | 의미 |
|---|---:|---|
| `summary` | `null` | 세션 종합평과 점수 |
| `sensor` | `null` | 전체 센서 타임라인 |
| `trial` | 1 이상 | 해당 trial의 RELEASE 자세와 점수 |

## URL 처리

- `/`로 시작하면 상대 URL이다. Unity는 WebSocket 서버의 `http://host:port`와 결합한다.
- `http://` 또는 `https://`로 시작하면 그대로 사용한다.
- Unity는 `UnityWebRequestTexture.GetTexture()`로 PNG를 내려받는다.
- 다운로드 실패 시 해당 페이지만 오류 placeholder로 표시하고 나머지 페이지는 유지한다.

## 호환성

- Unity는 알 수 없는 필드를 무시해야 한다.
- 서버는 기존 v1 필드를 제거하거나 의미를 변경하지 않는다.
- 호환되지 않는 변경은 `report_version: 2`와 별도 스키마로 제공한다.
- 센서가 없으면 관련 수치는 PNG에서 `측정 안 함`으로 표현하며 `sensor_availability`가 `false`다.

## Unity 최소 구현

1. `type == "session_report"`를 분기한다.
2. `payload.report_version == 1`인지 확인한다.
3. `pages`를 `page_index` 순으로 정렬한다.
4. 상대 URL을 서버 base URL과 결합한다.
5. PNG를 다운로드해 `Texture2D`로 보관한다.
6. 첫 이미지를 World Space Canvas의 `RawImage`에 표시한다.
7. 좌우 입력으로 현재 texture index를 변경한다.

정식 구조는 `session_report.v1.schema.json`, 실제 예시는 `session_report.v1.example.json`을 기준으로 한다.
