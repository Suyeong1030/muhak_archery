from functools import lru_cache
from pathlib import Path

from pydantic import AliasChoices, Field
from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    app_name: str = "Artemis Realtime Server"
    api_prefix: str = "/api/v1"
    # 심박(Polar)은 느리게 변하므로 넉넉히. 자세(pose)는 빠르게 바뀌므로 좁게 둔다.
    sensor_sync_window_seconds: float = 5.0
    pose_sync_window_seconds: float = 1.0
    pose_draw_minimum_ratio: float = 0.5
    pose_draw_maximum_ratio: float = 2.5
    pose_draw_visibility_threshold: float = 0.5
    feedback_cooldown_seconds: float = 2.5
    allowed_origins: list[str] = ["*"]

    # Nano 33 IoT button input (newline-delimited JSON over TCP).
    bow_tcp_enabled: bool = False
    bow_tcp_host: str = "0.0.0.0"
    bow_tcp_port: int = 8765
    bow_tcp_pulse_seconds: float = 0.05
    bow_ble_enabled: bool = False
    bow_ble_device_name: str = "ArtemisBow"
    bow_ble_session_id: str = "practice-001"
    bow_ble_scan_timeout: float = 8.0
    bow_ble_reconnect_delay: float = 2.0

    # LLM(GPT) 샷 리포트. 키는 .env 의 OPENAI_API_KEY 를 그대로 읽는다.
    # 키가 없으면 리포트는 기존 규칙 기반 문자열로 자동 폴백한다.
    openai_api_key: str | None = Field(
        default=None,
        validation_alias=AliasChoices("OPENAI_API_KEY", "ARTEMIS_OPENAI_API_KEY"),
    )
    openai_model: str = "gpt-4o-mini"
    llm_report_timeout_seconds: float = 10.0

    # Session report images served to Unity after the configured shot count.
    report_output_dir: Path = Path("generated/reports")
    report_public_path: str = "/reports"
    session_report_shot_count: int = 3
    expert_pose_path: Path = Path("expert_pose.json")

    model_config = SettingsConfigDict(
        env_file=".env",
        env_prefix="ARTEMIS_",
        extra="ignore",
    )


@lru_cache
def get_settings() -> Settings:
    return Settings()
