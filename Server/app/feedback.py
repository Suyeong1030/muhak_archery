from datetime import datetime

from .config import Settings
from .llm import (
    LLMReportResult,
    SessionContext,
    ShotContext,
    generate_session_report,
    generate_shot_report,
)
from .models import (
    ArcheryState,
    Feedback,
    SyncedPolarData,
    UnityFrame,
)
from .profile import ExpertProfile
from .session_store import SessionStore


class FeedbackEngine:
    def __init__(
        self,
        store: SessionStore,
        cooldown_seconds: float = 2.5,
        profile: ExpertProfile | None = None,
        settings: Settings | None = None,
    ) -> None:
        self.store = store
        self.cooldown_seconds = cooldown_seconds

        self.profile = (
            profile or
            ExpertProfile.load()
        )

        self.settings = settings

    def _pose_issues(self, pose):
        return self.profile.evaluate(pose)

    async def build_shot_report(
        self,
        ctx: ShotContext,
    ) -> LLMReportResult:
        """
        한 발 누적 데이터로 리포트를 생성합니다.

        OpenAI 호출 성공:
            source=openai

        키 미설정, 패키지 누락, API 오류, 빈 응답:
            source=fallback
        """
        if self.settings is None:
            return LLMReportResult(
                message=_fallback_report(ctx),
                source="fallback",
                error="FeedbackEngine에 Settings가 없습니다.",
            )

        result = await generate_shot_report(
            self.settings,
            ctx,
        )

        if result.message:
            return result

        return LLMReportResult(
            message=_fallback_report(ctx),
            source="fallback",
            error=result.error,
        )

    async def build_session_report(
        self,
        ctx: SessionContext,
        fallback_message: str,
    ) -> LLMReportResult:
        if self.settings is None:
            return LLMReportResult(
                message=fallback_message,
                source="fallback",
                error="FeedbackEngine has no settings.",
            )

        result = await generate_session_report(self.settings, ctx)
        if result.message:
            return result
        return LLMReportResult(
            message=fallback_message,
            source="fallback",
            error=result.error,
        )

    async def create(
        self,
        frame: UnityFrame,
        polar: SyncedPolarData | None,
        now: datetime,
    ) -> Feedback:
        issues = self._pose_issues(frame.pose)

        if frame.state is ArcheryState.SETUP:
            return Feedback(
                type="voice",
                code="READY",
                message="호흡을 고르고 준비하세요.",
            )

        if frame.state is ArcheryState.DRAW:
            if issues:
                issue = issues[0]

                return Feedback(
                    type="voice",
                    code=issue.code,
                    message=issue.message,
                )

            return Feedback(type="none")

        if frame.state in (
            ArcheryState.ANCHOR,
            ArcheryState.AIM,
        ):
            if not issues:
                return Feedback(type="none")

            allowed = (
                await self.store.feedback_allowed(
                    frame.session_id,
                    frame.state.value,
                    now,
                    self.cooldown_seconds,
                )
            )

            if not allowed:
                return Feedback(
                    type="none",
                    cooldown_ms=round(
                        self.cooldown_seconds *
                        1000
                    ),
                )

            issue = issues[0]

            return Feedback(
                type="alert",
                code=issue.code,
                message=issue.message,
                cooldown_ms=round(
                    self.cooldown_seconds *
                    1000
                ),
            )

        if frame.state is ArcheryState.RELEASE:
            if issues:
                issue = issues[0]

                return Feedback(
                    type="voice",
                    code=issue.code,
                    message=issue.message,
                )

            return Feedback(type="none")

        if frame.state is ArcheryState.RESULT:
            parts: list[str] = []

            if frame.arrow_score is not None:
                parts.append(
                    f"점수 {frame.arrow_score}점"
                )

            if polar is not None:
                parts.append(
                    "심박수 "
                    f"{polar.heart_rate_bpm} BPM"
                )

            message = (
                ", ".join(parts)
                if parts
                else "측정된 결과 데이터가 없습니다."
            )

            return Feedback(
                type="report",
                code="SHOT_REPORT",
                message=message,
            )

        return Feedback(type="none")


def _fallback_report(
    ctx: ShotContext,
) -> str:
    """
    OpenAI를 사용하지 못할 때 반환하는
    규칙 기반 리포트입니다.
    """
    parts: list[str] = []

    if ctx.arrow_score is not None:
        score_text = f"점수 {ctx.arrow_score}점"

        if ctx.shot_count is not None:
            score_text = (
                f"{ctx.shot_count}번째 발 "
                + score_text
            )

        if ctx.total_score is not None:
            score_text += (
                f" (누적 {ctx.total_score}점)"
            )

        parts.append(score_text)

    if ctx.heart_rate_bpm is not None:
        parts.append(
            "최대 심박수 "
            f"{ctx.heart_rate_bpm} BPM"
        )

    if ctx.stance_type:
        parts.append(
            f"스탠스 {ctx.stance_type}"
        )

    if ctx.issues:
        parts.append(
            "개선점: " +
            ", ".join(ctx.issues)
        )

    if parts:
        return " / ".join(parts)

    if ctx.phases:
        return (
            "동작 단계 "
            + " → ".join(ctx.phases)
            + " 수신은 확인됐지만, "
            "자세·심박 데이터가 없어 "
            "세부 코칭은 생성하지 못했습니다."
        )

    return "이번 발은 분석할 측정 데이터가 없습니다."
