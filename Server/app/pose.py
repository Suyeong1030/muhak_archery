"""
MediaPipe Pose 랜드마크(33점) -> 양궁 자세 각도(PoseFeatures) 변환.

설계 B안: Flutter(클라이언트)는 MediaPipe로 좌표만 뽑아 보내고,
각도 정의/계산은 서버 한 곳에서 관리한다.

원칙:
- 절대 좌표/픽셀 거리는 쓰지 않는다. 사람 체격/카메라 거리에 따라 깨지기 때문.
- 각도(스케일 불변)와 어깨너비 대비 비율만 사용한다.
- 카메라 정면·오른손잡이를 기본 가정. handedness 로 좌우를 바꾼다.
"""

from __future__ import annotations

import math

from .models import PoseFeatures, PoseLandmark, StanceType

# MediaPipe Pose 랜드마크 인덱스 (33점 규격)
NOSE = 0
LEFT_SHOULDER = 11
RIGHT_SHOULDER = 12
LEFT_ELBOW = 13
RIGHT_ELBOW = 14
LEFT_WRIST = 15
RIGHT_WRIST = 16
LEFT_HIP = 23
RIGHT_HIP = 24
LEFT_ANKLE = 27
RIGHT_ANKLE = 28
LEFT_HEEL = 29
RIGHT_HEEL = 30
LEFT_FOOT_INDEX = 31
RIGHT_FOOT_INDEX = 32

# 뒤꿈치·발끝은 저해상도/원거리에서 신뢰도가 낮다. 이 값 미만이면
# 발 각도(평행성)는 계산하지 않는다(엉터리 값 방지).
_FOOT_VIS_THRESHOLD = 0.8


def _angle_at(a: PoseLandmark, b: PoseLandmark, c: PoseLandmark) -> float:
    """b 를 꼭짓점으로 하는 세 점의 각도(도). 2D(x, y)로 계산."""
    bax = a.x - b.x
    bay = a.y - b.y
    bcx = c.x - b.x
    bcy = c.y - b.y
    dot = bax * bcx + bay * bcy
    mag = math.hypot(bax, bay) * math.hypot(bcx, bcy)
    if mag == 0:
        return 0.0
    cos = max(-1.0, min(1.0, dot / mag))
    return math.degrees(math.acos(cos))


def _line_angle_from_horizontal(a: PoseLandmark, b: PoseLandmark) -> float:
    """두 점을 잇는 선이 수평에서 벗어난 각도(도, 부호 포함)."""
    return math.degrees(math.atan2(b.y - a.y, b.x - a.x))


def _mid(a: PoseLandmark, b: PoseLandmark) -> PoseLandmark:
    return PoseLandmark(x=(a.x + b.x) / 2, y=(a.y + b.y) / 2)


def _distance(a: PoseLandmark, b: PoseLandmark) -> float:
    return math.hypot(b.x - a.x, b.y - a.y)


def hand_draw_strength(
    landmarks: list[PoseLandmark],
    *,
    minimum_ratio: float = 0.5,
    maximum_ratio: float = 2.5,
    visibility_threshold: float = 0.5,
) -> tuple[float, float, bool]:
    """Return (strength, wrist-distance/shoulder-width, valid)."""
    required = (
        landmarks[LEFT_SHOULDER],
        landmarks[RIGHT_SHOULDER],
        landmarks[LEFT_WRIST],
        landmarks[RIGHT_WRIST],
    )
    if any(point.visibility < visibility_threshold for point in required):
        return 0.0, 0.0, False

    shoulder_width = _distance(required[0], required[1])
    if shoulder_width <= 1e-6 or maximum_ratio <= minimum_ratio:
        return 0.0, 0.0, False

    hand_distance = _distance(required[2], required[3])
    ratio = hand_distance / shoulder_width
    strength = (ratio - minimum_ratio) / (maximum_ratio - minimum_ratio)
    return max(0.0, min(1.0, strength)), ratio, True


def landmarks_to_features(
    landmarks: list[PoseLandmark],
    handedness: str = "right",
) -> PoseFeatures:
    """
    33개 랜드마크 -> PoseFeatures(각도).

    stability(안정성)는 한 프레임만으로 계산할 수 없어(시간축 필요)
    여기서는 채우지 않는다. 필요 시 SessionStore 에서 최근 프레임들의
    분산으로 별도 계산한다.
    """
    lm = landmarks

    # 오른손잡이: 활을 왼손, 시위를 오른손으로 당긴다 -> draw arm = 오른팔.
    if handedness == "right":
        draw_shoulder, draw_elbow, draw_wrist = (
            lm[RIGHT_SHOULDER],
            lm[RIGHT_ELBOW],
            lm[RIGHT_WRIST],
        )
    else:
        draw_shoulder, draw_elbow, draw_wrist = (
            lm[LEFT_SHOULDER],
            lm[LEFT_ELBOW],
            lm[LEFT_WRIST],
        )

    draw_elbow_angle_deg = _angle_at(draw_shoulder, draw_elbow, draw_wrist)

    # 어깨선 기울기: 수평 기준. 좌->우 방향이라 부호가 붙는다.
    shoulder_tilt_deg = _line_angle_from_horizontal(
        lm[LEFT_SHOULDER], lm[RIGHT_SHOULDER]
    )

    # 상체 기울기: 골반 중점 -> 어깨 중점 벡터가 수직에서 벗어난 각도.
    shoulder_mid = _mid(lm[LEFT_SHOULDER], lm[RIGHT_SHOULDER])
    hip_mid = _mid(lm[LEFT_HIP], lm[RIGHT_HIP])
    # 이미지 좌표는 y가 아래로 증가하므로 수직 위쪽은 (0, -1).
    torso_tilt_deg = math.degrees(
        math.atan2(shoulder_mid.x - hip_mid.x, hip_mid.y - shoulder_mid.y)
    )

    # 앵커 거리: 당기는 손목~코 거리를 어깨너비로 정규화한 비율(스케일 불변).
    shoulder_width = math.hypot(
        lm[LEFT_SHOULDER].x - lm[RIGHT_SHOULDER].x,
        lm[LEFT_SHOULDER].y - lm[RIGHT_SHOULDER].y,
    )
    anchor_distance = None
    if shoulder_width > 0:
        wrist_to_nose = math.hypot(
            draw_wrist.x - lm[NOSE].x,
            draw_wrist.y - lm[NOSE].y,
        )
        anchor_distance = wrist_to_nose / shoulder_width

    stance = _stance_metrics(lm, shoulder_width, handedness)

    return PoseFeatures(
        draw_elbow_angle_deg=round(draw_elbow_angle_deg, 1),
        shoulder_tilt_deg=round(shoulder_tilt_deg, 1),
        torso_tilt_deg=round(torso_tilt_deg, 1),
        anchor_distance=round(anchor_distance, 3) if anchor_distance is not None else None,
        **stance,
    )


def _stance_metrics(
    lm: list[PoseLandmark],
    shoulder_width: float,
    handedness: str,
) -> dict:
    """
    발(뒤꿈치·발끝) 기반 스탠스 관측값을 계산한다.

    표적축은 카메라 방향을 모르므로 '어깨선 방향'을 프록시로 쓴다
    (궁수는 표적을 향해 옆으로 서므로 어깨선이 표적을 향한다).
    모든 거리는 어깨너비로 정규화해 스케일 불변으로 만든다.
    """
    empty = {
        "stance_width_ratio": None,
        "foot_angle_diff_deg": None,
        "front_back_offset_ratio": None,
    }
    if shoulder_width <= 0:
        return empty

    # 표적축 = 뒷어깨 -> 앞(활)어깨. 오른손잡이는 활=왼팔이므로 앞=왼쪽.
    # 발선도 발목(신뢰도 높음)으로 잡는다. 앞발=표적쪽 발.
    if handedness == "right":
        front_sh, rear_sh = lm[LEFT_SHOULDER], lm[RIGHT_SHOULDER]
        front_ankle, rear_ankle = lm[LEFT_ANKLE], lm[RIGHT_ANKLE]
    else:
        front_sh, rear_sh = lm[RIGHT_SHOULDER], lm[LEFT_SHOULDER]
        front_ankle, rear_ankle = lm[RIGHT_ANKLE], lm[LEFT_ANKLE]

    ax, ay = front_sh.x - rear_sh.x, front_sh.y - rear_sh.y
    axis_len = math.hypot(ax, ay)
    if axis_len == 0:
        return empty
    ax, ay = ax / axis_len, ay / axis_len
    px, py = -ay, ax  # 어깨축에 수직인 방향

    dfx, dfy = front_ankle.x - rear_ankle.x, front_ankle.y - rear_ankle.y
    # 정면 뷰에서 발은 어깨축(좌우) 방향으로 벌어진다 -> 그 성분이 '발 간격'.
    # 어깨축에 수직인 성분이 앞발의 전후 위치 차이(스탠스 유형의 단서).
    width = abs(dfx * ax + dfy * ay) / shoulder_width
    offset = (dfx * px + dfy * py) / shoulder_width

    # 발 각도(평행성)는 뒤꿈치/발끝이 충분히 보일 때만 계산한다.
    foot_angle_diff = None
    l_heel, l_toe = lm[LEFT_HEEL], lm[LEFT_FOOT_INDEX]
    r_heel, r_toe = lm[RIGHT_HEEL], lm[RIGHT_FOOT_INDEX]
    if min(l_heel.visibility, l_toe.visibility, r_heel.visibility,
           r_toe.visibility) >= _FOOT_VIS_THRESHOLD:
        l_angle = _line_angle_from_horizontal(l_heel, l_toe)
        r_angle = _line_angle_from_horizontal(r_heel, r_toe)
        foot_angle_diff = round(abs(l_angle - r_angle), 1)

    return {
        "stance_width_ratio": round(width, 3),
        "foot_angle_diff_deg": foot_angle_diff,
        "front_back_offset_ratio": round(offset, 3),
    }


def classify_stance(
    features: PoseFeatures,
    square_threshold: float = 0.08,
) -> StanceType:
    """
    전후 오프셋 비율로 스탠스 유형을 '분류만' 한다(품질 판정 아님).
    - |offset| < 임계값 : SQUARE
    - 앞발이 뒤로(음수)   : OPEN
    - 앞발이 앞으로(양수)  : CLOSED
    """
    offset = features.front_back_offset_ratio
    if offset is None:
        return StanceType.UNKNOWN
    if abs(offset) < square_threshold:
        return StanceType.SQUARE
    return StanceType.OPEN if offset < 0 else StanceType.CLOSED
