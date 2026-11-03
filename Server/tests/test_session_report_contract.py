import json
from pathlib import Path

import pytest
from pydantic import ValidationError

from app.reporting.contract import SessionReportEnvelope


CONTRACT_DIR = Path(__file__).resolve().parents[1] / "contracts"


def test_v1_example_matches_outbound_model() -> None:
    example = json.loads(
        (CONTRACT_DIR / "session_report.v1.example.json").read_text(
            encoding="utf-8"
        )
    )
    envelope = SessionReportEnvelope.model_validate(example)
    assert envelope.model_dump(mode="json") == example


def test_trial_page_requires_trial_number() -> None:
    example = json.loads(
        (CONTRACT_DIR / "session_report.v1.example.json").read_text(
            encoding="utf-8"
        )
    )
    example["payload"]["pages"][2]["trial_number"] = None
    with pytest.raises(ValidationError):
        SessionReportEnvelope.model_validate(example)


def test_page_indexes_must_be_contiguous() -> None:
    example = json.loads(
        (CONTRACT_DIR / "session_report.v1.example.json").read_text(
            encoding="utf-8"
        )
    )
    example["payload"]["pages"][1]["page_index"] = 9
    with pytest.raises(ValidationError):
        SessionReportEnvelope.model_validate(example)
