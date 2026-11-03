from __future__ import annotations

import json
from dataclasses import dataclass
from typing import Literal

from .config import Settings


@dataclass
class ShotContext:
    arrow_score: int | None = None
    total_score: int | None = None
    shot_count: int | None = None
    heart_rate_bpm: int | None = None
    stance_type: str | None = None
    issues: list[str] | None = None
    phases: list[str] | None = None
    min_stability: float | None = None
    frames: int | None = None


@dataclass
class SessionContext:
    session_id: str
    trials: list[dict]


@dataclass
class LLMReportResult:
    """
    샷 리포트 생성 결과입니다.

    source:
    - openai: OpenAI API가 실제 문구를 생성함
    - fallback: 키 미설정, 패키지 누락, API 오류 또는 빈 응답
    """

    message: str | None
    source: Literal["openai", "fallback"]
    error: str | None = None


def _build_prompt(ctx: ShotContext) -> str:
    parts: list[str] = []

    if ctx.phases:
        parts.append(
            "측정 구간: " +
            " → ".join(ctx.phases)
        )

    if ctx.arrow_score is not None:
        score_text = (
            f"이번 화살 점수: {ctx.arrow_score}점"
        )

        if ctx.shot_count is not None:
            score_text += (
                f" / {ctx.shot_count}번째 발"
            )

        if ctx.total_score is not None:
            score_text += (
                f" / 누적 {ctx.total_score}점"
            )

        parts.append(score_text)

    if ctx.heart_rate_bpm is not None:
        parts.append(
            "샷 구간 최대 심박수: "
            f"{ctx.heart_rate_bpm} BPM"
        )

    if ctx.min_stability is not None:
        parts.append(
            "최저 안정성(0~1): "
            f"{ctx.min_stability:.2f}"
        )

    if ctx.stance_type:
        parts.append(
            f"스탠스 유형: {ctx.stance_type}"
        )

    if ctx.issues:
        parts.append(
            "자주 감지된 자세 이슈: " +
            ", ".join(ctx.issues)
        )

    if ctx.frames is not None:
        parts.append(
            f"누적 상태 프레임 수: {ctx.frames}"
        )

    if not parts:
        parts.append(
            "측정된 데이터가 거의 없음"
        )

    return "\n".join(parts)


async def generate_shot_report(
    settings: Settings,
    ctx: ShotContext,
) -> LLMReportResult:
    """
    OpenAI로 짧은 코칭 리포트를 생성합니다.

    실패를 숨기지 않고 fallback 결과와 오류 원인을 반환합니다.
    API 키 문자열 자체는 오류 메시지나 로그에 포함하지 않습니다.
    """
    if not settings.openai_api_key:
        return LLMReportResult(
            message=None,
            source="fallback",
            error="OPENAI_API_KEY가 설정되지 않았습니다.",
        )

    try:
        from openai import AsyncOpenAI
    except ImportError:
        return LLMReportResult(
            message=None,
            source="fallback",
            error="openai Python 패키지가 설치되지 않았습니다.",
        )

    client = AsyncOpenAI(
        api_key=settings.openai_api_key
    )

    system_prompt = (
        "너는 생체신호와 동작을 함께 해석하는 양궁 퍼포먼스 코치다. "
        "제공된 한 발의 측정 데이터만 사용해 한국어로 한 문장의 "
        "짧은 피드백을 작성한다. "
        "측정되지 않은 자세, 호흡, 심박, 점수를 추측하지 않는다. "
        "자세 데이터가 없다면 자세가 좋거나 나쁘다고 평가하지 말고, "
        "확인 가능한 동작 단계와 데이터 부족 사실만 간단히 설명한다. "
        "심박 또는 호흡 데이터와 자세 데이터가 함께 있다면, 수치를 "
        "나열하지 말고 생체 리듬 변화가 안정성 저하나 자세 흐트러짐과 "
        "시간적으로 함께 나타났는지부터 설명한다. 상관관계를 인과관계로 "
        "단정하지 않는다. 여러 문제를 나열하지 말고 경기력에 가장 큰 "
        "영향을 준 핵심 개선점 하나와 바로 실행할 교정 행동 하나를 말한다. "
        "숫자는 판단 근거로 꼭 필요할 때만 최소한으로 사용한다."
    )

    try:
        response = await client.chat.completions.create(
            model=settings.openai_model,
            messages=[
                {
                    "role": "system",
                    "content": system_prompt,
                },
                {
                    "role": "user",
                    "content": _build_prompt(ctx),
                },
            ],
            max_tokens=200,
            temperature=0.3,
            timeout=settings.llm_report_timeout_seconds,
        )

        content = (
            response.choices[0]
            .message
            .content
        )

        if not content or not content.strip():
            return LLMReportResult(
                message=None,
                source="fallback",
                error="OpenAI API가 빈 응답을 반환했습니다.",
            )

        return LLMReportResult(
            message=content.strip(),
            source="openai",
            error=None,
        )

    except Exception as exc:
        return LLMReportResult(
            message=None,
            source="fallback",
            error=(
                f"{type(exc).__name__}: {exc}"
            ),
        )


async def generate_session_report(
    settings: Settings,
    ctx: SessionContext,
) -> LLMReportResult:
    """Generate the final three-trial coaching summary including HR/RR data."""
    if not settings.openai_api_key:
        return LLMReportResult(
            message=None,
            source="fallback",
            error="OPENAI_API_KEY is not configured.",
        )

    try:
        from openai import AsyncOpenAI
    except ImportError:
        return LLMReportResult(
            message=None,
            source="fallback",
            error="The openai Python package is not installed.",
        )

    prompt = (
        "다음 세 번의 양궁 트라이얼을 분석하세요. Polar H10 및 호흡 샘플은 "
        "앱의 트라이얼 시작과 종료 사이에서만 수집되었습니다. 각 트라이얼의 "
        "시간 흐름을 따라 심박·RR·호흡 변화가 안정성 저하 또는 자세 이슈와 "
        "함께 나타났는지 비교하세요. 데이터가 있을 때만 점수와 연결하고, "
        "상관관계를 의학적 또는 확정적 인과관계로 표현하지 마세요. 최종 답변은 "
        "한국어로 작성하세요. 수치 요약이나 문제 목록보다 '생체 리듬 변화 → "
        "자세 흐트러짐 → 다음 샷의 핵심 교정 행동'이라는 코칭 스토리를 우선하세요. "
        "가장 개선 효과가 큰 핵심 사항 하나를 선택하고, 실행 가능한 권고 하나로 "
        "마무리하세요. 숫자는 결론을 뒷받침할 때만 최소한으로 사용하세요.\n\n"
        + json.dumps(
            {
                "session_id": ctx.session_id,
                "trials": ctx.trials,
            },
            ensure_ascii=False,
            separators=(",", ":"),
        )
    )

    try:
        response = await AsyncOpenAI(
            api_key=settings.openai_api_key
        ).chat.completions.create(
            model=settings.openai_model,
            messages=[
                {
                    "role": "system",
                    "content": (
                        "너는 생체신호와 자세의 시간적 관계를 해석하는 양궁 "
                        "퍼포먼스 코치다. Polar H10 심박·RR 및 호흡 데이터는 "
                        "의학적 증거가 아니라 경기 수행 맥락으로만 사용한다. "
                        "누락된 측정값을 만들지 않고, 상관관계를 인과로 단정하지 "
                        "않는다. 코치 피드백은 숫자 나열보다 가장 중요한 자세 "
                        "개선점 하나와 구체적인 다음 행동을 우선한다."
                    ),
                },
                {"role": "user", "content": prompt},
            ],
            max_tokens=350,
            temperature=0.3,
            timeout=settings.llm_report_timeout_seconds,
        )
        content = response.choices[0].message.content
        if not content or not content.strip():
            return LLMReportResult(
                message=None,
                source="fallback",
                error="OpenAI returned an empty session report.",
            )
        return LLMReportResult(message=content.strip(), source="openai")
    except Exception as exc:
        return LLMReportResult(
            message=None,
            source="fallback",
            error=f"{type(exc).__name__}: {exc}",
        )
