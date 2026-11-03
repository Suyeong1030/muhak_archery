import asyncio

import pytest

from app.bow_tcp import BowTcpServer


def test_press_generates_press_and_release_pulse() -> None:
    received: list[tuple[str, bool, float, bool]] = []

    async def handler(
        session_id: str,
        pressed: bool,
        draw_value: float,
        draw_valid: bool,
    ) -> None:
        received.append((session_id, pressed, draw_value, draw_valid))

    async def exercise() -> None:
        server = BowTcpServer("127.0.0.1", 0, handler, pulse_seconds=0)
        await server._dispatch(
            {
                "session_id": "practice-001",
                "event": "press",
                "draw_value": 412,
            }
        )

    asyncio.run(exercise())

    assert received == [
        ("practice-001", True, 412.0, True),
        ("practice-001", False, 412.0, True),
    ]


def test_invalid_event_is_rejected() -> None:
    async def handler(*args: object) -> None:
        raise AssertionError("handler must not be called")

    async def exercise() -> None:
        server = BowTcpServer("127.0.0.1", 0, handler)
        with pytest.raises(ValueError, match="event"):
            await server._dispatch({"session_id": "s1", "event": "tap"})

    asyncio.run(exercise())
