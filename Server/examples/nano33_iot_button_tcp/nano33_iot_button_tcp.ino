#include <SPI.h>
#include <WiFiNINA.h>

#include "arduino_secrets.h"

constexpr int BUTTON_PIN = 2;
constexpr unsigned long DEBOUNCE_MS = 40;
constexpr unsigned long RECONNECT_MS = 1000;

// FastAPI 서버 PC의 LAN IP로 변경하세요.
IPAddress serverIp(192, 0, 0, 2);
constexpr uint16_t SERVER_PORT = 8765;
const char SESSION_ID[] = "practice-001";

WiFiClient client;
int stableButtonState = HIGH;
int lastReading = HIGH;
unsigned long lastChangeAt = 0;
unsigned long lastConnectAttemptAt = 0;

void connectWifi() {
  if (WiFi.status() == WL_NO_MODULE) {
    Serial.println("[ERROR] WiFiNINA module not found");
    while (true) {
      digitalWrite(LED_BUILTIN, HIGH);
      delay(200);
      digitalWrite(LED_BUILTIN, LOW);
      delay(200);
    }
  }

  while (WiFi.status() != WL_CONNECTED) {
    Serial.print("[WiFi] connecting to: ");
    Serial.println(SECRET_SSID);
    WiFi.begin(SECRET_SSID, SECRET_PASS);
    delay(2000);
  }

  Serial.print("[WiFi] connected, Nano IP: ");
  Serial.println(WiFi.localIP());
}

void ensureTcpConnected() {
  if (client.connected()) {
    return;
  }

  const unsigned long now = millis();
  if (now - lastConnectAttemptAt < RECONNECT_MS) {
    return;
  }

  lastConnectAttemptAt = now;
  client.stop();
  Serial.print("[TCP] connecting to ");
  Serial.print(serverIp);
  Serial.print(":");
  Serial.println(SERVER_PORT);

  if (client.connect(serverIp, SERVER_PORT)) {
    Serial.println("[TCP] connected");
    digitalWrite(LED_BUILTIN, HIGH);
  } else {
    Serial.println("[TCP] connection failed; retrying");
    digitalWrite(LED_BUILTIN, LOW);
  }
}

void sendPress() {
  if (!client.connected()) {
    Serial.println("[BUTTON] press detected, but TCP is disconnected");
    return;
  }

  client.print("{\"session_id\":\"");
  client.print(SESSION_ID);
  client.println("\",\"event\":\"press\"}");
  Serial.println("[BUTTON] press sent");
}

void readServerResponses() {
  while (client.available()) {
    String response = client.readStringUntil('\n');
    Serial.print("[SERVER] ");
    Serial.println(response);
  }
}

void setup() {
  pinMode(BUTTON_PIN, INPUT_PULLUP);
  pinMode(LED_BUILTIN, OUTPUT);
  digitalWrite(LED_BUILTIN, LOW);
  Serial.begin(115200);
  delay(1000);

  Serial.println();
  Serial.println("[Artemis Nano] starting");
  connectWifi();
}

void loop() {
  if (WiFi.status() != WL_CONNECTED) {
    connectWifi();
  }
  ensureTcpConnected();
  readServerResponses();

  const int reading = digitalRead(BUTTON_PIN);
  if (reading != lastReading) {
    lastChangeAt = millis();
    lastReading = reading;
  }

  if (millis() - lastChangeAt >= DEBOUNCE_MS &&
      reading != stableButtonState) {
    stableButtonState = reading;
    if (stableButtonState == LOW) {
      sendPress();
    }
  }
}
