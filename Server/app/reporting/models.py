from __future__ import annotations

from datetime import datetime

from pydantic import BaseModel, Field

from ..models import PoseFeatures, PoseLandmark


class SensorAvailability(BaseModel):
    pose: bool = False
    polar_h10: bool = False
    respiration: bool = False


class TimelineSample(BaseModel):
    captured_at: datetime
    trial_number: int | None = Field(default=None, ge=1)
    phase: str | None = None
    heart_rate_bpm: int | None = None
    rr_interval_ms: float | None = Field(default=None, gt=0)
    respiration_rate_bpm: float | None = None
    stability: float | None = Field(default=None, ge=0, le=1)


class PolarReportSample(BaseModel):
    captured_at: datetime
    heart_rate_bpm: int = Field(ge=20, le=240)
    rr_intervals_ms: list[float] = Field(default_factory=list)


class ImpactPoint(BaseModel):
    """Normalized target coordinates, optional until Unity provides them."""

    x: float = Field(ge=-1, le=1)
    y: float = Field(ge=-1, le=1)


class TrialReport(BaseModel):
    trial_number: int = Field(ge=1)
    score: int | None = Field(default=None, ge=0, le=10)
    release_pose: PoseFeatures | None = None
    release_landmarks: list[PoseLandmark] | None = Field(
        default=None,
        min_length=33,
        max_length=33,
    )
    reference_landmarks: list[PoseLandmark] | None = Field(
        default=None,
        min_length=33,
        max_length=33,
    )
    max_heart_rate_bpm: int | None = None
    min_stability: float | None = Field(default=None, ge=0, le=1)
    impact_point: ImpactPoint | None = None
    feedback: str | None = None
    polar_samples: list[PolarReportSample] = Field(default_factory=list)
    timeline: list[TimelineSample] = Field(default_factory=list)


class SessionReport(BaseModel):
    report_version: int = 1
    session_id: str = Field(min_length=1, max_length=100)
    created_at: datetime
    message: str
    trials: list[TrialReport] = Field(default_factory=list)
    timeline: list[TimelineSample] = Field(default_factory=list)
    sensor_availability: SensorAvailability = Field(default_factory=SensorAvailability)
    strengths: list[str] = Field(default_factory=list)
    weaknesses: list[str] = Field(default_factory=list)
    recommendations: list[str] = Field(default_factory=list)

    @property
    def total_score(self) -> int:
        return sum(trial.score or 0 for trial in self.trials)

    @property
    def average_score(self) -> float:
        scored = [trial.score for trial in self.trials if trial.score is not None]
        return sum(scored) / len(scored) if scored else 0.0
