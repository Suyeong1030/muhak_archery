from __future__ import annotations

from abc import ABC, abstractmethod
from pathlib import Path

from .models import SessionReport, TrialReport
from .theme import ArtemisTheme


class ReportRenderer(ABC):
    """Boundary for the future HTML-to-PNG rendering implementation."""

    @abstractmethod
    async def render_summary(self, report: SessionReport, destination: Path, theme: ArtemisTheme) -> None:
        raise NotImplementedError

    @abstractmethod
    async def render_sensor_timeline(self, report: SessionReport, destination: Path, theme: ArtemisTheme) -> None:
        raise NotImplementedError

    @abstractmethod
    async def render_trial(self, report: SessionReport, trial: TrialReport, destination: Path, theme: ArtemisTheme) -> None:
        raise NotImplementedError
