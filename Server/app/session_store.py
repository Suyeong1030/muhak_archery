import asyncio
from collections import Counter, defaultdict, deque
from dataclasses import dataclass, field
from datetime import datetime

from .models import (
    PolarH10Sample,
    PoseFeatures,
    PoseLandmark,
    PoseLandmarksSample,
    RespirationSample,
)
from .reporting.models import TrialReport


@dataclass
class ShotTimelineFrame:
    captured_at: datetime
    state: str
    heart_rate_bpm: int | None = None
    stability: float | None = None
    respiration_rate_bpm: float | None = None


@dataclass
class ShotAccumulator:
    """한 발(DRAW~RELEASE) 동안의 관측을 누적한다. RELEASE에서 리포트로 종합."""

    frames: int = 0
    states: set[str] = field(default_factory=set)
    issue_counts: Counter = field(default_factory=Counter)
    max_heart_rate: int | None = None
    min_stability: float | None = None
    stance_type: str | None = None
    # 대표 각도(가장 최근 값). 앵커/에임 구간에서 갱신되는 게 보통.
    draw_elbow_angle_deg: float | None = None
    torso_tilt_deg: float | None = None
    shoulder_tilt_deg: float | None = None
    anchor_distance: float | None = None
    release_landmarks: list[PoseLandmark] | None = None
    polar_samples: list[PolarH10Sample] = field(default_factory=list)
    timeline: list[ShotTimelineFrame] = field(default_factory=list)


@dataclass
class PoseTrial:
    state: str = "ACTIVE_DRAW"
    draw_strength: float = 0.0
    polar_samples: list[PolarH10Sample] = field(default_factory=list)


class SessionStore:
    """Small in-memory buffer. Replace behind this boundary for multi-worker deployment."""

    def __init__(self, max_sensor_samples: int = 500) -> None:
        self._polar: dict[str, deque[PolarH10Sample]] = defaultdict(
            lambda: deque(maxlen=max_sensor_samples)
        )
        self._pose: dict[str, deque[PoseLandmarksSample]] = defaultdict(
            lambda: deque(maxlen=max_sensor_samples)
        )
        self._respiration: dict[str, deque[RespirationSample]] = defaultdict(
            lambda: deque(maxlen=max_sensor_samples)
        )
        self._last_feedback_at: dict[tuple[str, str], datetime] = {}
        self._shots: dict[str, ShotAccumulator] = {}
        self._completed_trials: dict[str, list[TrialReport]] = defaultdict(list)
        self._pose_trials: dict[str, PoseTrial] = {}
        self._lock = asyncio.Lock()

    async def start_pose_trial(self, session_id: str) -> PoseTrial:
        """Enable Flutter pose frames as the draw source for one shot."""
        async with self._lock:
            trial = PoseTrial()
            self._pose_trials[session_id] = trial
            return PoseTrial(trial.state, trial.draw_strength, list(trial.polar_samples))

    async def update_pose_trial(
        self,
        session_id: str,
        draw_strength: float,
    ) -> bool:
        """Store the latest strength and return whether the trial is active."""
        async with self._lock:
            trial = self._pose_trials.get(session_id)
            if trial is None or trial.state != "ACTIVE_DRAW":
                return False
            trial.draw_strength = max(0.0, min(1.0, draw_strength))
            return True

    async def fire_pose_trial(self, session_id: str) -> float | None:
        """Latch one Nano press and return the strength used for release."""
        async with self._lock:
            trial = self._pose_trials.get(session_id)
            if trial is None or trial.state != "ACTIVE_DRAW":
                return None
            trial.state = "FIRED"
            return trial.draw_strength

    async def finish_pose_trial(self, session_id: str) -> PoseTrial | None:
        """Close a Flutter-managed trial regardless of whether it fired."""
        async with self._lock:
            trial = self._pose_trials.pop(session_id, None)
            if trial is None:
                return None
            return PoseTrial(trial.state, trial.draw_strength, list(trial.polar_samples))

    async def pose_trial(self, session_id: str) -> PoseTrial | None:
        async with self._lock:
            trial = self._pose_trials.get(session_id)
            if trial is None:
                return None
            return PoseTrial(trial.state, trial.draw_strength, list(trial.polar_samples))

    async def pose_trial_active(self, session_id: str) -> bool:
        async with self._lock:
            return session_id in self._pose_trials

    async def clear_completed_trials(
        self,
        session_id: str,
    ) -> None:
        async with self._lock:
            self._completed_trials.pop(
                session_id,
                None,
            )

    async def shot_record(
        self,
        session_id: str,
        state: str,
        *,
        features: PoseFeatures | None,
        issue_messages: list[str],
        heart_rate: int | None,
        stance_type: str | None,
        captured_at: datetime,
        landmarks: list[PoseLandmark] | None = None,
        respiration_rate_bpm: float | None = None,
    ) -> None:
        """
        DRAW~RELEASE 프레임을 누적한다. 샷은 DRAW 진입 시 시작(초기화)되고,
        DRAW로 시작하지 않은 프레임(누적기 없음)은 무시한다.
        """
        async with self._lock:
            acc = self._shots.get(session_id)
            if state == "DRAW" and acc is None:
                acc = ShotAccumulator()
                self._shots[session_id] = acc
            if acc is None:
                return

            acc.frames += 1
            acc.states.add(state)
            acc.timeline.append(
                ShotTimelineFrame(
                    captured_at=captured_at,
                    state=state,
                    heart_rate_bpm=heart_rate,
                    stability=features.stability if features else None,
                    respiration_rate_bpm=respiration_rate_bpm,
                )
            )
            for msg in issue_messages:
                acc.issue_counts[msg] += 1
            if heart_rate is not None:
                acc.max_heart_rate = (
                    heart_rate if acc.max_heart_rate is None
                    else max(acc.max_heart_rate, heart_rate)
                )
            if stance_type and stance_type != "UNKNOWN":
                acc.stance_type = stance_type
            if features is not None:
                if features.stability is not None:
                    acc.min_stability = (
                        features.stability if acc.min_stability is None
                        else min(acc.min_stability, features.stability)
                    )
                if features.draw_elbow_angle_deg is not None:
                    acc.draw_elbow_angle_deg = features.draw_elbow_angle_deg
                if features.torso_tilt_deg is not None:
                    acc.torso_tilt_deg = features.torso_tilt_deg
                if features.shoulder_tilt_deg is not None:
                    acc.shoulder_tilt_deg = features.shoulder_tilt_deg
                if features.anchor_distance is not None:
                    acc.anchor_distance = features.anchor_distance
            if state == "RELEASE" and landmarks:
                acc.release_landmarks = list(landmarks)

    async def shot_pop(self, session_id: str) -> ShotAccumulator | None:
        """샷을 종료하고 누적 결과를 반환한다(제거). RELEASE에서 호출."""
        async with self._lock:
            return self._shots.pop(session_id, None)

    async def attach_trial_polar_samples(
        self,
        session_id: str,
        samples: list[PolarH10Sample],
    ) -> bool:
        """Freeze the app-bounded Polar samples into the current shot."""
        async with self._lock:
            accumulator = self._shots.get(session_id)
            if accumulator is None:
                return False
            accumulator.polar_samples = list(samples)
            return True

    async def add_completed_trial(
        self,
        session_id: str,
        trial: TrialReport,
    ) -> list[TrialReport]:
        async with self._lock:
            trials = self._completed_trials[session_id]
            existing = next(
                (
                    index
                    for index, item in enumerate(trials)
                    if item.trial_number == trial.trial_number
                ),
                None,
            )
            if existing is None:
                trials.append(trial)
            else:
                trials[existing] = trial
            trials.sort(key=lambda item: item.trial_number)
            return list(trials)

    async def completed_trials(self, session_id: str) -> list[TrialReport]:
        async with self._lock:
            return list(self._completed_trials.get(session_id, ()))
        
    async def clear_completed_trials(
        self,
        session_id: str,
    ) -> None:
        """
        3발 최종 리포트 생성 후 완료 Trial 목록만 비웁니다.

        센서 데이터는 유지하므로 같은 Unity 실행 중
        다음 3발을 계속 측정할 수 있습니다.
        """
        async with self._lock:
            self._completed_trials.pop(
                session_id,
                None,
            )


    async def reset_session(
        self,
        session_id: str,
        *,
        clear_sensors: bool = True,
    ) -> None:
        """
        Unity가 새로 시작될 때 해당 session_id의
        서버 측 세션 데이터를 모두 초기화합니다.
        """
        async with self._lock:
            # 진행 중인 한 발 데이터
            self._shots.pop(
                session_id,
                None,
            )
            self._pose_trials.pop(session_id, None)

            # 이전 완료 Trial
            self._completed_trials.pop(
                session_id,
                None,
            )

            # 이전 Feedback cooldown
            feedback_keys = [
                key
                for key in self._last_feedback_at
                if key[0] == session_id
            ]

            for key in feedback_keys:
                self._last_feedback_at.pop(
                    key,
                    None,
                )

            if clear_sensors:
                self._polar.pop(
                    session_id,
                    None,
                )

                self._pose.pop(
                    session_id,
                    None,
                )

                self._respiration.pop(
                    session_id,
                    None,
                )
            

    async def add_polar(self, sample: PolarH10Sample) -> None:
        async with self._lock:
            self._polar[sample.session_id].append(sample)
            trial = self._pose_trials.get(sample.session_id)
            if trial is not None:
                trial.polar_samples.append(sample)

    async def add_pose(self, sample: PoseLandmarksSample) -> None:
        async with self._lock:
            self._pose[sample.session_id].append(sample)

    async def add_respiration(self, sample: RespirationSample) -> None:
        async with self._lock:
            self._respiration[sample.session_id].append(sample)

    async def nearest_pose(
        self,
        session_id: str,
        captured_at: datetime,
        window_seconds: float,
    ) -> tuple[PoseLandmarksSample, int] | None:
        async with self._lock:
            samples = tuple(self._pose.get(session_id, ()))

        if not samples:
            return None
        sample = min(
            samples, key=lambda item: abs((item.captured_at - captured_at).total_seconds())
        )
        age_ms = round(abs((captured_at - sample.captured_at).total_seconds()) * 1000)
        if age_ms > window_seconds * 1000:
            return None
        return sample, age_ms

    async def nearest_polar(
        self,
        session_id: str,
        captured_at: datetime,
        window_seconds: float,
    ) -> tuple[PolarH10Sample, int] | None:
        async with self._lock:
            samples = tuple(self._polar.get(session_id, ()))

        if not samples:
            return None
        sample = min(samples, key=lambda item: abs((item.captured_at - captured_at).total_seconds()))
        age_ms = round(abs((captured_at - sample.captured_at).total_seconds()) * 1000)
        if age_ms > window_seconds * 1000:
            return None
        return sample, age_ms

    async def nearest_respiration(
        self,
        session_id: str,
        captured_at: datetime,
        window_seconds: float,
    ) -> tuple[RespirationSample, int] | None:
        async with self._lock:
            samples = tuple(self._respiration.get(session_id, ()))

        if not samples:
            return None
        sample = min(
            samples,
            key=lambda item: abs((item.captured_at - captured_at).total_seconds()),
        )
        age_ms = round(abs((captured_at - sample.captured_at).total_seconds()) * 1000)
        if age_ms > window_seconds * 1000:
            return None
        return sample, age_ms

    async def feedback_allowed(
        self,
        session_id: str,
        state: str,
        now: datetime,
        cooldown_seconds: float,
    ) -> bool:
        key = (session_id, state)
        async with self._lock:
            previous = self._last_feedback_at.get(key)
            if previous and (now - previous).total_seconds() < cooldown_seconds:
                return False
            self._last_feedback_at[key] = now
            return True
