from datetime import datetime, timezone
from enum import StrEnum
from typing import Annotated, Literal
from uuid import UUID, uuid4

from pydantic import BaseModel, Field


def utc_now() -> datetime:
    return datetime.now(timezone.utc)


class ArcheryState(StrEnum):
    SETUP = "SETUP"
    DRAW = "DRAW"
    ANCHOR = "ANCHOR"
    AIM = "AIM"
    RELEASE = "RELEASE"
    FOLLOW_THROUGH = "FOLLOW_THROUGH"
    RESULT = "RESULT"


class StanceType(StrEnum):
    SQUARE = "SQUARE"
    OPEN = "OPEN"
    CLOSED = "CLOSED"
    UNKNOWN = "UNKNOWN"


class PoseFeatures(BaseModel):
    draw_elbow_angle_deg: float | None = None
    shoulder_tilt_deg: float | None = None
    torso_tilt_deg: float | None = None
    anchor_distance: Annotated[float | None, Field(ge=0)] = None
    stability: Annotated[float | None, Field(ge=0, le=1)] = None

    # --- 스탠스(발) 관측값 ---
    # 어깨너비 대비 발 간격 비율(표적축에 수직인 방향으로 투영).
    stance_width_ratio: Annotated[float | None, Field(ge=0)] = None
    # 양발 방향각 차이(평행성). 클수록 비대칭.
    foot_angle_diff_deg: float | None = None
    # 표적축 기준 앞발-뒷발 전후 오프셋(어깨너비로 정규화, 부호 포함).
    # 음수=앞발이 뒤로(OPEN 경향), 양수=앞발이 앞으로(CLOSED 경향).
    front_back_offset_ratio: float | None = None


class PoseLandmark(BaseModel):
    """MediaPipe Pose 랜드마크 한 점. 좌표는 정규화(0~1) 값을 기대한다."""

    x: float
    y: float
    z: float = 0.0
    visibility: Annotated[float, Field(ge=0, le=1)] = 1.0


class PoseLandmarksSample(BaseModel):
    """
    Flutter(MediaPipe)에서 넘어오는 원본 좌표 프레임.
    서버가 좌표 -> 각도(PoseFeatures) 변환을 담당한다(설계 B안).
    """

    session_id: str = Field(min_length=1, max_length=100)
    captured_at: datetime = Field(default_factory=utc_now)
    handedness: Literal["right", "left"] = "right"
    # MediaPipe Pose 는 33개 랜드마크를 반환한다.
    landmarks: list[PoseLandmark] = Field(min_length=33, max_length=33)


class UnityFrame(BaseModel):
    frame_id: UUID = Field(default_factory=uuid4)
    session_id: str = Field(min_length=1, max_length=100)
    state: ArcheryState
    captured_at: datetime = Field(default_factory=utc_now)
    pose: PoseFeatures | None = None
    draw_length: Annotated[float | None, Field(ge=0)] = None
    arrow_score: Annotated[int | None, Field(ge=0, le=10)] = None


class PolarH10Sample(BaseModel):
    session_id: str = Field(min_length=1, max_length=100)
    captured_at: datetime = Field(default_factory=utc_now)
    device_id: str | None = Field(default=None, max_length=100)
    heart_rate_bpm: Annotated[int, Field(ge=20, le=240)]
    rr_intervals_ms: list[Annotated[float, Field(gt=0)]] = Field(
        default_factory=list,
        max_length=20,
    )


class RespirationSample(BaseModel):
    session_id: str = Field(min_length=1, max_length=100)
    captured_at: datetime = Field(default_factory=utc_now)
    respiratory_rate_bpm: Annotated[float, Field(gt=0, le=80)]
    phase: str
    normalized_wave: Annotated[float, Field(ge=-1, le=1)]


class SyncedPolarData(BaseModel):
    heart_rate_bpm: int
    rr_intervals_ms: list[float]
    captured_at: datetime
    age_ms: int
    device_id: str | None = None


class Feedback(BaseModel):
    type: Literal["voice", "alert", "report", "none"]
    message: str | None = None
    code: str | None = None
    cooldown_ms: int = 0


class UnityResponse(BaseModel):
    frame_id: UUID
    session_id: str
    state: ArcheryState
    server_time: datetime = Field(default_factory=utc_now)
    polar_h10: SyncedPolarData | None = None
    feedback: Feedback


class SensorAccepted(BaseModel):
    accepted: bool = True
    sensor: Literal["polar_h10"] = "polar_h10"
    session_id: str
    captured_at: datetime


class HealthResponse(BaseModel):
    status: Literal["ok"] = "ok"


class WebSocketIncoming(BaseModel):
    type: Literal["unity_frame"]
    payload: UnityFrame
