import asyncio
from collections import defaultdict

from fastapi import WebSocket


class UnityConnectionManager:
    def __init__(self) -> None:
        self._connections: dict[str, set[WebSocket]] = defaultdict(set)
        self._lock = asyncio.Lock()

    async def connect(self, session_id: str, websocket: WebSocket) -> None:
        await websocket.accept()
        async with self._lock:
            self._connections[session_id].add(websocket)

    async def disconnect(self, session_id: str, websocket: WebSocket) -> None:
        async with self._lock:
            connections = self._connections.get(session_id)
            if connections is None:
                return
            connections.discard(websocket)
            if not connections:
                self._connections.pop(session_id, None)

    async def broadcast(self, session_id: str, message: dict) -> int:
        async with self._lock:
            connections = tuple(self._connections.get(session_id, ()))
        stale: list[WebSocket] = []
        delivered = 0
        for websocket in connections:
            try:
                await websocket.send_json(message)
                delivered += 1
            except RuntimeError:
                stale.append(websocket)
        for websocket in stale:
            await self.disconnect(session_id, websocket)
        return delivered

