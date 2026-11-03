import 'dart:convert';

import 'package:google_mlkit_pose_detection/google_mlkit_pose_detection.dart';
import 'package:http/http.dart' as http;

/// Unity Archery_Demo 씬 및 Nano 33 IoT 게이트웨이와 공유하는 기본 세션입니다.
const String kArtemisSessionId = 'practice-001';

/// Android 기기는 `adb reverse tcp:8000 tcp:8000`을 통해 USB로만 서버에 연결한다.
/// 휴대폰의 127.0.0.1:8000이 USB를 거쳐 Windows의 127.0.0.1:8000으로 전달된다.
const String kArtemisUsbServerUrl = 'http://127.0.0.1:8000';

/// ML Kit PoseLandmarkType 을 MediaPipe BlazePose 33개 인덱스 순서로 정렬한다.
/// (서버 pose.py 의 인덱스 규격과 1:1로 맞춘다. 0=nose, 11=leftShoulder ...)
const List<PoseLandmarkType> kMediaPipeOrder = [
  PoseLandmarkType.nose,
  PoseLandmarkType.leftEyeInner,
  PoseLandmarkType.leftEye,
  PoseLandmarkType.leftEyeOuter,
  PoseLandmarkType.rightEyeInner,
  PoseLandmarkType.rightEye,
  PoseLandmarkType.rightEyeOuter,
  PoseLandmarkType.leftEar,
  PoseLandmarkType.rightEar,
  PoseLandmarkType.leftMouth,
  PoseLandmarkType.rightMouth,
  PoseLandmarkType.leftShoulder,
  PoseLandmarkType.rightShoulder,
  PoseLandmarkType.leftElbow,
  PoseLandmarkType.rightElbow,
  PoseLandmarkType.leftWrist,
  PoseLandmarkType.rightWrist,
  PoseLandmarkType.leftPinky,
  PoseLandmarkType.rightPinky,
  PoseLandmarkType.leftIndex,
  PoseLandmarkType.rightIndex,
  PoseLandmarkType.leftThumb,
  PoseLandmarkType.rightThumb,
  PoseLandmarkType.leftHip,
  PoseLandmarkType.rightHip,
  PoseLandmarkType.leftKnee,
  PoseLandmarkType.rightKnee,
  PoseLandmarkType.leftAnkle,
  PoseLandmarkType.rightAnkle,
  PoseLandmarkType.leftHeel,
  PoseLandmarkType.rightHeel,
  PoseLandmarkType.leftFootIndex,
  PoseLandmarkType.rightFootIndex,
];

/// 감지된 Pose 를 서버가 기대하는 정규화(0~1) 좌표 33개 리스트로 변환한다.
List<Map<String, double>> poseToLandmarks(
  Pose pose,
  double imageWidth,
  double imageHeight,
) {
  return kMediaPipeOrder.map((type) {
    final lm = pose.landmarks[type];
    if (lm == null) {
      return {'x': 0.0, 'y': 0.0, 'z': 0.0, 'visibility': 0.0};
    }
    return {
      'x': imageWidth > 0 ? lm.x / imageWidth : 0.0,
      'y': imageHeight > 0 ? lm.y / imageHeight : 0.0,
      'z': lm.z,
      'visibility': lm.likelihood,
    };
  }).toList();
}

/// 서버 /sensors/pose 로 좌표를 전송한다.
class PoseSender {
  PoseSender({
    this.sessionId = kArtemisSessionId,
    this.handedness = 'right',
  });

  final String sessionId;
  final String handedness;

  Future<SendResult> startTrial() => _sendTrialCommand('start');

  Future<SendResult> finishTrial() => _sendTrialCommand('finish');

  Future<SendResult> _sendTrialCommand(String action) async {
    final uri = Uri.parse('$kArtemisUsbServerUrl/api/v1/pose-trials/$action');
    try {
      final resp = await http
          .post(
            uri,
            headers: {'Content-Type': 'application/json'},
            body: jsonEncode({'session_id': sessionId}),
          )
          .timeout(const Duration(seconds: 3));
      if (resp.statusCode >= 200 && resp.statusCode < 300) {
        return SendResult.ok(jsonDecode(resp.body) as Map<String, dynamic>);
      }
      return SendResult.error('HTTP ${resp.statusCode}: ${resp.body}');
    } catch (e) {
      return SendResult.error('network: $e');
    }
  }

  Future<SendResult> send(List<Map<String, double>> landmarks) async {
    final uri = Uri.parse('$kArtemisUsbServerUrl/api/v1/sensors/pose');
    try {
      final resp = await http
          .post(
            uri,
            headers: {'Content-Type': 'application/json'},
            body: jsonEncode({
              'session_id': sessionId,
              'handedness': handedness,
              'landmarks': landmarks,
            }),
          )
          .timeout(const Duration(seconds: 3));
      if (resp.statusCode == 202) {
        return SendResult.ok(jsonDecode(resp.body) as Map<String, dynamic>);
      }
      // 서버까지는 닿았지만 거절됨(예: 422 검증 실패). body 에 이유가 있다.
      return SendResult.error('HTTP ${resp.statusCode}: ${resp.body}');
    } catch (e) {
      // 네트워크 자체 실패(연결 거부/타임아웃/주소 오류 등).
      return SendResult.error('network: $e');
    }
  }
}

/// 전송 결과. 실패 시 원인을 담아 UI/로그에서 구분할 수 있게 한다.
class SendResult {
  SendResult.ok(this.body) : error = null;
  SendResult.error(this.error) : body = null;

  final Map<String, dynamic>? body;
  final String? error;

  bool get isOk => error == null;
}
