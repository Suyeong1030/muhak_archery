"""
전문가 레퍼런스 프로파일 로더 + 룰베이스 자세 평가.

expert_profile.json 의 규칙을 읽어 PoseFeatures 를 평가하고
위반 사항(PoseIssue)을 우선순위 순으로 돌려준다. 임계값을 코드가 아니라
데이터(JSON)로 두어, 전문가 자세 재캘리브레이션 시 코드 변경이 필요 없다.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

from .models import PoseFeatures

_DEFAULT_PATH = Path(__file__).resolve().parent.parent / "expert_profile.json"


@dataclass(frozen=True)
class PoseIssue:
    priority: int
    code: str
    message: str


class ExpertProfile:
    def __init__(self, rules: list[dict], handedness: str = "right") -> None:
        self._rules = sorted(rules, key=lambda r: r.get("priority", 999))
        self.handedness = handedness

    @classmethod
    def load(cls, path: str | Path | None = None) -> "ExpertProfile":
        path = Path(path) if path else _DEFAULT_PATH
        data = json.loads(path.read_text(encoding="utf-8"))
        return cls(data.get("rules", []), data.get("handedness", "right"))

    @staticmethod
    def _violates(rule: dict, value: float) -> bool:
        rtype = rule["type"]
        if rtype == "abs_max":
            return abs(value) > rule["limit"]
        if rtype == "min":
            return value < rule["limit"]
        if rtype == "range":
            return not (rule["min"] <= value <= rule["max"])
        return False

    def evaluate(self, pose: PoseFeatures | None) -> list[PoseIssue]:
        if pose is None:
            return []
        issues: list[PoseIssue] = []
        for rule in self._rules:
            value = getattr(pose, rule["feature"], None)
            if value is None:
                continue
            if self._violates(rule, value):
                issues.append(
                    PoseIssue(rule["priority"], rule["code"], rule["message"])
                )
        return issues
