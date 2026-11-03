import asyncio
import json
import os
import time
from contextlib import asynccontextmanager
from datetime import datetime, timezone
from pathlib import Path

from fastapi import (
    APIRouter,
    FastAPI,
    Request,
    WebSocket,
    WebSocketDisconnect,
)
from fastapi.middleware.cors import CORSMiddleware
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel, ValidationError

from .config import get_settings
from .bow_tcp import BowTcpServer
from .bow_ble import BowBleReceiver
from .connections import UnityConnectionManager
from .feedback import FeedbackEngine
from .llm import SessionContext, ShotContext
from .models import (
    ArcheryState,
    HealthResponse,
    PolarH10Sample,
    PoseFeatures,
    PoseLandmarksSample,
    PoseLandmark,
    RespirationSample,
    SensorAccepted,
    SyncedPolarData,
    UnityFrame,
    UnityResponse,
    WebSocketIncoming,
    utc_now,
)
from .pose import (
    classify_stance,
    hand_draw_strength,
    landmarks_to_features,
)
from .session_store import SessionStore
from .reporting.models import (
    PolarReportSample,
    SensorAvailability,
    SessionReport,
    TimelineSample,
    TrialReport,
)
from .reporting.contract import (
    ReportPagePayload,
    ReportSensorAvailability,
    SessionReportEnvelope,
    SessionReportPayload,
)
from .reporting.pillow_renderer import PillowReportRenderer
from .reporting.service import SessionReportService


_pose_debug_log = os.environ.get(
    "ARTEMIS_POSE_DEBUG_LOG"
)

_SHOT_STATES = {
    ArcheryState.DRAW,
    ArcheryState.ANCHOR,
    ArcheryState.AIM,
    ArcheryState.RELEASE,
}

# RELEASE 후 타겟 충돌과 SCORE_UPDATED를 기다리는 최대 시간입니다.
_SCORE_WAIT_SECONDS = 2.0


class BowInputSample(BaseModel):
    """
    Arduino 또는 테스트 도구가 서버로 보내는 활 입력입니다.
    sequence와 captured_at은 서버에서 Unity용 값으로 다시 부여합니다.
    """

    session_id: str
    button_pressed: bool
    draw_value: float
    draw_valid: bool = True


class BowShotTestRequest(BaseModel):
    """
    Swagger에서 한 번의 요청으로 버튼 ON/OFF를 시험하기 위한 입력입니다.
    """

    session_id: str = "practice-001"
    draw_value: float = 400.0
    draw_valid: bool = True
    hold_seconds: float = 0.15


class PoseTrialRequest(BaseModel):
    session_id: str = "practice-001"


def _load_reference_landmarks(path: Path) -> list[PoseLandmark] | None:
    if not path.exists():
        return None
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
        landmarks = [PoseLandmark.model_validate(item) for item in payload["landmarks"]]
        return landmarks if len(landmarks) == 33 else None
    except (KeyError, TypeError, ValueError, json.JSONDecodeError) as exc:
        print(f"[report] 전문가 기준 포즈 로드 실패: {exc}")
        return None


async def _finalize_session_report(
    app: FastAPI,
    session_id: str,
    trials: list[TrialReport],
) -> None:
    scored = [trial for trial in trials if trial.score is not None]
    best = max(scored, key=lambda item: item.score or 0) if scored else None
    fallback_message = (
        f"{best.trial_number}번 샷에서 최고 점수 {best.score}점을 기록했습니다. "
        "Trial별 릴리즈 자세와 센서 흐름을 함께 확인하세요."
        if best
        else "3발 세션이 완료되었습니다. Trial별 자세와 센서 흐름을 확인하세요."
    )
    session_context = SessionContext(
        session_id=session_id,
        trials=[
            {
                "trial_number": trial.trial_number,
                "score": trial.score,
                "release_pose": (
                    trial.release_pose.model_dump(mode="json")
                    if trial.release_pose
                    else None
                ),
                "min_stability": trial.min_stability,
                "polar_h10": [
                    sample.model_dump(mode="json")
                    for sample in trial.polar_samples
                ],
            }
            for trial in trials
        ],
    )
    llm_payload = {
        "session_id": session_context.session_id,
        "trials": session_context.trials,
    }
    polar_sample_count = sum(len(trial.polar_samples) for trial in trials)

    print(
        "\n========================================\n"
        "[Artemis][FINAL_SESSION] generation started\n"
        f"session_id: {session_id}\n"
        f"trial_count: {len(trials)}\n"
        f"polar_sample_count: {polar_sample_count}\n"
        f"llm_payload_characters: {len(json.dumps(llm_payload, ensure_ascii=False))}\n"
        "stage: LLM_REQUEST\n"
        "========================================",
        flush=True,
    )

    llm_started_at = time.perf_counter()
    session_result = await app.state.feedback.build_session_report(
        session_context,
        fallback_message,
    )
    message = session_result.message or fallback_message
    llm_elapsed = time.perf_counter() - llm_started_at

    print(
        "\n========================================\n"
        "[Artemis][FINAL_SESSION] LLM response\n"
        f"session_id: {session_id}\n"
        f"source: {session_result.source}\n"
        f"elapsed_seconds: {llm_elapsed:.2f}\n"
        f"error: {session_result.error or '(none)'}\n"
        f"message: {message}\n"
        "stage: LLM_COMPLETE\n"
        "========================================",
        flush=True,
    )
    timeline = [sample for trial in trials for sample in trial.timeline]
    report = SessionReport(
        session_id=session_id,
        created_at=utc_now(),
        message=message,
        trials=trials,
        timeline=timeline,
        sensor_availability=SensorAvailability(
            pose=any(trial.release_landmarks for trial in trials),
            polar_h10=any(trial.max_heart_rate_bpm for trial in trials),
            respiration=any(
                sample.respiration_rate_bpm is not None
                for sample in timeline
            ),
        ),
        strengths=["후반 trial의 수행 결과를 유지했습니다."],
        weaknesses=["Trial별 릴리즈 자세 편차를 확인하세요."],
        recommendations=["기준 자세와 겹친 관절 차이를 다음 세션에서 줄여보세요."],
    )
    print(
        f"[Artemis][FINAL_SESSION] PNG_RENDER_START session_id={session_id}",
        flush=True,
    )
    pages = await app.state.report_service.render(report)
    print(
        "[Artemis][FINAL_SESSION] PNG_RENDER_COMPLETE "
        f"session_id={session_id} pages={[page.path.name for page in pages]}",
        flush=True,
    )
    public_root = app.state.settings.report_public_path.rstrip("/")
    geometry = app.state.report_service.theme.geometry
    page_payload = [
        ReportPagePayload(
            page_index=index,
            kind=page.kind,
            trial_number=page.trial_number,
            url=f"{public_root}/{session_id}/{page.path.name}",
            width=geometry.canvas_width,
            height=geometry.canvas_height,
        )
        for index, page in enumerate(pages)
    ]
    envelope = SessionReportEnvelope(
        payload=SessionReportPayload(
            session_id=session_id,
            message=report.message,
            shot_count=len(trials),
            total_score=report.total_score,
            average_score=report.average_score,
            strengths=report.strengths,
            weaknesses=report.weaknesses,
            recommendations=report.recommendations,
            sensor_availability=ReportSensorAvailability(
                **report.sensor_availability.model_dump()
            ),
            pages=page_payload,
        )
    )
    envelope_data = envelope.model_dump(mode="json")
    delivered_connections = await app.state.connections.broadcast(
        session_id,
        envelope_data,
    )

    if delivered_connections > 0:
        await app.state.store.clear_completed_trials(session_id)
        app.state.pending_session_reports.pop(session_id, None)
    else:
        app.state.pending_session_reports[session_id] = envelope_data

    print(
        "\n"
        "========================================\n"
        "[Artemis][FINAL_SESSION] Unity delivery result\n"
        f"session_id: {session_id}\n"
        f"shot_count: {len(trials)}\n"
        f"delivered_connections: {delivered_connections}\n"
        f"queued_for_reconnect: {delivered_connections == 0}\n"
        "stage: UNITY_DELIVERY_COMPLETE\n"
        "========================================"
        , flush=True
    )

async def _finalize_shot(
    app: FastAPI,
    session_id: str,
    *,
    current_score: int | None = None,
    total_score: int | None = None,
    shot_count: int | None = None,
) -> None:
    """
    RELEASE까지 누적한 한 발 데이터를 꺼내 리포트를 생성하고,
    같은 session_id의 Unity WebSocket으로 전송합니다.
    """
    accumulator = await app.state.store.shot_pop(
        session_id
    )

    if accumulator is None:
        print(
            "\n"
            "========================================\n"
            "[Shot Report 생성 중단]\n"
            f"session_id: {session_id}\n"
            "reason: 누적된 DRAW 샷이 없습니다.\n"
            "========================================"
        )
        return

    trial_max_heart_rate = max(
        (
            sample.heart_rate_bpm
            for sample in accumulator.polar_samples
        ),
        default=accumulator.max_heart_rate or 0,
    ) or None

    context = ShotContext(
        heart_rate_bpm=trial_max_heart_rate,
        stance_type=accumulator.stance_type,
        issues=[
            message
            for message, _ in
            accumulator.issue_counts.most_common(3)
        ],
        phases=[
            state
            for state in (
                "DRAW",
                "ANCHOR",
                "AIM",
                "RELEASE",
            )
            if state in accumulator.states
        ],
        min_stability=accumulator.min_stability,
        frames=accumulator.frames,
        arrow_score=current_score,
        total_score=total_score,
        shot_count=shot_count,
    )

    report_result = (
        await app.state.feedback.build_shot_report(
            context
        )
    )

    status_label = (
        "성공"
        if report_result.source == "openai"
        else "Fallback"
    )

    print(
        "\n"
        "========================================\n"
        f"[LLM Shot Report {status_label}]\n"
        f"session_id: {session_id}\n"
        f"source: {report_result.source}\n"
        f"phases: {context.phases}\n"
        f"current_score: {current_score}\n"
        f"total_score: {total_score}\n"
        f"shot_count: {shot_count}\n"
        f"message: {report_result.message}\n"
        + (
            f"error: {report_result.error}\n"
            if report_result.error
            else ""
        )
        + "========================================"
    )

    await app.state.connections.broadcast(
        session_id,
        {
            "type": "shot_report",
            "payload": {
                "session_id": session_id,
                "code": "SHOT_REPORT",
                "message": report_result.message,
                "source": report_result.source,
                "phases": context.phases,
                "current_score": current_score,
                "total_score": total_score,
                "shot_count": shot_count,
            },
        },
    )

    existing_trials = await app.state.store.completed_trials(session_id)
    trial_number = shot_count or (len(existing_trials) + 1)
    polar_samples = [
        PolarReportSample(
            captured_at=sample.captured_at,
            heart_rate_bpm=sample.heart_rate_bpm,
            rr_intervals_ms=list(sample.rr_intervals_ms),
        )
        for sample in accumulator.polar_samples
    ]
    polar_max_heart_rate = trial_max_heart_rate

    timeline = [
        TimelineSample(
            captured_at=item.captured_at,
            trial_number=trial_number,
            phase=item.state,
            heart_rate_bpm=item.heart_rate_bpm,
            respiration_rate_bpm=item.respiration_rate_bpm,
            stability=item.stability,
        )
        for item in accumulator.timeline
    ]
    timeline.extend(
        TimelineSample(
            captured_at=sample.captured_at,
            trial_number=trial_number,
            heart_rate_bpm=sample.heart_rate_bpm,
            rr_interval_ms=(
                sum(sample.rr_intervals_ms) / len(sample.rr_intervals_ms)
                if sample.rr_intervals_ms
                else None
            ),
        )
        for sample in polar_samples
    )
    timeline.sort(key=lambda item: item.captured_at)

    trial = TrialReport(
        trial_number=trial_number,
        score=current_score,
        release_pose=PoseFeatures(
            draw_elbow_angle_deg=accumulator.draw_elbow_angle_deg,
            shoulder_tilt_deg=accumulator.shoulder_tilt_deg,
            torso_tilt_deg=accumulator.torso_tilt_deg,
            anchor_distance=accumulator.anchor_distance,
            stability=accumulator.min_stability,
        ),
        release_landmarks=accumulator.release_landmarks,
        reference_landmarks=app.state.reference_landmarks,
        max_heart_rate_bpm=polar_max_heart_rate,
        min_stability=accumulator.min_stability,
        feedback=report_result.message,
        polar_samples=polar_samples,
        timeline=timeline,
    )
    trials = await app.state.store.add_completed_trial(
    session_id,
    trial,)

    required_shot_count = (
        app.state.settings.session_report_shot_count
    )

    print(
        "\n"
        "========================================\n"
        "[Session Trial 누적]\n"
        f"session_id: {session_id}\n"
        f"current_trials: {len(trials)}\n"
        f"required_trials: {required_shot_count}\n"
        "========================================"
    )

    if len(trials) == required_shot_count:
        await _finalize_session_report(
            app,
            session_id,
            trials,
        )


async def _reset_unity_session(
    app: FastAPI,
    session_id: str,
) -> None:
    """
    Unity의 새로운 Play 실행을 시작할 때
    해당 session_id에 남아 있는 서버 측 상태를 초기화합니다.
    """

    # RELEASE 후 SCORE_UPDATED를 기다리는 Task 취소
    pending_task = (
        app.state.pending_report_tasks.pop(
            session_id,
            None,
        )
    )

    if (
        pending_task is not None
        and not pending_task.done()
    ):
        pending_task.cancel()

    # Trial, 진행 중 Shot, 센서, Feedback cooldown 초기화
    await app.state.store.reset_session(
        session_id,
        clear_sensors=True,
    )

    app.state.finished_pose_trials.discard(session_id)
    app.state.pending_trial_scores.pop(session_id, None)
    app.state.pending_session_reports.pop(session_id, None)

    # Arduino bow input sequence도 새로 시작
    app.state.bow_input_sequences.pop(
        session_id,
        None,
    )

    print(
        "\n"
        "========================================\n"
        "[Unity Session Reset 완료]\n"
        f"session_id: {session_id}\n"
        "진행 중 Shot: 초기화\n"
        "완료 Trial: 초기화\n"
        "센서 Buffer: 초기화\n"
        "대기 중 Report Task: 초기화\n"
        "========================================"
    )

async def _finalize_after_score_timeout(
    app: FastAPI,
    session_id: str,
) -> None:
    """
    RELEASE 뒤 SCORE_UPDATED가 오지 않더라도 리포트가 유실되지 않도록
    제한시간 후 점수 없는 리포트를 생성합니다.
    """
    try:
        await asyncio.sleep(_SCORE_WAIT_SECONDS)

        current_task = asyncio.current_task()
        registered_task = (
            app.state.pending_report_tasks.get(
                session_id
            )
        )

        if registered_task is not current_task:
            return

        app.state.pending_report_tasks.pop(
            session_id,
            None,
        )

        if session_id not in app.state.finished_pose_trials:
            print(
                "\n"
                "========================================\n"
                "[Trial completion waiting]\n"
                f"session_id: {session_id}\n"
                "reason: the app has not finished the trial yet.\n"
                "========================================"
            )
            return

        print(
            "\n"
            "========================================\n"
            "[Score 대기 시간 초과]\n"
            f"session_id: {session_id}\n"
            "점수 없이 Shot Report를 생성합니다.\n"
            "========================================"
        )

        await _complete_finished_trial(
            app,
            session_id,
            allow_missing_score=True,
        )

    except asyncio.CancelledError:
        return


async def _complete_finished_trial(
    app: FastAPI,
    session_id: str,
    *,
    allow_missing_score: bool = False,
) -> bool:
    """Finalize one fired shot only after Flutter has closed its trial."""
    if session_id not in app.state.finished_pose_trials:
        return False

    score = app.state.pending_trial_scores.get(session_id)
    if score is None and not allow_missing_score:
        return False

    # Remove the completion gate before awaiting report generation so a late
    # duplicate SCORE_UPDATED cannot add the same trial twice.
    app.state.finished_pose_trials.discard(session_id)
    app.state.pending_trial_scores.pop(session_id, None)

    await _finalize_shot(
        app,
        session_id,
        current_score=(score or {}).get("current_score"),
        total_score=(score or {}).get("total_score"),
        shot_count=(score or {}).get("shot_count"),
    )
    return True


def _normalize_timestamp(
    value: datetime,
) -> datetime:
    """
    timezone이 없는 시각은 UTC로 간주하고,
    timezone이 있으면 UTC로 변환합니다.
    """
    if value.tzinfo is None:
        return value.replace(
            tzinfo=timezone.utc
        )

    return value.astimezone(
        timezone.utc
    )


def create_app() -> FastAPI:
    settings = get_settings()
    settings.report_output_dir.mkdir(parents=True, exist_ok=True)

    def pose_draw_message(
        session_id: str,
        action: str,
        *,
        draw_strength: float = 0.0,
        draw_valid: bool = True,
        hand_distance_ratio: float | None = None,
    ) -> dict:
        return {
            "type": "pose_draw_update",
            "payload": {
                "session_id": session_id,
                "action": action,
                "state": "FIRED" if action == "release" else "ACTIVE_DRAW",
                "draw_strength": max(0.0, min(1.0, draw_strength)),
                "draw_valid": draw_valid,
                "hand_distance_ratio": hand_distance_ratio or 0.0,
                "captured_at": utc_now().isoformat(),
            },
        }

    async def consume_pose_trial_nano(
        target_app: FastAPI,
        session_id: str,
        button_pressed: bool,
    ) -> bool:
        trial = await target_app.state.store.pose_trial(session_id)
        if trial is None:
            return False

        # BowTcpServer emits an ON/OFF pulse. The first ON fires once; the
        # matching OFF and subsequent presses are consumed while FIRED.
        if button_pressed and trial.state == "ACTIVE_DRAW":
            strength = await target_app.state.store.fire_pose_trial(session_id)
            if strength is not None:
                await target_app.state.connections.broadcast(
                    session_id,
                    pose_draw_message(
                        session_id,
                        "release",
                        draw_strength=strength,
                    ),
                )
        return True

    @asynccontextmanager
    async def lifespan(
        app: FastAPI,
    ):
        app.state.store = SessionStore()
        app.state.settings = settings
        app.state.reference_landmarks = _load_reference_landmarks(
            settings.expert_pose_path
        )
        app.state.report_service = SessionReportService(
            PillowReportRenderer(),
            settings.report_output_dir,
        )

        app.state.connections = (
            UnityConnectionManager()
        )

        app.state.feedback = FeedbackEngine(
            app.state.store,
            settings.feedback_cooldown_seconds,
            settings=settings,
        )

        app.state.pending_report_tasks = {}
        app.state.finished_pose_trials = set()
        app.state.pending_trial_scores = {}
        app.state.pending_session_reports = {}
        app.state.bow_input_sequences = {}
        app.state.bow_tcp_server = None
        app.state.bow_ble_task = None

        if settings.bow_tcp_enabled:
            async def handle_tcp_bow_event(
                session_id: str,
                button_pressed: bool,
                draw_value: float,
                draw_valid: bool,
            ) -> None:
                if await consume_pose_trial_nano(app, session_id, button_pressed):
                    return
                message = make_bow_input_message(
                    app,
                    session_id=session_id,
                    button_pressed=button_pressed,
                    draw_value=draw_value,
                    draw_valid=draw_valid,
                )
                await app.state.connections.broadcast(session_id, message)

            app.state.bow_tcp_server = BowTcpServer(
                settings.bow_tcp_host,
                settings.bow_tcp_port,
                handle_tcp_bow_event,
                pulse_seconds=settings.bow_tcp_pulse_seconds,
            )
            await app.state.bow_tcp_server.start()

        if settings.bow_ble_enabled:
            async def handle_ble_press() -> None:
                session_id = settings.bow_ble_session_id
                if await consume_pose_trial_nano(app, session_id, True):
                    return
                pressed = make_bow_input_message(
                    app,
                    session_id=session_id,
                    button_pressed=True,
                    draw_value=0.0,
                    draw_valid=True,
                )
                released = make_bow_input_message(
                    app,
                    session_id=session_id,
                    button_pressed=False,
                    draw_value=0.0,
                    draw_valid=True,
                )
                await app.state.connections.broadcast(session_id, pressed)
                await asyncio.sleep(0.05)
                await app.state.connections.broadcast(session_id, released)

            receiver = BowBleReceiver(
                settings.bow_ble_device_name,
                handle_ble_press,
                scan_timeout=settings.bow_ble_scan_timeout,
                reconnect_delay=settings.bow_ble_reconnect_delay,
            )
            app.state.bow_ble_task = asyncio.create_task(receiver.run())

        print(
            "\n"
            "========================================\n"
            "[Artemis Server 준비 완료]\n"
            "Unity WebSocket 연결 대기 중\n"
            "========================================"
        )

        try:
            yield
        finally:
            if app.state.bow_tcp_server is not None:
                await app.state.bow_tcp_server.stop()
            if app.state.bow_ble_task is not None:
                app.state.bow_ble_task.cancel()
                try:
                    await app.state.bow_ble_task
                except asyncio.CancelledError:
                    pass

    app = FastAPI(
        title=settings.app_name,
        version="0.1.0",
        lifespan=lifespan,
    )

    app.add_middleware(
        CORSMiddleware,
        allow_origins=settings.allowed_origins,
        allow_credentials=(
            settings.allowed_origins != ["*"]
        ),
        allow_methods=["*"],
        allow_headers=["*"],
    )

    app.mount(
        settings.report_public_path,
        StaticFiles(directory=settings.report_output_dir),
        name="reports",
    )

    router = APIRouter(
        prefix=settings.api_prefix
    )

    async def process_frame(
        request: Request | WebSocket,
        frame: UnityFrame,
    ) -> UnityResponse:
        """
        Unity Frame에 가장 가까운 Pose 및 Polar 데이터를 동기화하고,
        실시간 피드백과 샷 누적을 처리합니다.
        """
        frame.captured_at = _normalize_timestamp(
            frame.captured_at
        )

        print(
            "\n"
            "========================================\n"
            "[Unity Frame 수신 성공]\n"
            f"session_id: {frame.session_id}\n"
            f"state: {frame.state.value}\n"
            "========================================"
        )

        synced_landmarks = None
        synced_pose = await request.app.state.store.nearest_pose(
            frame.session_id,
            frame.captured_at,
            settings.pose_sync_window_seconds,
        )
        if synced_pose:
            pose_sample, _ = synced_pose
            synced_landmarks = pose_sample.landmarks
            derived_pose = landmarks_to_features(
                pose_sample.landmarks,
                pose_sample.handedness,
            )
            if frame.pose is None:
                frame.pose = derived_pose
            else:
                provided = frame.pose.model_dump()
                derived = derived_pose.model_dump()
                frame.pose = type(frame.pose)(
                    **{
                        key: provided[key] if provided[key] is not None else value
                        for key, value in derived.items()
                    }
                )

        synced_polar = (
            await request.app.state.store.nearest_polar(
                frame.session_id,
                frame.captured_at,
                settings.sensor_sync_window_seconds,
            )
        )

        polar = None

        if synced_polar:
            polar_sample, age_ms = synced_polar

            polar = SyncedPolarData(
                heart_rate_bpm=(
                    polar_sample.heart_rate_bpm
                ),
                rr_intervals_ms=(
                    polar_sample.rr_intervals_ms
                ),
                captured_at=(
                    polar_sample.captured_at
                ),
                age_ms=age_ms,
                device_id=polar_sample.device_id,
            )

        synced_respiration = await request.app.state.store.nearest_respiration(
            frame.session_id,
            frame.captured_at,
            settings.sensor_sync_window_seconds,
        )
        respiration_rate_bpm = (
            synced_respiration[0].respiratory_rate_bpm
            if synced_respiration
            else None
        )

        feedback = (
            await request.app.state.feedback.create(
                frame,
                polar,
                utc_now(),
            )
        )

        if frame.state in _SHOT_STATES:
            pose_issues = (
                request.app.state.feedback
                .profile
                .evaluate(frame.pose)
            )

            stance_type = (
                classify_stance(frame.pose).value
                if frame.pose
                else None
            )

            await request.app.state.store.shot_record(
                frame.session_id,
                frame.state.value,
                features=frame.pose,
                issue_messages=[
                    issue.message
                    for issue in pose_issues
                ],
                heart_rate=(
                    polar.heart_rate_bpm
                    if polar
                    else None
                ),
                stance_type=stance_type,
                captured_at=frame.captured_at,
                landmarks=synced_landmarks,
                respiration_rate_bpm=respiration_rate_bpm,
            )

            if (
                frame.state
                is ArcheryState.RELEASE
            ):
                # Keyboard (P) releases do not pass through the Nano gateway,
                # so latch the active app-managed trial here as fired.
                await request.app.state.store.fire_pose_trial(
                    frame.session_id
                )

                previous_task = (
                    request.app.state
                    .pending_report_tasks
                    .pop(
                        frame.session_id,
                        None,
                    )
                )

                if (
                    previous_task is not None
                    and not previous_task.done()
                ):
                    previous_task.cancel()

                request.app.state.pending_report_tasks[
                    frame.session_id
                ] = asyncio.create_task(
                    _finalize_after_score_timeout(
                        request.app,
                        frame.session_id,
                    )
                )

        return UnityResponse(
            frame_id=frame.frame_id,
            session_id=frame.session_id,
            state=frame.state,
            polar_h10=polar,
            feedback=feedback,
        )

    @router.get(
        "/health",
        response_model=HealthResponse,
        tags=["system"],
    )
    async def health() -> HealthResponse:
        return HealthResponse()

    @router.post("/pose-trials/start", tags=["pose trials"])
    async def start_pose_trial(
        request: Request,
        command: PoseTrialRequest,
    ) -> dict:
        await request.app.state.store.start_pose_trial(command.session_id)
        message = pose_draw_message(command.session_id, "start")
        await request.app.state.connections.broadcast(command.session_id, message)
        return {
            "accepted": True,
            "session_id": command.session_id,
            "trial_state": "ACTIVE_DRAW",
        }

    @router.post("/pose-trials/finish", tags=["pose trials"])
    async def finish_pose_trial(
        request: Request,
        command: PoseTrialRequest,
    ) -> dict:
        trial = await request.app.state.store.finish_pose_trial(command.session_id)
        if trial is not None and trial.state != "FIRED":
            await request.app.state.connections.broadcast(
                command.session_id,
                pose_draw_message(command.session_id, "cancel", draw_valid=False),
            )

        existing_trials = await request.app.state.store.completed_trials(
            command.session_id
        )
        completed_trial_count = len(existing_trials)
        trial_completed = False
        if trial is not None and trial.state == "FIRED":
            await request.app.state.store.attach_trial_polar_samples(
                command.session_id,
                trial.polar_samples,
            )
            completed_trial_count = min(
                completed_trial_count + 1,
                request.app.state.settings.session_report_shot_count,
            )
            request.app.state.finished_pose_trials.add(command.session_id)
            trial_completed = await _complete_finished_trial(
                request.app,
                command.session_id,
            )

            if not trial_completed:
                previous_task = request.app.state.pending_report_tasks.pop(
                    command.session_id,
                    None,
                )
                if previous_task is not None and not previous_task.done():
                    previous_task.cancel()
                request.app.state.pending_report_tasks[command.session_id] = (
                    asyncio.create_task(
                        _finalize_after_score_timeout(
                            request.app,
                            command.session_id,
                        )
                    )
                )

        return {
            "accepted": trial is not None,
            "session_id": command.session_id,
            "trial_state": "IDLE",
            "previous_state": trial.state if trial else "IDLE",
            "trial_completed": trial_completed,
            "completed_trials": completed_trial_count,
            "required_trials": request.app.state.settings.session_report_shot_count,
            "polar_samples_collected": (
                len(trial.polar_samples) if trial is not None else 0
            ),
        }

    @router.post(
        "/sensors/polar-h10",
        response_model=SensorAccepted,
        status_code=202,
        tags=["sensors"],
    )
    async def ingest_polar(
        request: Request,
        sample: PolarH10Sample,
    ) -> SensorAccepted:
        sample.captured_at = (
            _normalize_timestamp(
                sample.captured_at
            )
        )

        await request.app.state.store.add_polar(
            sample
        )

        active_trial = await request.app.state.store.pose_trial(
            sample.session_id
        )
        collecting = active_trial is not None
        trial_state = active_trial.state if active_trial else "IDLE"
        trial_sample_count = (
            len(active_trial.polar_samples)
            if active_trial is not None
            else 0
        )

        print(
            "\n"
            "========================================\n"
            "[Artemis][POLAR_H10] sample received\n"
            f"session_id: {sample.session_id}\n"
            f"captured_at: {sample.captured_at.isoformat()}\n"
            f"device_id: {sample.device_id or '(unknown)'}\n"
            f"heart_rate_bpm: {sample.heart_rate_bpm}\n"
            f"rr_intervals_ms: {sample.rr_intervals_ms}\n"
            f"trial_state: {trial_state}\n"
            f"trial_collection: {'RECORDING' if collecting else 'NOT_RECORDING'}\n"
            f"trial_polar_sample_count: {trial_sample_count}\n"
            "========================================",
            flush=True,
        )

        await request.app.state.connections.broadcast(
            sample.session_id,
            {
                "type": "sensor_update",
                "sensor": "polar_h10",
                "payload": sample.model_dump(
                    mode="json"
                ),
            },
        )

        return SensorAccepted(
            session_id=sample.session_id,
            captured_at=sample.captured_at,
        )

    @router.post(
        "/sensors/pose",
        status_code=202,
        tags=["sensors"],
    )
    async def ingest_pose(
        request: Request,
        sample: PoseLandmarksSample,
    ):
        sample.captured_at = (
            _normalize_timestamp(
                sample.captured_at
            )
        )

        await request.app.state.store.add_pose(
            sample
        )

        features = landmarks_to_features(
            sample.landmarks,
            sample.handedness,
        )

        stance_type = classify_stance(
            features
        )

        draw_strength, hand_distance_ratio, draw_valid = hand_draw_strength(
            sample.landmarks,
            minimum_ratio=settings.pose_draw_minimum_ratio,
            maximum_ratio=settings.pose_draw_maximum_ratio,
            visibility_threshold=settings.pose_draw_visibility_threshold,
        )
        trial = await request.app.state.store.pose_trial(sample.session_id)
        trial_state = trial.state if trial else "IDLE"
        if trial_state == "ACTIVE_DRAW":
            if draw_valid:
                await request.app.state.store.update_pose_trial(
                    sample.session_id,
                    draw_strength,
                )
            await request.app.state.connections.broadcast(
                sample.session_id,
                pose_draw_message(
                    sample.session_id,
                    "update",
                    draw_strength=draw_strength,
                    draw_valid=draw_valid,
                    hand_distance_ratio=hand_distance_ratio,
                ),
            )

        if _pose_debug_log:
            try:
                with open(
                    _pose_debug_log,
                    "a",
                    encoding="utf-8",
                ) as debug_file:
                    debug_file.write(
                        json.dumps(
                            {
                                "handedness": (
                                    sample.handedness
                                ),
                                "landmarks": [
                                    landmark.model_dump()
                                    for landmark in
                                    sample.landmarks
                                ],
                                "features": (
                                    features.model_dump(
                                        mode="json"
                                    )
                                ),
                                "stance_type": (
                                    stance_type.value
                                ),
                            },
                            ensure_ascii=False,
                        )
                        + "\n"
                    )

            except Exception as exc:
                print(
                    "[pose-debug] "
                    f"로그 기록 실패: {exc}"
                )

        return {
            "accepted": True,
            "session_id": sample.session_id,
            "captured_at": sample.captured_at,
            "features": features.model_dump(
                mode="json"
            ),
            "stance_type": stance_type.value,
            "trial_state": trial_state,
            "draw_strength": draw_strength,
            "hand_distance_ratio": hand_distance_ratio,
            "draw_valid": draw_valid,
        }

    @router.post(
        "/sensors/respiration",
        status_code=202,
        tags=["sensors"],
    )
    async def ingest_respiration(
        request: Request,
        sample: RespirationSample,
    ):
        sample.captured_at = (
            _normalize_timestamp(
                sample.captured_at
            )
        )

        await request.app.state.store.add_respiration(sample)

        await request.app.state.connections.broadcast(
            sample.session_id,
            {
                "type": "sensor_update",
                "sensor": "respiration",
                "payload": sample.model_dump(
                    mode="json"
                ),
            },
        )

        return {
            "accepted": True,
            "session_id": sample.session_id,
            "captured_at": sample.captured_at,
        }

    def next_bow_input_sequence(
        session_id: str,
    ) -> int:
        """
        Unity가 오래되거나 중복된 패킷을 무시할 수 있도록
        세션별 단조 증가 sequence를 서버에서 생성합니다.
        """
        previous_sequence = (
            app.state.bow_input_sequences.get(
                session_id,
                0,
            )
        )

        # 서버를 재시작해도 실행 중인 Unity가 새 패킷을
        # 과거 sequence로 오인하지 않도록 UTC 마이크로초를 기준으로 합니다.
        clock_sequence = int(
            utc_now().timestamp() * 1_000_000
        )

        next_sequence = max(
            previous_sequence + 1,
            clock_sequence,
        )

        app.state.bow_input_sequences[
            session_id
        ] = next_sequence

        return next_sequence

    def make_bow_input_message(
        target_app: FastAPI,
        *,
        session_id: str,
        button_pressed: bool,
        draw_value: float,
        draw_valid: bool,
    ) -> dict:
        sequence = next_bow_input_sequence(session_id)
        return {
            "type": "bow_input",
            "payload": {
                "session_id": session_id,
                "sequence": sequence,
                "button_pressed": button_pressed,
                "draw_value": draw_value,
                "draw_valid": draw_valid,
                "captured_at": utc_now().isoformat(),
            },
        }

    async def broadcast_bow_input(
        request: Request,
        *,
        session_id: str,
        button_pressed: bool,
        draw_value: float,
        draw_valid: bool,
    ) -> dict:
        """
        기존 UnityConnectionManager를 이용해 bow_input을 전송합니다.
        별도의 WebSocket 연결은 만들지 않습니다.
        """
        if await consume_pose_trial_nano(
            request.app,
            session_id,
            button_pressed,
        ):
            trial = await request.app.state.store.pose_trial(session_id)
            return {
                "type": "pose_trial_consumed",
                "payload": {
                    "session_id": session_id,
                    "trial_state": trial.state if trial else "IDLE",
                },
            }

        message = make_bow_input_message(
            request.app,
            session_id=session_id,
            button_pressed=button_pressed,
            draw_value=draw_value,
            draw_valid=draw_valid,
        )

        await request.app.state.connections.broadcast(
            session_id,
            message,
        )

        print(
            "\n"
            "========================================\n"
            "[Bow Input 전송]\n"
            f"session_id: {session_id}\n"
            f"sequence: {message['payload']['sequence']}\n"
            f"button_pressed: {button_pressed}\n"
            f"draw_value: {draw_value}\n"
            f"draw_valid: {draw_valid}\n"
            "========================================"
        )

        return message

    @router.post(
        "/test/bow-input",
        tags=["test"],
    )
    async def test_bow_input(
        request: Request,
        sample: BowInputSample,
    ):
        """
        버튼 상태 또는 센서값 한 패킷을 Unity로 전달합니다.
        실제 Arduino 연동 시에도 같은 요청 형식을 사용할 수 있습니다.
        """
        sent_message = await broadcast_bow_input(
            request,
            session_id=sample.session_id,
            button_pressed=(
                sample.button_pressed
            ),
            draw_value=sample.draw_value,
            draw_valid=sample.draw_valid,
        )

        return {
            "accepted": True,
            "sent": sent_message,
        }

    @router.post(
        "/test/bow-shot",
        tags=["test"],
    )
    async def test_bow_shot(
        request: Request,
        sample: BowShotTestRequest,
    ):
        """
        버튼 ON과 OFF를 한 요청 안에서 순서대로 전송합니다.
        Swagger에서 수동으로 ON/OFF를 누르는 동안 Unity timeout이
        발생하는 문제를 피하기 위한 통합 테스트용 API입니다.
        """
        safe_hold_seconds = max(
            0.01,
            min(sample.hold_seconds, 0.75),
        )

        pressed_message = (
            await broadcast_bow_input(
                request,
                session_id=sample.session_id,
                button_pressed=True,
                draw_value=sample.draw_value,
                draw_valid=sample.draw_valid,
            )
        )

        await asyncio.sleep(
            safe_hold_seconds
        )

        released_message = (
            await broadcast_bow_input(
                request,
                session_id=sample.session_id,
                button_pressed=False,
                draw_value=sample.draw_value,
                draw_valid=sample.draw_valid,
            )
        )

        return {
            "accepted": True,
            "hold_seconds": safe_hold_seconds,
            "pressed": pressed_message,
            "released": released_message,
        }

    @router.post(
        "/unity/frames",
        response_model=UnityResponse,
        tags=["unity"],
    )
    async def unity_frame(
        request: Request,
        frame: UnityFrame,
    ) -> UnityResponse:
        return await process_frame(
            request,
            frame,
        )

    app.include_router(router)

    @app.websocket(
        "/ws/unity/{session_id}"
    )
    async def unity_socket(
        websocket: WebSocket,
        session_id: str,
    ) -> None:
        await app.state.connections.connect(
            session_id,
            websocket,
        )

        pending_session_report = app.state.pending_session_reports.pop(
            session_id,
            None,
        )
        if pending_session_report is not None:
            await websocket.send_json(pending_session_report)
            await app.state.store.clear_completed_trials(session_id)
            print(
                "[Artemis][FINAL_SESSION] queued report delivered after Unity reconnect "
                f"session_id={session_id}",
                flush=True,
            )

        print(
            "\n"
            "========================================\n"
            "[WebSocket] Unity 연결 성공\n"
            f"session_id: {session_id}\n"
            "========================================"
        )

        try:
            while True:
                raw = await websocket.receive_json()

                if not isinstance(raw, dict):
                    await websocket.send_json(
                        {
                            "type": "error",
                            "code": "INVALID_MESSAGE",
                            "message": (
                                "메시지 최상위 구조는 "
                                "JSON 객체여야 합니다."
                            ),
                        }
                    )
                    continue

                message_type = raw.get("type")

                if message_type == "unity_trigger":
                    trigger_wrapper = raw.get(
                        "payload"
                    )

                    if not isinstance(
                        trigger_wrapper,
                        dict,
                    ):
                        await websocket.send_json(
                            {
                                "type": "error",
                                "code": (
                                    "INVALID_TRIGGER_PAYLOAD"
                                ),
                                "message": (
                                    "unity_trigger의 payload는 "
                                    "JSON 객체여야 합니다."
                                ),
                            }
                        )
                        continue

                    payload_session_id = (
                        trigger_wrapper.get(
                            "session_id"
                        )
                    )

                    if payload_session_id != session_id:
                        await websocket.send_json(
                            {
                                "type": "error",
                                "code": "SESSION_MISMATCH",
                                "message": (
                                    "URL과 payload의 "
                                    "session_id가 다릅니다."
                                ),
                            }
                        )
                        continue

                    trigger_name = (
                        trigger_wrapper.get(
                            "name",
                            "UNKNOWN",
                        )
                    )

                    trigger_data = (
                        trigger_wrapper.get(
                            "payload",
                            {},
                        )
                    )

                    allowed_triggers = {
                        "SCORE_UPDATED",
                        "SESSION_RESET",
                        "TEST_TRIGGER",
                    }

                    if (
                        trigger_name
                        not in allowed_triggers
                    ):
                        await websocket.send_json(
                            {
                                "type": "error",
                                "code": (
                                    "UNSUPPORTED_TRIGGER"
                                ),
                                "message": (
                                    "현재 서버에서 사용하지 않는 "
                                    "Trigger입니다."
                                ),
                                "trigger_name": (
                                    trigger_name
                                ),
                            }
                        )
                        continue

                    if trigger_name == "SCORE_UPDATED":
                        accepted_data = {
                            "current_score": int(
                                trigger_data.get(
                                    "current_score",
                                    0,
                                )
                            ),
                            "total_score": int(
                                trigger_data.get(
                                    "total_score",
                                    0,
                                )
                            ),
                            "shot_count": int(
                                trigger_data.get(
                                    "shot_count",
                                    0,
                                )
                            ),
                        }

                    elif trigger_name == "SESSION_RESET":
                        accepted_data = {
                            "reset": True,
                        }

                    else:
                        accepted_data = {}

                    print(
                        "\n"
                        "========================================\n"
                        "[Unity Trigger 수신 성공]\n"
                        f"session_id: {session_id}\n"
                        f"trigger_name: {trigger_name}\n"
                        f"trigger_data: {accepted_data}\n"
                        "========================================"
                    )
                    
                    if trigger_name == "SESSION_RESET":
                        await _reset_unity_session(
                            app,
                            session_id,
                        )

                    await websocket.send_json(
                        {
                            "type": "server_ack",
                            "received": True,
                            "session_id": session_id,
                            "trigger_name": trigger_name,
                            "received_at": (
                                utc_now().isoformat()
                            ),
                        }
                    )

                    if (
                        trigger_name
                        == "SCORE_UPDATED"
                    ):
                        pending_task = (
                            app.state
                            .pending_report_tasks
                            .pop(
                                session_id,
                                None,
                            )
                        )

                        if pending_task is not None:
                            if not pending_task.done():
                                pending_task.cancel()

                        app.state.pending_trial_scores[session_id] = (
                            accepted_data
                        )

                        await _complete_finished_trial(
                            app,
                            session_id,
                        )

                    continue

                try:
                    incoming = (
                        WebSocketIncoming.model_validate(
                            raw
                        )
                    )

                    if (
                        incoming.payload.session_id
                        != session_id
                    ):
                        await websocket.send_json(
                            {
                                "type": "error",
                                "code": "SESSION_MISMATCH",
                                "message": (
                                    "URL과 payload의 "
                                    "session_id가 다릅니다."
                                ),
                            }
                        )
                        continue

                    response = await process_frame(
                        websocket,
                        incoming.payload,
                    )

                    await websocket.send_json(
                        {
                            "type": "feedback",
                            "payload": (
                                response.model_dump(
                                    mode="json"
                                )
                            ),
                        }
                    )

                except ValidationError as exc:
                    error_details = exc.errors(
                        include_url=False
                    )

                    print(
                        "\n"
                        "[WebSocket] 메시지 검증 실패\n"
                        f"details: {error_details}"
                    )

                    await websocket.send_json(
                        {
                            "type": "error",
                            "code": "INVALID_MESSAGE",
                            "details": error_details,
                        }
                    )

        except WebSocketDisconnect:
            print(
                "\n"
                "========================================\n"
                "[WebSocket] Unity 연결 종료\n"
                f"session_id: {session_id}\n"
                "========================================"
            )

        except Exception as exc:
            print(
                "\n"
                "========================================\n"
                "[WebSocket] 처리 중 예외 발생\n"
                f"session_id: {session_id}\n"
                f"error: {type(exc).__name__}: {exc}\n"
                "========================================"
            )

        finally:
            await app.state.connections.disconnect(
                session_id,
                websocket,
            )

    return app


app = create_app()
