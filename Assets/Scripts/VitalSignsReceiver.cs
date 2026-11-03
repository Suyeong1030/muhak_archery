using System;
using UnityEngine;

/// <summary>
/// ArtemisServerTest가 수신한 sensor_update JSON을 분석합니다.
///
/// 지원 센서:
/// - polar_h10: 심박수
/// - respiration: 호흡수, 호흡 단계, 정규화 파형
///
/// 서버가 아직 준비되지 않은 경우에는
/// Context Menu 또는 RespirationTestInput을 통해
/// 테스트 데이터를 직접 주입할 수 있습니다.
/// </summary>
public class VitalSignsReceiver : MonoBehaviour
{
    [Header("Server")]
    [SerializeField]
    private ArtemisServerTest artemisServer;

    [Header("Debug")]
    [SerializeField]
    private bool showSensorUpdateLogs = false;


    // =========================================================
    // 심박 이벤트
    // =========================================================

    /// <summary>
    /// 새로운 심박수가 들어왔을 때 발생합니다.
    /// 단위: BPM
    /// </summary>
    public event Action<float> HeartRateUpdated;


    // =========================================================
    // 호흡 이벤트
    // =========================================================

    /// <summary>
    /// 새로운 호흡 데이터가 들어왔을 때 발생합니다.
    ///
    /// 매개변수:
    /// 1. respiratoryRateBpm: 분당 호흡수
    /// 2. phase: inhale, exhale 또는 hold
    /// 3. normalizedWave: 0~1 범위 호흡 파형
    /// </summary>
    public event Action<float, string, float>
        RespirationUpdated;


    // =========================================================
    // JSON 구조
    // =========================================================

    [Serializable]
    private class SensorMessage
    {
        public string type;
        public string sensor;
        public SensorPayload payload;
    }


    /// <summary>
    /// JsonUtility는 메시지 종류별로 서로 다른 Payload 클래스를
    /// 자동 선택하지 못하므로, 필요한 센서 필드를 하나의 Payload에
    /// 함께 선언합니다.
    /// </summary>
    [Serializable]
    private class SensorPayload
    {
        // Polar H10
        public float heart_rate_bpm;

        // Respiration
        public float respiratory_rate_bpm;
        public string phase;
        public float normalized_wave;
    }


    // =========================================================
    // Unity 생명주기
    // =========================================================

    private void Awake()
    {
        if (artemisServer == null)
        {
            artemisServer =
                FindFirstObjectByType<ArtemisServerTest>();
        }
    }


    private void OnEnable()
    {
        if (artemisServer != null)
        {
            artemisServer.RawMessageReceived +=
                HandleRawMessage;
        }
        else
        {
            Debug.LogWarning(
                "[VitalSignsReceiver] " +
                "ArtemisServerTest를 찾지 못했습니다. " +
                "테스트 입력은 사용할 수 있습니다.",
                this
            );
        }
    }


    private void OnDisable()
    {
        if (artemisServer != null)
        {
            artemisServer.RawMessageReceived -=
                HandleRawMessage;
        }
    }


    // =========================================================
    // 서버 JSON 처리
    // =========================================================

    /// <summary>
    /// 서버에서 받은 원본 JSON을 분석합니다.
    /// </summary>
    public void HandleRawMessage(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            SensorMessage message =
                JsonUtility.FromJson<SensorMessage>(
                    json
                );

            if (
                message == null ||
                message.type != "sensor_update" ||
                message.payload == null
            )
            {
                return;
            }

            if (message.sensor == "polar_h10")
            {
                PublishHeartRate(
                    message.payload.heart_rate_bpm
                );

                return;
            }

            if (message.sensor == "respiration")
            {
                PublishRespiration(
                    message.payload
                        .respiratory_rate_bpm,

                    message.payload.phase,

                    message.payload
                        .normalized_wave
                );
            }
        }
        catch (Exception exception)
        {
            Debug.LogError(
                "[VitalSignsReceiver] JSON 처리 실패: "
                + exception.Message,
                this
            );
        }
    }


    // =========================================================
    // 데이터 발행
    // =========================================================

    private void PublishHeartRate(
        float heartRateBpm)
    {
        if (
            float.IsNaN(heartRateBpm) ||
            float.IsInfinity(heartRateBpm) ||
            heartRateBpm <= 0f
        )
        {
            Debug.LogWarning(
                "[VitalSignsReceiver] " +
                "유효하지 않은 심박수입니다: " +
                heartRateBpm,
                this
            );

            return;
        }

        if (showSensorUpdateLogs)
        {
            Debug.Log(
                "[VitalSignsReceiver] 심박수 수신: " +
                $"{heartRateBpm:F0} BPM",
                this
            );
        }

        HeartRateUpdated?.Invoke(
            heartRateBpm
        );
    }


    private void PublishRespiration(
        float respiratoryRateBpm,
        string phase,
        float normalizedWave)
    {
        if (
            float.IsNaN(respiratoryRateBpm) ||
            float.IsInfinity(respiratoryRateBpm) ||
            respiratoryRateBpm <= 0f
        )
        {
            Debug.LogWarning(
                "[VitalSignsReceiver] " +
                "유효하지 않은 호흡수입니다: " +
                respiratoryRateBpm,
                this
            );

            return;
        }

        string safePhase =
            NormalizeRespirationPhase(
                phase
            );

        float safeWave =
            Mathf.Clamp01(
                normalizedWave
            );

        if (showSensorUpdateLogs)
        {
            Debug.Log(
                "[VitalSignsReceiver] 호흡 데이터 수신"
                + $"\nRate: {respiratoryRateBpm:F1} BPM"
                + $"\nPhase: {safePhase}"
                + $"\nWave: {safeWave:F2}",
                this
            );
        }

        RespirationUpdated?.Invoke(
            respiratoryRateBpm,
            safePhase,
            safeWave
        );
    }


    private static string NormalizeRespirationPhase(
        string phase)
    {
        if (string.IsNullOrWhiteSpace(phase))
        {
            return "unknown";
        }

        string normalized =
            phase.Trim().ToLowerInvariant();

        switch (normalized)
        {
            case "inhale":
            case "inspiration":
            case "in":
                return "inhale";

            case "exhale":
            case "expiration":
            case "out":
                return "exhale";

            case "hold":
            case "pause":
                return "hold";

            default:
                return normalized;
        }
    }


    // =========================================================
    // 테스트 입력
    // =========================================================

    /// <summary>
    /// 다른 테스트 스크립트에서 서버 없이
    /// 호흡 데이터를 직접 넣을 때 사용합니다.
    /// </summary>
    public void InjectTestRespiration(
        float respiratoryRateBpm,
        string phase,
        float normalizedWave)
    {
        PublishRespiration(
            respiratoryRateBpm,
            phase,
            normalizedWave
        );
    }


    /// <summary>
    /// Inspector의 Component 메뉴에서
    /// 단일 심박 JSON 수신을 시험합니다.
    /// </summary>
    [ContextMenu("Test Heart Rate JSON")]
    private void TestHeartRateJson()
    {
        const string testJson =
            "{\"type\":\"sensor_update\"," +
            "\"sensor\":\"polar_h10\"," +
            "\"payload\":{\"heart_rate_bpm\":75}}";

        HandleRawMessage(
            testJson
        );
    }


    /// <summary>
    /// Inspector의 Component 메뉴에서
    /// 단일 호흡 JSON 수신을 시험합니다.
    /// </summary>
    [ContextMenu("Test Respiration JSON")]
    private void TestRespirationJson()
    {
        const string testJson =
            "{\"type\":\"sensor_update\"," +
            "\"sensor\":\"respiration\"," +
            "\"payload\":{" +
            "\"respiratory_rate_bpm\":14.0," +
            "\"phase\":\"inhale\"," +
            "\"normalized_wave\":0.65" +
            "}}";

        HandleRawMessage(
            testJson
        );
    }
}
