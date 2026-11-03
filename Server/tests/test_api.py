from datetime import datetime, timezone

from fastapi.testclient import TestClient

from app.main import create_app


def test_health() -> None:
    with TestClient(create_app()) as client:
        response = client.get("/api/v1/health")
    assert response.status_code == 200
    assert response.json() == {"status": "ok"}


def test_polar_sample_is_synchronized_into_unity_response() -> None:
    captured_at = datetime.now(timezone.utc).isoformat()
    with TestClient(create_app()) as client:
        accepted = client.post(
            "/api/v1/sensors/polar-h10",
            json={
                "session_id": "practice-1",
                "captured_at": captured_at,
                "heart_rate_bpm": 82,
                "rr_intervals_ms": [731.7],
            },
        )
        response = client.post(
            "/api/v1/unity/frames",
            json={
                "session_id": "practice-1",
                "captured_at": captured_at,
                "state": "DRAW",
                "pose": {"torso_tilt_deg": 12},
            },
        )

    assert accepted.status_code == 202
    assert response.status_code == 200
    body = response.json()
    assert body["polar_h10"]["heart_rate_bpm"] == 82
    assert body["polar_h10"]["age_ms"] == 0
    assert body["feedback"]["code"] == "TORSO_TILT"


def test_polar_data_does_not_cross_sessions() -> None:
    captured_at = datetime.now(timezone.utc).isoformat()
    with TestClient(create_app()) as client:
        client.post(
            "/api/v1/sensors/polar-h10",
            json={"session_id": "a", "captured_at": captured_at, "heart_rate_bpm": 75},
        )
        response = client.post(
            "/api/v1/unity/frames",
            json={"session_id": "b", "captured_at": captured_at, "state": "AIM"},
        )
    assert response.json()["polar_h10"] is None


def test_unity_websocket_returns_feedback() -> None:
    with TestClient(create_app()) as client:
        with client.websocket_connect("/ws/unity/ws-session") as websocket:
            websocket.send_json(
                {
                    "type": "unity_frame",
                    "payload": {"session_id": "ws-session", "state": "SETUP"},
                }
            )
            response = websocket.receive_json()
    assert response["type"] == "feedback"
    assert response["payload"]["feedback"]["code"] == "READY"


def test_anchor_feedback_is_throttled() -> None:
    payload = {
        "session_id": "cooldown-test",
        "state": "ANCHOR",
        "pose": {"stability": 0.2},
    }
    with TestClient(create_app()) as client:
        first = client.post("/api/v1/unity/frames", json=payload).json()
        second = client.post("/api/v1/unity/frames", json=payload).json()
    assert first["feedback"]["type"] == "alert"
    assert second["feedback"]["type"] == "none"

