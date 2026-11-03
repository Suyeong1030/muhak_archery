from fastapi.testclient import TestClient

from app.main import create_app
from app.models import PoseLandmark, StanceType
from app.pose import classify_stance, hand_draw_strength, landmarks_to_features


def _base_landmarks() -> list[dict]:
    lm = [{"x": 0.5, "y": 0.5} for _ in range(33)]
    lm[0] = {"x": 0.50, "y": 0.20}   # nose
    lm[11] = {"x": 0.40, "y": 0.40}  # left shoulder
    lm[12] = {"x": 0.60, "y": 0.40}  # right shoulder (수평)
    lm[14] = {"x": 0.70, "y": 0.50}  # right elbow
    lm[16] = {"x": 0.60, "y": 0.55}  # right wrist (굽힘)
    lm[23] = {"x": 0.42, "y": 0.75}  # left hip
    lm[24] = {"x": 0.58, "y": 0.75}  # right hip (수직)
    return lm


def test_landmarks_to_features_computes_angles():
    lm = [PoseLandmark(**p) for p in _base_landmarks()]
    features = landmarks_to_features(lm, "right")
    # 어깨 수평 / 상체 수직 -> 0에 가까움
    assert abs(features.shoulder_tilt_deg) < 1
    assert abs(features.torso_tilt_deg) < 1
    # 팔이 굽어 있으므로 정상 범위(150~180)를 벗어난다
    assert features.draw_elbow_angle_deg < 150


def test_pose_endpoint_returns_features():
    with TestClient(create_app()) as client:
        resp = client.post(
            "/api/v1/sensors/pose",
            json={"session_id": "s1", "handedness": "right", "landmarks": _base_landmarks()},
        )
        assert resp.status_code == 202
        assert "features" in resp.json()


def test_frame_syncs_uploaded_pose_and_flags_issue():
    with TestClient(create_app()) as client:
        client.post(
            "/api/v1/sensors/pose",
            json={"session_id": "s2", "handedness": "right", "landmarks": _base_landmarks()},
        )
        resp = client.post("/api/v1/unity/frames", json={"session_id": "s2", "state": "AIM"})
        assert resp.status_code == 200
        feedback = resp.json()["feedback"]
        # 팔꿈치 위반이 동기화되어 잡혀야 한다
        assert feedback["code"] == "DRAW_ELBOW"


def _with_ankles(front_y: float, rear_y: float) -> list[PoseLandmark]:
    """
    오른손잡이: 앞발=왼발목(27), 뒷발=오른발목(28). 발은 좌우로 벌어지고,
    앞발의 상하(전후) 편차가 스탠스 유형을 가른다. 어깨선은 base 에서 수평.
    """
    lm = [PoseLandmark(**p) for p in _base_landmarks()]
    lm[27] = PoseLandmark(x=0.45, y=front_y)  # left ankle (front)
    lm[28] = PoseLandmark(x=0.55, y=rear_y)   # right ankle (rear)
    return lm


def test_classify_square_stance():
    f = landmarks_to_features(_with_ankles(0.90, 0.90), "right")
    assert abs(f.front_back_offset_ratio) < 0.08
    assert classify_stance(f) is StanceType.SQUARE


def test_classify_open_stance():
    # 앞발이 뒤로(이미지에서 아래로) -> OPEN
    f = landmarks_to_features(_with_ankles(0.95, 0.90), "right")
    assert classify_stance(f) is StanceType.OPEN


def test_classify_closed_stance():
    # 앞발이 앞으로(이미지에서 위로) -> CLOSED
    f = landmarks_to_features(_with_ankles(0.85, 0.90), "right")
    assert classify_stance(f) is StanceType.CLOSED


def test_pose_landmarks_require_33_points():
    with TestClient(create_app()) as client:
        resp = client.post(
            "/api/v1/sensors/pose",
            json={"session_id": "s3", "landmarks": [{"x": 0.1, "y": 0.1}]},
        )
        assert resp.status_code == 422


def test_hand_draw_strength_grows_with_wrist_distance():
    near = [PoseLandmark(**p) for p in _base_landmarks()]
    far = [PoseLandmark(**p) for p in _base_landmarks()]
    near[15] = PoseLandmark(x=0.45, y=0.5)
    near[16] = PoseLandmark(x=0.55, y=0.5)
    far[15] = PoseLandmark(x=0.25, y=0.5)
    far[16] = PoseLandmark(x=0.75, y=0.5)

    near_strength, _, near_valid = hand_draw_strength(near)
    far_strength, _, far_valid = hand_draw_strength(far)

    assert near_valid and far_valid
    assert far_strength > near_strength


def test_flutter_trial_and_nano_release_websocket_contract():
    session_id = "pose-trial"
    with TestClient(create_app()) as client:
        with client.websocket_connect(f"/ws/unity/{session_id}") as websocket:
            started = client.post(
                "/api/v1/pose-trials/start",
                json={"session_id": session_id},
            )
            assert started.json()["trial_state"] == "ACTIVE_DRAW"
            assert websocket.receive_json()["payload"]["action"] == "start"

            pose = client.post(
                "/api/v1/sensors/pose",
                json={"session_id": session_id, "landmarks": _base_landmarks()},
            )
            assert pose.json()["trial_state"] == "ACTIVE_DRAW"
            update = websocket.receive_json()
            assert update["type"] == "pose_draw_update"
            assert update["payload"]["action"] == "update"

            client.post(
                "/api/v1/test/bow-input",
                json={
                    "session_id": session_id,
                    "button_pressed": True,
                    "draw_value": 0,
                },
            )
            release = websocket.receive_json()
            assert release["payload"]["action"] == "release"
            assert release["payload"]["state"] == "FIRED"

            finished = client.post(
                "/api/v1/pose-trials/finish",
                json={"session_id": session_id},
            )
            assert finished.json()["previous_state"] == "FIRED"
