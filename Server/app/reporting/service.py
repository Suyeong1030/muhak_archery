from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from .models import SessionReport
from .renderer import ReportRenderer
from .theme import THEME, ArtemisTheme


@dataclass(frozen=True)
class ReportPage:
    kind: str
    path: Path
    trial_number: int | None = None


class SessionReportService:
    """Creates an ordered set of report images for Unity."""

    def __init__(self, renderer: ReportRenderer, output_root: Path, theme: ArtemisTheme = THEME) -> None:
        self.renderer = renderer
        self.output_root = output_root
        self.theme = theme

    async def render(self, report: SessionReport) -> list[ReportPage]:
        session_dir = self.output_root / report.session_id
        session_dir.mkdir(parents=True, exist_ok=True)

        pages = [
            ReportPage(kind="summary", path=session_dir / "summary.png"),
            ReportPage(kind="sensor", path=session_dir / "sensor.png"),
        ]
        await self.renderer.render_summary(report, pages[0].path, self.theme)
        await self.renderer.render_sensor_timeline(report, pages[1].path, self.theme)

        for trial in report.trials:
            page = ReportPage(
                kind="trial",
                path=session_dir / f"trial-{trial.trial_number}.png",
                trial_number=trial.trial_number,
            )
            await self.renderer.render_trial(report, trial, page.path, self.theme)
            pages.append(page)

        return pages
