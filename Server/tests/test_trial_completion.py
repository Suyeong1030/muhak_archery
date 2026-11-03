import asyncio
from datetime import datetime, timezone
from pathlib import Path
from types import SimpleNamespace

from app import main as main_module
from app.llm import LLMReportResult
from app.models import PolarH10Sample
from app.reporting.models import PolarReportSample, SessionReport, TrialReport
from app.reporting.pillow_renderer import PillowReportRenderer
from app.reporting.theme import THEME
from app.session_store import SessionStore


def test_score_does_not_finalize_before_app_finishes(monkeypatch):
    calls = []

    async def fake_finalize(app, session_id, **score):
        calls.append((session_id, score))

    monkeypatch.setattr(main_module, "_finalize_shot", fake_finalize)
    app = SimpleNamespace(
        state=SimpleNamespace(
            finished_pose_trials=set(),
            pending_trial_scores={
                "session-1": {
                    "current_score": 9,
                    "total_score": 9,
                    "shot_count": 1,
                }
            },
        )
    )

    completed = asyncio.run(
        main_module._complete_finished_trial(app, "session-1")
    )

    assert completed is False
    assert calls == []


def test_app_finish_finalizes_pending_score_once(monkeypatch):
    calls = []

    async def fake_finalize(app, session_id, **score):
        calls.append((session_id, score))

    monkeypatch.setattr(main_module, "_finalize_shot", fake_finalize)
    app = SimpleNamespace(
        state=SimpleNamespace(
            finished_pose_trials={"session-1"},
            pending_trial_scores={
                "session-1": {
                    "current_score": 8,
                    "total_score": 17,
                    "shot_count": 2,
                }
            },
        )
    )

    first = asyncio.run(
        main_module._complete_finished_trial(app, "session-1")
    )
    duplicate = asyncio.run(
        main_module._complete_finished_trial(app, "session-1")
    )

    assert first is True
    assert duplicate is False
    assert calls == [
        (
            "session-1",
            {
                "current_score": 8,
                "total_score": 17,
                "shot_count": 2,
            },
        )
    ]


def test_polar_samples_are_collected_only_inside_app_trial():
    async def scenario():
        store = SessionStore()

        def sample(heart_rate, rr):
            return PolarH10Sample(
                session_id="session-1",
                captured_at=datetime.now(timezone.utc),
                heart_rate_bpm=heart_rate,
                rr_intervals_ms=rr,
            )

        await store.add_polar(sample(70, [850]))
        await store.start_pose_trial("session-1")
        await store.add_polar(sample(75, [810, 820]))
        await store.add_polar(sample(80, [760]))
        await store.fire_pose_trial("session-1")
        await store.add_polar(sample(78, [780]))
        trial = await store.finish_pose_trial("session-1")
        await store.add_polar(sample(72, [840]))
        return trial

    trial = asyncio.run(scenario())

    assert trial is not None
    assert [sample.heart_rate_bpm for sample in trial.polar_samples] == [75, 80, 78]
    assert trial.polar_samples[0].rr_intervals_ms == [810, 820]


def test_sensor_report_renders_hr_and_rr_png(tmp_path):
    captured_at = datetime.now(timezone.utc)
    report = SessionReport(
        session_id="session-1",
        created_at=captured_at,
        message="test",
        trials=[
            TrialReport(
                trial_number=1,
                polar_samples=[
                    PolarReportSample(
                        captured_at=captured_at,
                        heart_rate_bpm=72,
                        rr_intervals_ms=[820, 830],
                    ),
                    PolarReportSample(
                        captured_at=captured_at,
                        heart_rate_bpm=84,
                        rr_intervals_ms=[710, 720],
                    ),
                ],
            )
        ],
    )
    destination = tmp_path / "sensor.png"

    asyncio.run(
        PillowReportRenderer().render_sensor_timeline(
            report,
            destination,
            THEME,
        )
    )

    assert destination.read_bytes().startswith(b"\x89PNG\r\n\x1a\n")


def test_final_session_generates_and_broadcasts_report():
    broadcasts = []
    cleared = []

    class Feedback:
        async def build_session_report(self, context, fallback_message):
            assert len(context.trials) == 3
            return LLMReportResult(
                message="세 발 종합 분석",
                source="openai",
            )

    class ReportService:
        theme = SimpleNamespace(
            geometry=SimpleNamespace(canvas_width=1920, canvas_height=1080)
        )

        async def render(self, report):
            assert report.message == "세 발 종합 분석"
            return [
                SimpleNamespace(
                    kind="summary",
                    trial_number=None,
                    path=Path("summary.png"),
                )
            ]

    class Connections:
        async def broadcast(self, session_id, message):
            broadcasts.append((session_id, message))
            return 1

    class Store:
        async def clear_completed_trials(self, session_id):
            cleared.append(session_id)

    app = SimpleNamespace(
        state=SimpleNamespace(
            feedback=Feedback(),
            report_service=ReportService(),
            connections=Connections(),
            store=Store(),
            settings=SimpleNamespace(report_public_path="/reports"),
            pending_session_reports={},
        )
    )
    trials = [TrialReport(trial_number=index, score=8) for index in range(1, 4)]

    asyncio.run(
        main_module._finalize_session_report(app, "session-1", trials)
    )

    assert broadcasts[0][1]["type"] == "session_report"
    assert broadcasts[0][1]["payload"]["shot_count"] == 3
    assert cleared == ["session-1"]
