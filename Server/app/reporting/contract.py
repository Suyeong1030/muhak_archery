from __future__ import annotations

from typing import Literal

from pydantic import BaseModel, Field, model_validator


class ReportSensorAvailability(BaseModel):
    pose: bool
    polar_h10: bool
    respiration: bool


class ReportPagePayload(BaseModel):
    page_index: int = Field(ge=0)
    kind: Literal["summary", "sensor", "trial"]
    trial_number: int | None = Field(default=None, ge=1)
    url: str = Field(pattern=r"^(https?://|/).+")
    mime_type: Literal["image/png"] = "image/png"
    width: int = Field(gt=0)
    height: int = Field(gt=0)

    @model_validator(mode="after")
    def validate_trial_number(self) -> "ReportPagePayload":
        if self.kind == "trial" and self.trial_number is None:
            raise ValueError("trial page requires trial_number")
        if self.kind != "trial" and self.trial_number is not None:
            raise ValueError("non-trial page must not have trial_number")
        return self


class SessionReportPayload(BaseModel):
    report_version: Literal[1] = 1
    session_id: str = Field(min_length=1, max_length=100)
    code: Literal["SESSION_REPORT"] = "SESSION_REPORT"
    message: str = Field(min_length=1)
    shot_count: int = Field(ge=1)
    total_score: int = Field(ge=0)
    average_score: float = Field(ge=0)
    strengths: list[str]
    weaknesses: list[str]
    recommendations: list[str]
    sensor_availability: ReportSensorAvailability
    pages: list[ReportPagePayload] = Field(min_length=1)

    @model_validator(mode="after")
    def validate_pages(self) -> "SessionReportPayload":
        indexes = [page.page_index for page in self.pages]
        if indexes != list(range(len(self.pages))):
            raise ValueError("pages must use contiguous page_index values from zero")
        if self.pages[0].kind != "summary":
            raise ValueError("first page must be summary")
        return self


class SessionReportEnvelope(BaseModel):
    type: Literal["session_report"] = "session_report"
    payload: SessionReportPayload
