import 'dart:io';
import 'dart:typed_data';

import 'package:camera/camera.dart';
import 'package:flutter/material.dart';
import 'package:google_mlkit_pose_detection/google_mlkit_pose_detection.dart';

import 'pose_painter.dart';
import 'pose_sender.dart';

late List<CameraDescription> _cameras;

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  _cameras = await availableCameras();
  runApp(const ArtemisApp());
}

class ArtemisApp extends StatelessWidget {
  const ArtemisApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'Artemis Pose',
      theme: ThemeData.dark(useMaterial3: true),
      home: const PoseSpikePage(),
    );
  }
}

/// 스파이크 화면: 카메라 -> ML Kit 포즈 -> 33 랜드마크 -> 서버 전송.
/// 목표는 "좌표 33개가 실제로 잡혀서 서버로 나가는지" 확인하는 것.
class PoseSpikePage extends StatefulWidget {
  const PoseSpikePage({super.key});

  @override
  State<PoseSpikePage> createState() => _PoseSpikePageState();
}

class _PoseSpikePageState extends State<PoseSpikePage> {
  CameraController? _controller;
  final _detector = PoseDetector(options: PoseDetectorOptions());

  bool _busy = false;
  bool _streaming = false;
  int _detectedCount = 0;
  String _lastServer = '-';
  String _stanceType = '-';
  String _trialState = 'IDLE';
  double _drawStrength = 0;
  int _completedTrials = 0;
  int _requiredTrials = 3;

  // 오버레이용: 마지막으로 감지된 포즈 + 이미지 메타데이터
  Pose? _lastPose;
  Size _lastImageSize = Size.zero;
  InputImageRotation _lastRotation = InputImageRotation.rotation0deg;

  @override
  void initState() {
    super.initState();
    _initCamera();
  }

  Future<void> _initCamera() async {
    final back = _cameras.firstWhere(
      (c) => c.lensDirection == CameraLensDirection.back,
      orElse: () => _cameras.first,
    );
    final controller = CameraController(
      back,
      ResolutionPreset.medium,
      enableAudio: false,
      imageFormatGroup: Platform.isIOS
          ? ImageFormatGroup.bgra8888
          : ImageFormatGroup.nv21,
    );
    await controller.initialize();
    if (!mounted) return;
    setState(() => _controller = controller);
  }

  Future<void> _toggleStream() async {
    final controller = _controller;
    if (controller == null) return;
    if (_streaming) {
      final result = await _sender().finishTrial();
      if (!result.isOk) {
        if (mounted) setState(() => _lastServer = 'Trial 종료 실패: ${result.error}');
        return;
      }
      await controller.stopImageStream();
      if (mounted) {
        setState(() {
          _streaming = false;
          _trialState = 'IDLE';
          _drawStrength = 0;
          _completedTrials =
              (result.body?['completed_trials'] as num?)?.toInt() ??
                  _completedTrials;
          _requiredTrials =
              (result.body?['required_trials'] as num?)?.toInt() ??
                  _requiredTrials;
          final previousState = '${result.body?['previous_state']}';
          final polarSamples =
              (result.body?['polar_samples_collected'] as num?)?.toInt() ?? 0;
          _lastServer = previousState == 'FIRED'
              ? 'Trial completed ($_completedTrials/$_requiredTrials) · Polar $polarSamples samples'
              : 'Trial closed without a shot';
        });
      }
      return;
    }
    final result = await _sender().startTrial();
    if (!result.isOk) {
      if (mounted) setState(() => _lastServer = 'Trial 시작 실패: ${result.error}');
      return;
    }
    await controller.startImageStream(_onFrame);
    if (mounted) {
      setState(() {
        _streaming = true;
        _trialState = 'ACTIVE_DRAW';
        _drawStrength = 0;
      });
    }
  }

  int _frameCount = 0;

  Future<void> _onFrame(CameraImage image) async {
    if (_busy) return;
    _busy = true;
    try {
      _frameCount++;
      final inputImage = _toInputImage(image);
      if (inputImage == null) {
        debugPrint('[POSE] inputImage null — 포맷 변환 실패 '
            '(format.raw=${image.format.raw}, planes=${image.planes.length})');
        if (mounted) setState(() => _lastServer = '이미지 포맷 변환 실패');
        return;
      }

      List<Pose> poses;
      try {
        poses = await _detector.processImage(inputImage);
      } catch (e) {
        debugPrint('[POSE] 감지기 예외: $e');
        if (mounted) setState(() => _lastServer = '감지기 오류: $e');
        return;
      }

      // 로그 폭주 방지: 10프레임마다 한 번만 출력.
      if (_frameCount % 10 == 0) {
        debugPrint('[POSE] frame#$_frameCount poses=${poses.length}');
      }

      if (poses.isEmpty) {
        if (mounted) {
          setState(() {
            _detectedCount = 0;
            _lastPose = null; // 오버레이 지움
          });
        }
        return;
      }

      final landmarks = poseToLandmarks(
        poses.first,
        image.width.toDouble(),
        image.height.toDouble(),
      );
      final result = await _sender().send(landmarks);
      debugPrint('[SEND] ${result.isOk ? "OK stance=${result.body?["stance_type"]} ${result.body?["features"]}" : "FAIL ${result.error}"}');
      if (mounted) {
        setState(() {
          _detectedCount = poses.first.landmarks.length;
          _lastPose = poses.first;
          _lastImageSize = Size(image.width.toDouble(), image.height.toDouble());
          _lastServer = result.isOk ? '각도: ${result.body!['features']}' : '전송 실패: ${result.error}';
          if (result.isOk) {
            _stanceType = '${result.body!['stance_type']}';
            _trialState = '${result.body!['trial_state']}';
            _drawStrength =
                (result.body!['draw_strength'] as num?)?.toDouble() ?? 0;
          }
        });
      }
    } finally {
      _busy = false;
    }
  }

  PoseSender _sender() => PoseSender();

  Color _stanceColor(String stance) {
    switch (stance) {
      case 'SQUARE':
        return Colors.greenAccent;
      case 'OPEN':
        return Colors.orangeAccent;
      case 'CLOSED':
        return Colors.lightBlueAccent;
      default:
        return Colors.grey;
    }
  }

  /// 모델과 무관하게 "서버로 JSON 전송" 경로만 검증한다.
  /// 더미 33개 랜드마크를 보내 서버 각도 응답이 오는지 확인.
  Future<void> _testSend() async {
    final dummy = List.generate(33, (i) => {'x': 0.5, 'y': 0.5, 'z': 0.0, 'visibility': 1.0});
    dummy[12] = {'x': 0.6, 'y': 0.4, 'z': 0.0, 'visibility': 1.0}; // 값 차이 하나
    final result = await _sender().send(dummy);
    debugPrint('[TEST-SEND] ${result.isOk ? "OK ${result.body}" : "FAIL ${result.error}"}');
    if (mounted) {
      setState(() {
        _lastServer = result.isOk
            ? '테스트 전송 OK — 각도: ${result.body!['features']}'
            : '테스트 전송 실패: ${result.error}';
        if (result.isOk) _stanceType = '${result.body!['stance_type']}';
      });
    }
  }

  /// CameraImage -> ML Kit InputImage. 플랫폼별 포맷/회전 처리.
  InputImage? _toInputImage(CameraImage image) {
    final camera = _controller!.description;
    final rotation = InputImageRotationValue.fromRawValue(camera.sensorOrientation) ??
        InputImageRotation.rotation0deg;
    _lastRotation = rotation; // 오버레이 좌표 변환에 사용
    final format = InputImageFormatValue.fromRawValue(image.format.raw);
    if (format == null) return null;

    final bytes = image.planes.fold<List<int>>(
      <int>[],
      (acc, plane) => acc..addAll(plane.bytes),
    );

    return InputImage.fromBytes(
      bytes: Uint8List.fromList(bytes),
      metadata: InputImageMetadata(
        size: Size(image.width.toDouble(), image.height.toDouble()),
        rotation: rotation,
        format: format,
        bytesPerRow: image.planes.first.bytesPerRow,
      ),
    );
  }

  @override
  void dispose() {
    _controller?.dispose();
    _detector.close();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final controller = _controller;
    return Scaffold(
      appBar: AppBar(title: const Text('Artemis Pose 스파이크')),
      body: SafeArea(
        child: Column(
          children: [
            // === 컨트롤 패널 (항상 화면 상단에 고정, 카메라에 안 가려짐) ===
            Padding(
              padding: const EdgeInsets.all(12),
              child: Column(
                children: [
                  const Align(
                    alignment: Alignment.centerLeft,
                    child: Text(
                      'USB 연결 · Session: $kArtemisSessionId',
                    ),
                  ),
                  const SizedBox(height: 8),
                  Row(
                    children: [
                      Expanded(
                        child: FilledButton(
                          onPressed: controller == null ? null : _toggleStream,
                          child: Text(
                            _streaming ? '트라이얼 종료' : '트라이얼 시작',
                          ),
                        ),
                      ),
                      const SizedBox(width: 8),
                      Expanded(
                        child: OutlinedButton(
                          onPressed: _testSend,
                          child: const Text('서버 연결 테스트'),
                        ),
                      ),
                    ],
                  ),
                  const SizedBox(height: 6),
                  Row(
                    mainAxisAlignment: MainAxisAlignment.center,
                    children: [
                      const Text('스탠스: '),
                      Text(
                        _stanceType,
                        style: TextStyle(
                          fontSize: 22,
                          fontWeight: FontWeight.bold,
                          color: _stanceColor(_stanceType),
                        ),
                      ),
                    ],
                  ),
                  const SizedBox(height: 4),
                  Text('감지된 랜드마크: $_detectedCount / 33'),
                  Text(
                    'Trial: $_trialState · Draw: ${(_drawStrength * 100).round()}%',
                  ),
                  Text('Completed trials: $_completedTrials / $_requiredTrials'),
                  Text('서버 응답: $_lastServer',
                      maxLines: 2, overflow: TextOverflow.ellipsis),
                ],
              ),
            ),
            // === 카메라 프리뷰 (남는 공간 차지) ===
            if (controller != null && controller.value.isInitialized)
              Expanded(
                child: ClipRect(
                  child: Stack(
                    fit: StackFit.expand,
                    children: [
                      FittedBox(
                        // 원본 비율을 유지하며 영역을 꽉 채운다(왜곡 없음).
                        fit: BoxFit.cover,
                        child: SizedBox(
                          // previewSize 는 가로/세로가 뒤바뀐 값이라 세로 화면에선 swap 한다.
                          width: controller.value.previewSize!.height,
                          height: controller.value.previewSize!.width,
                          child: CameraPreview(controller),
                        ),
                      ),
                      // 감지된 관절 스켈레톤 오버레이
                      CustomPaint(
                        painter: PosePainter(
                          pose: _lastPose,
                          imageSize: _lastImageSize,
                          rotation: _lastRotation,
                          lensDirection: controller.description.lensDirection,
                        ),
                      ),
                    ],
                  ),
                ),
              )
            else
              const Expanded(child: Center(child: CircularProgressIndicator())),
          ],
        ),
      ),
    );
  }
}
