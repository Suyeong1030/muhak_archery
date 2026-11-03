from __future__ import annotations

import asyncio
import math
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

SERVER_DIR = Path(__file__).resolve().parents[1]
if str(SERVER_DIR) not in sys.path:
    sys.path.insert(0, str(SERVER_DIR))

from app.models import PoseFeatures, PoseLandmark
from app.reporting.models import (
    ImpactPoint,
    SensorAvailability,
    SessionReport,
    TimelineSample,
    TrialReport,
)
from app.reporting.pillow_renderer import PillowReportRenderer
from app.reporting.service import SessionReportService


def build_dummy_report() -> SessionReport:
    started = datetime(2026, 8, 3, 10, 30, tzinfo=timezone.utc)
    release_points = {20: 1, 45: 2, 70: 3}
    timeline = []
    for index in range(80):
        trial = 1 if index <= 20 else 2 if index <= 45 else 3
        timeline.append(
            TimelineSample(
                captured_at=started + timedelta(seconds=index * 1.3),
                trial_number=trial,
                phase="RELEASE" if index in release_points else "AIM",
                heart_rate_bpm=round(88 + 8 * math.sin(index / 6) + trial * 2),
                respiration_rate_bpm=round(15 + 2 * math.sin(index / 10), 1),
                stability=min(0.97, 0.67 + index * 0.003 + 0.05 * math.sin(index / 4)),
            )
        )

    reference_pose = _dummy_pose()
    trials = [
        TrialReport(
            trial_number=1, score=8, max_heart_rate_bpm=98, min_stability=0.78,
            release_pose=PoseFeatures(draw_elbow_angle_deg=151.2, shoulder_tilt_deg=4.8, torso_tilt_deg=2.6, anchor_distance=0.19),
            release_landmarks=_dummy_pose(shoulder_shift=0.035, elbow_drop=0.035),
            reference_landmarks=reference_pose,
            impact_point=ImpactPoint(x=-0.34, y=0.18),
            feedback="앵커 이후 어깨 높이가 상승했습니다.",
        ),
        TrialReport(
            trial_number=2, score=8, max_heart_rate_bpm=95, min_stability=0.84,
            release_pose=PoseFeatures(draw_elbow_angle_deg=154.7, shoulder_tilt_deg=3.6, torso_tilt_deg=2.1, anchor_distance=0.17),
            release_landmarks=_dummy_pose(shoulder_shift=0.022, elbow_drop=0.018),
            reference_landmarks=reference_pose,
            impact_point=ImpactPoint(x=0.27, y=-0.13),
            feedback="상체는 안정적이지만 팔꿈치 정렬을 확인하세요.",
        ),
        TrialReport(
            trial_number=3, score=10, max_heart_rate_bpm=92, min_stability=0.91,
            release_pose=PoseFeatures(draw_elbow_angle_deg=158.1, shoulder_tilt_deg=1.9, torso_tilt_deg=1.4, anchor_distance=0.15),
            release_landmarks=_dummy_pose(shoulder_shift=0.008, elbow_drop=0.006),
            reference_landmarks=reference_pose,
            impact_point=None,
            feedback="세 샷 중 가장 안정적인 릴리즈였습니다.",
        ),
    ]
    return SessionReport(
        session_id="demo-session-001",
        created_at=started + timedelta(seconds=104),
        message=(
            "세 번째 샷의 조준 구간이 가장 안정적이었습니다. "
            "릴리즈 순간 어깨선의 흔들림도 점차 줄었습니다. "
            "다음 세션에서는 앵커 이후의 정렬을 유지해 보세요."
        ),
        trials=trials,
        timeline=timeline,
        sensor_availability=SensorAvailability(pose=True, polar_h10=True, respiration=True),
        strengths=["세 번째 샷의 높은 안정성"],
        weaknesses=["초반 어깨선 상승"],
        recommendations=["앵커 이후 어깨 높이 유지"],
    )


def _dummy_pose(
    *,
    shoulder_shift: float = 0.0,
    elbow_drop: float = 0.0,
) -> list[PoseLandmark]:
    points = [(0.5, 0.13)] * 33
    values = {
        0: (0.50, 0.12),
        11: (0.38, 0.27 + shoulder_shift),
        12: (0.62, 0.27),
        13: (0.23, 0.30),
        14: (0.75, 0.31 + elbow_drop),
        15: (0.10, 0.28),
        16: (0.54, 0.18 + elbow_drop),
        23: (0.43, 0.52),
        24: (0.58, 0.52),
        25: (0.41, 0.72),
        26: (0.60, 0.72),
        27: (0.39, 0.91),
        28: (0.62, 0.91),
        29: (0.36, 0.94),
        30: (0.59, 0.94),
        31: (0.43, 0.96),
        32: (0.66, 0.96),
    }
    for index, value in values.items():
        points[index] = value
    return [PoseLandmark(x=x, y=y, visibility=1.0) for x, y in points]


async def main() -> None:
    output_root = SERVER_DIR / "generated" / "reports"
    service = SessionReportService(PillowReportRenderer(), output_root)
    pages = await service.render(build_dummy_report())
    for page in pages:
        print(page.path)


if __name__ == "__main__":
    asyncio.run(main())
