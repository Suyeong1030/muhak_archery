# artemis_pose (Flutter)

MediaPipe(ML Kit) 포즈 스파이크 앱. 카메라로 33개 랜드마크를 뽑아
Artemis 서버 `POST /api/v1/sensors/pose` 로 전송한다. 각도 계산은 서버가 담당(설계 B안).

## 구조
- `lib/pose_sender.dart` — ML Kit 랜드마크를 MediaPipe 33 인덱스 순서로 정렬 + 서버 전송
- `lib/main.dart` — 카메라 스트림 → 포즈 감지 → USB 전송 상태 표시

## 아이폰 무선 빌드
1. 최초 1회만 USB 연결 → Xcode `Window > Devices and Simulators`에서
   기기 선택 후 **"Connect via network"** 체크. 이후 무선.
2. Xcode로 `ios/Runner.xcworkspace` 열어 Signing & Capabilities 에서
   본인 Apple ID(Team) + Bundle ID 설정 (무료 계정은 앱 7일 만료).
3. 같은 와이파이에서:
   ```
   flutter devices        # 네트워크 기기 확인
   flutter run -d <기기>
   ```

## 서버 연결
- 갤럭시와 Windows를 USB로 연결하고 USB 디버깅을 허용한다.
- 앱은 주소 입력 없이 `http://127.0.0.1:8000`만 사용한다.
- 앱 실행 전 Windows에서 포트 리버스를 설정한다:

  ```powershell
  adb reverse tcp:8000 tcp:8000
  ```

- 이 연결은 Wi-Fi나 핫스팟을 사용하지 않는다. USB를 뽑거나 기기를 재연결하면
  `adb reverse` 명령을 다시 실행한다.
- 서버는 외부 접근 위해 `0.0.0.0` 바인딩으로 실행:
  ```
  cd ../Server && uvicorn app.main:app --host 0.0.0.0 --port 8000
  ```

## 스파이크 확인 포인트
"감지된 랜드마크: 33 / 33" 이 뜨고 "서버 응답: 각도: {...}" 에 값이 오면
카메라→좌표→서버→각도 변환까지 관통된 것. 여기까지 되면 나머지는 쉽다.

## 참고
- iOS 최소 15.5 (ML Kit 요구). `ios/Podfile` 에 설정됨.
- Android minSdk 21+ 필요할 수 있음(ML Kit).
