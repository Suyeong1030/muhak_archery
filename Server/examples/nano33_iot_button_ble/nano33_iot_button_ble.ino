#include <ArduinoBLE.h>

constexpr int BUTTON_PIN = 2;
constexpr unsigned long DEBOUNCE_MS = 40;

BLEService bowService("7b1e0001-6a5b-4f5c-9d7a-7c9f2e4a1001");
BLEByteCharacteristic buttonCharacteristic(
  "7b1e0002-6a5b-4f5c-9d7a-7c9f2e4a1001",
  BLERead | BLENotify
);

int stableButtonState = HIGH;
int lastReading = HIGH;
unsigned long lastChangeAt = 0;

void setup() {
  pinMode(BUTTON_PIN, INPUT_PULLUP);
  pinMode(LED_BUILTIN, OUTPUT);
  Serial.begin(115200);
  delay(1000);

  if (!BLE.begin()) {
    Serial.println("[ERROR] BLE initialization failed");
    while (true) {
      digitalWrite(LED_BUILTIN, !digitalRead(LED_BUILTIN));
      delay(200);
    }
  }

  BLE.setLocalName("ArtemisBow");
  BLE.setDeviceName("ArtemisBow");
  BLE.setAdvertisedService(bowService);
  bowService.addCharacteristic(buttonCharacteristic);
  BLE.addService(bowService);
  buttonCharacteristic.writeValue((byte)0);
  BLE.advertise();

  Serial.println("[Artemis Bow BLE] advertising as ArtemisBow");
}

void loop() {
  BLEDevice central = BLE.central();
  if (!central) {
    return;
  }

  Serial.print("[BLE] connected: ");
  Serial.println(central.address());
  digitalWrite(LED_BUILTIN, HIGH);

  while (central.connected()) {
    BLE.poll();
    const int reading = digitalRead(BUTTON_PIN);

    if (reading != lastReading) {
      lastChangeAt = millis();
      lastReading = reading;
    }

    if (millis() - lastChangeAt >= DEBOUNCE_MS &&
        reading != stableButtonState) {
      stableButtonState = reading;
      if (stableButtonState == LOW) {
        buttonCharacteristic.writeValue((byte)1);
        Serial.println("[BUTTON] press notified");
      } else {
        buttonCharacteristic.writeValue((byte)0);
      }
    }
  }

  digitalWrite(LED_BUILTIN, LOW);
  Serial.println("[BLE] disconnected; advertising continues");
}
