import 'package:flutter_test/flutter_test.dart';

import 'package:artemis_pose/pose_sender.dart';

void main() {
  test('MediaPipe 인덱스 순서는 33개이고 어깨 인덱스가 규격과 맞는다', () {
    expect(kMediaPipeOrder.length, 33);
    // 서버 pose.py 규격: 11=leftShoulder, 12=rightShoulder
    expect(kMediaPipeOrder[11].name, contains('leftShoulder'));
    expect(kMediaPipeOrder[12].name, contains('rightShoulder'));
  });
}
