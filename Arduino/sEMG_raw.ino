#include <ArduinoBLE.h>

const char* TARGET_NAME = "MyoWareSensor1";

const char* EMG_UUID =
  "f3a56edf-8f1e-4533-93bf-5601b2e91308";

void setup()
{
  Serial.begin(115200);
  delay(1000);

  if (!BLE.begin())
  {
    while (1);
  }

  BLE.scan();
}

void loop()
{
  BLEDevice peripheral = BLE.available();

  if (!peripheral)
    return;

  if (peripheral.localName() != TARGET_NAME)
    return;

  BLE.stopScan();

  if (!peripheral.connect())
  {
    BLE.scan();
    return;
  }

  if (!peripheral.discoverAttributes())
  {
    peripheral.disconnect();
    BLE.scan();
    return;
  }

  BLECharacteristic emg =
    peripheral.characteristic(EMG_UUID);

  if (!emg)
  {
    peripheral.disconnect();
    BLE.scan();
    return;
  }

  while (peripheral.connected())
  {
    if (emg.canRead())
    {
      char buffer[16];

      int len = emg.readValue(
        (uint8_t*)buffer,
        sizeof(buffer)
      );

      if (len > 0)
      {
        // MyoWare가 보내는 "0.00" 같은 문자열 그대로 전송
        Serial.write(
          (uint8_t*)buffer,
          len
        );

        // Unity에서 한 줄 단위로 구분하기 위한 newline
        Serial.println();
      }
    }

    BLE.poll();

    // 약 50Hz
    delay(20);
  }

  // 연결 끊기면 다시 스캔
  BLE.scan();
}