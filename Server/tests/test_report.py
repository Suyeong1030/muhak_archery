import asyncio
from types import SimpleNamespace

from app.feedback import FeedbackEngine
from app.llm import ShotContext, generate_shot_report
from app.session_store import SessionStore


def test_build_shot_report_falls_back_without_settings():
    # settings 가 없으면 규칙 기반 문자열 리포트로 폴백해야 한다.
    engine = FeedbackEngine(SessionStore(), settings=None)
    ctx = ShotContext(
        arrow_score=9,
        stance_type="SQUARE",
        issues=["당기는 팔꿈치를 화살 선에 맞춰주세요."],
        phases=["DRAW", "ANCHOR", "AIM", "RELEASE"],
    )
    out = asyncio.run(engine.build_shot_report(ctx))
    assert "9" in out
    assert "SQUARE" in out


def test_generate_shot_report_returns_none_without_key():
    # 키가 없으면 (env 무관) None 을 돌려준다.
    fake_settings = SimpleNamespace(openai_api_key=None)
    out = asyncio.run(generate_shot_report(fake_settings, ShotContext(arrow_score=10)))
    assert out is None
