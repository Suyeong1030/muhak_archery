import asyncio
import json
from collections.abc import Awaitable, Callable
from typing import Any


BowEventHandler = Callable[[str, bool, float, bool], Awaitable[None]]


class BowTcpServer:
    """Nano 33 IoT의 newline-delimited JSON 버튼 이벤트를 수신한다."""

    def __init__(
        self,
        host: str,
        port: int,
        handler: BowEventHandler,
        *,
        pulse_seconds: float = 0.05,
    ) -> None:
        self.host = host
        self.port = port
        self.handler = handler
        self.pulse_seconds = pulse_seconds
        self._server: asyncio.Server | None = None

    async def start(self) -> None:
        self._server = await asyncio.start_server(
            self._handle_client,
            self.host,
            self.port,
        )

    async def stop(self) -> None:
        if self._server is None:
            return
        self._server.close()
        await self._server.wait_closed()
        self._server = None

    async def _handle_client(
        self,
        reader: asyncio.StreamReader,
        writer: asyncio.StreamWriter,
    ) -> None:
        try:
            while line := await reader.readline():
                try:
                    payload = json.loads(line)
                    await self._dispatch(payload)
                    response: dict[str, Any] = {"accepted": True}
                except (json.JSONDecodeError, TypeError, ValueError) as exc:
                    response = {"accepted": False, "error": str(exc)}

                writer.write(
                    json.dumps(response, separators=(",", ":")).encode()
                    + b"\n"
                )
                await writer.drain()
        finally:
            writer.close()
            await writer.wait_closed()

    async def _dispatch(self, payload: Any) -> None:
        if not isinstance(payload, dict):
            raise ValueError("message must be a JSON object")

        session_id = payload.get("session_id")
        if not isinstance(session_id, str) or not session_id.strip():
            raise ValueError("session_id must be a non-empty string")

        event = payload.get("event", "press")
        if event not in {"press", "release"}:
            raise ValueError("event must be 'press' or 'release'")

        draw_value = float(payload.get("draw_value", 0.0))
        draw_valid = bool(payload.get("draw_valid", True))
        pressed = event == "press"
        print(
            "[Bow TCP] "
            f"session_id={session_id} event={event} "
            f"draw_value={draw_value} draw_valid={draw_valid}"
        )
        await self.handler(session_id, pressed, draw_value, draw_valid)

        # Nano는 발사 순간에 press edge 하나만 보내면 된다. Unity의 기존
        # 버튼 상태 계약을 위해 서버가 release edge를 자동 생성한다.
        if pressed:
            await asyncio.sleep(self.pulse_seconds)
            await self.handler(session_id, False, draw_value, draw_valid)
