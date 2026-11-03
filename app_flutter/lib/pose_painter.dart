import 'dart:io';

import 'package:camera/camera.dart';
import 'package:flutter/material.dart';
import 'package:google_mlkit_pose_detection/google_mlkit_pose_detection.dart';

/// 감지된 Pose 를 카메라 프리뷰 위에 스켈레톤으로 그린다.
/// 이미지 좌표(ML Kit) -> 캔버스 좌표 변환은 회전/전면카메라 미러를 반영한다.
class PosePainter extends CustomPainter {
  PosePainter({
    required this.pose,
    required this.imageSize,
    required this.rotation,
    required this.lensDirection,
  });

  final Pose? pose;
  final Size imageSize;
  final InputImageRotation rotation;
  final CameraLensDirection lensDirection;

  // 스켈레톤 연결선 정의 (MediaPipe/ML Kit 관절 쌍)
  static const List<List<PoseLandmarkType>> _bones = [
    [PoseLandmarkType.leftShoulder, PoseLandmarkType.rightShoulder],
    [PoseLandmarkType.leftShoulder, PoseLandmarkType.leftElbow],
    [PoseLandmarkType.leftElbow, PoseLandmarkType.leftWrist],
    [PoseLandmarkType.rightShoulder, PoseLandmarkType.rightElbow],
    [PoseLandmarkType.rightElbow, PoseLandmarkType.rightWrist],
    [PoseLandmarkType.leftShoulder, PoseLandmarkType.leftHip],
    [PoseLandmarkType.rightShoulder, PoseLandmarkType.rightHip],
    [PoseLandmarkType.leftHip, PoseLandmarkType.rightHip],
    [PoseLandmarkType.leftHip, PoseLandmarkType.leftKnee],
    [PoseLandmarkType.leftKnee, PoseLandmarkType.leftAnkle],
    [PoseLandmarkType.rightHip, PoseLandmarkType.rightKnee],
    [PoseLandmarkType.rightKnee, PoseLandmarkType.rightAnkle],
  ];

  @override
  void paint(Canvas canvas, Size size) {
    final p = pose;
    if (p == null) return;

    final jointPaint = Paint()
      ..style = PaintingStyle.fill
      ..color = Colors.tealAccent;
    final bonePaint = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = 3
      ..color = Colors.tealAccent.withValues(alpha: 0.8);

    Offset? point(PoseLandmarkType type) {
      final lm = p.landmarks[type];
      if (lm == null) return null;
      return Offset(
        _translateX(lm.x, size),
        _translateY(lm.y, size),
      );
    }

    // 뼈대 선
    for (final bone in _bones) {
      final a = point(bone[0]);
      final b = point(bone[1]);
      if (a != null && b != null) canvas.drawLine(a, b, bonePaint);
    }

    // 관절 점
    for (final lm in p.landmarks.values) {
      canvas.drawCircle(
        Offset(_translateX(lm.x, size), _translateY(lm.y, size)),
        4,
        jointPaint,
      );
    }
  }

  double _translateX(double x, Size canvasSize) {
    double dx;
    switch (rotation) {
      case InputImageRotation.rotation90deg:
        dx = x * canvasSize.width / (Platform.isIOS ? imageSize.width : imageSize.height);
        break;
      case InputImageRotation.rotation270deg:
        dx = canvasSize.width -
            x * canvasSize.width / (Platform.isIOS ? imageSize.width : imageSize.height);
        break;
      default:
        dx = x * canvasSize.width / imageSize.width;
    }
    // 참고: iOS 전면 카메라는 이미 프리뷰가 미러링된 상태로 표시되어,
    // ML Kit 좌표를 그대로 쓰면 오히려 맞는다. 별도 미러 처리는 하지 않는다.
    return dx;
  }

  double _translateY(double y, Size canvasSize) {
    switch (rotation) {
      case InputImageRotation.rotation90deg:
      case InputImageRotation.rotation270deg:
        return y * canvasSize.height / (Platform.isIOS ? imageSize.height : imageSize.width);
      default:
        return y * canvasSize.height / imageSize.height;
    }
  }

  @override
  bool shouldRepaint(covariant PosePainter old) =>
      old.pose != pose || old.imageSize != imageSize;
}
