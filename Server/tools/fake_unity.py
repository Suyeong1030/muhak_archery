"""End-to-end demo client for sensors, three Unity trials, and final PNG report."""

from __future__ import annotations

import argparse
import asyncio
import json
from datetime import datetime, timezone

import httpx
import websockets


STATES = ("DRAW", "ANCHOR", "AIM", "RELEASE")
DEFAULT_SCORES = (8, 8, 10)


def pose_landmarks(trial: int) -> list[dict[str, float]]:
    points = [(0.5, 0.13)] * 33
    shoulder_shift = (4 - trial) * 0.01
    elbow_drop = (4 - trial) * 0.008
    values = {
        0: (0.50, 0.12), 11: (0.38, 0.27 + shoulder_shift),
        12: (0.62, 0.27), 13: (0.23, 0.30),
        14: (0.75, 0.31 + elbow_drop), 15: (0.10, 0.28),
        16: (0.54, 0.18 + elbow_drop), 23: (0.43, 0.52),
        24: (0.58, 0.52), 25: (0.41, 0.72),
        26: (0.60, 0.72), 27: (0.39, 0.91),
        28: (0.62, 0.91), 29: (0.36, 0.94),
        30: (0.59, 0.94), 31: (0.43, 0.96),
        32: (0.66, 0.96),
    }
    for index, value in values.items():
        points[index] = value
    return [
        {"x": x, "y": y, "z": 0.0, "visibility": 1.0}
        for x, y in points
    ]


async def receive_until(ws, expected: str) -> dict:
    while True:
        message = json.loads(await asyncio.wait_for(ws.recv(), timeout=30))
        if message.get("type") == expected:
            return message


async def post_sensors(
    client: httpx.AsyncClient,
    session: str,
    trial: int,
    captured_at: str,
) -> None:
    payloads = (
        (
            "/api/v1/sensors/pose",
            {
                "session_id": session,
                "captured_at": captured_at,
                "handedness": "right",
                "landmarks": pose_landmarks(trial),
            },
        ),
        (
            "/api/v1/sensors/polar-h10",
            {
                "session_id": session,
                "captured_at": captured_at,
                "heart_rate_bpm": 101 - trial * 3,
                "rr_intervals_ms": [650 + trial * 20, 655 + trial * 20],
            },
        ),
        (
            "/api/v1/sensors/respiration",
            {
                "session_id": session,
                "captured_at": captured_at,
                "respiratory_rate_bpm": 18 - trial,
                "phase": "exhale",
                "normalized_wave": 0.2,
            },
        ),
    )
    responses = await asyncio.gather(
        *(client.post(path, json=payload) for path, payload in payloads)
    )
    for response in responses:
        response.raise_for_status()


async def run(host: str, session: str, hold: float) -> None:
    http_base = f"http://{host}"
    ws_uri = f"ws://{host}/ws/unity/{session}"
    total_score = 0

    print(f"[fake-unity] 연결: {ws_uri}")
    async with httpx.AsyncClient(base_url=http_base, timeout=10) as client:
        async with websockets.connect(ws_uri) as ws:
            for trial, score in enumerate(DEFAULT_SCORES, 1):
                print(f"\n[fake-unity] Trial {trial} 시작")
                for state_index, state in enumerate(STATES):
                    captured_at = datetime.now(timezone.utc).isoformat()
                    await post_sensors(client, session, trial, captured_at)
                    stability = min(0.96, 0.70 + trial * 0.06 + state_index * 0.015)
                    await ws.send(
                        json.dumps(
                            {
                                "type": "unity_frame",
                                "payload": {
                                    "session_id": session,
                                    "state": state,
                                    "captured_at": captured_at,
                                    "pose": {"stability": stability},
                                },
                            }
                        )
                    )
                    reply = await receive_until(ws, "feedback")
                    feedback = reply.get("payload", {}).get("feedback", {})
                    print(f"  {state:8} → {feedback.get('type')}/{feedback.get('code')}")
                    await asyncio.sleep(hold)

                total_score += score
                await ws.send(
                    json.dumps(
                        {
                            "type": "unity_trigger",
                            "payload": {
                                "session_id": session,
                                "name": "SCORE_UPDATED",
                                "payload": {
                                    "current_score": score,
                                    "total_score": total_score,
                                    "shot_count": trial,
                                },
                            },
                        }
                    )
                )
                await receive_until(ws, "server_ack")
                shot_report = await receive_until(ws, "shot_report")
                print(
                    f"  shot_report: {shot_report['payload']['message']}"
                )

            session_report = await receive_until(ws, "session_report")
            payload = session_report["payload"]
            print("\n========================================")
            print("[SESSION REPORT]")
            print(f"score: {payload['total_score']} / 30")
            print(f"message: {payload['message']}")
            for page in payload["pages"]:
                print(f"page: {http_base}{page['url']}")
            print("========================================")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1:8000")
    parser.add_argument("--session", default="practice-001")
    parser.add_argument("--hold", type=float, default=0.05)
    args = parser.parse_args()
    asyncio.run(run(args.host, args.session, args.hold))


if __name__ == "__main__":
    main()
