using System;
using UnityEngine;

/// <summary>
/// ArtemisServerTest가 받은 bow_input JSON을 해석하여
/// BowStringController의 외부 입력 함수에 전달합니다.
///
/// 서버 메시지 역할:
/// - button_pressed = true  → 시위 잡기 시작
/// - draw_value             → Strength 및 시위 위치 갱신
/// - button_pressed = false → 마지막 Strength로 발사
/// </summary>
public class ServerBowInputReceiver : MonoBehaviour
{
    private enum StrengthMode
    {
        Sensor,
        WizardOfOz
    }

    [Header("References")]

    [Tooltip("기존 WebSocket 통신을 담당하는 ArtemisServerTest")]
    [SerializeField]
    private ArtemisServerTest artemisServer;

    [Tooltip("외부 입력을 적용할 활시위")]
    [SerializeField]
    private BowStringController bowStringController;


    [Header("Input Mode")]

    [Tooltip(
        "Sensor: 서버 draw_value로 Strength 계산\n" +
        "WizardOfOz: 센서값을 무시하고 고정 Strength 사용"
    )]
    [SerializeField]
    private StrengthMode strengthMode =
        StrengthMode.Sensor;


    [Header("Sensor Calibration")]

    [Tooltip("활을 당기지 않은 상태의 draw_value")]
    [SerializeField]
    private float sensorMinimum = 200f;

    [Tooltip("활을 최대로 당긴 상태의 draw_value")]
    [SerializeField]
    private float sensorMaximum = 600f;

    [Tooltip("센서값이 감소할수록 더 많이 당긴 구조라면 체크합니다.")]
    [SerializeField]
    private bool invertInput = false;

    [Tooltip(
        "Strength 변화가 부드러워지는 시간입니다. " +
        "0이면 보간 없이 즉시 적용합니다."
    )]
    [Min(0f)]
    [SerializeField]
    private float smoothingTime = 0.05f;


    [Header("Wizard Of Oz")]

    [Tooltip("WizardOfOz 모드에서 사용할 고정 Strength입니다.")]
    [Range(0f, 1f)]
    [SerializeField]
    private float manualStrength = 0.8f;

    [Header("Safety")]

    [Tooltip(
        "이 시간 동안 bow_input이 들어오지 않으면 " +
        "발사하지 않고 시위를 취소합니다."
    )]
    [Min(0.1f)]
    [SerializeField]
    private float inputTimeoutSeconds = 1f;

    [Tooltip(
        "메시지의 session_id가 ArtemisServerTest와 다르면 무시합니다."
    )]
    [SerializeField]
    private bool rejectMismatchedSession = true;


    [Header("Debug")]

    [Tooltip("bow_input 패킷을 받는 즉시 내용을 Console에 표시합니다.")]
    [SerializeField]
    private bool showPacketReceivedLogs = true;

    [Tooltip("버튼 ON, OFF, 발사 등의 상태 변화를 표시합니다.")]
    [SerializeField]
    private bool showStateChangeLogs = true;

    [Tooltip("세션 불일치나 중복 패킷을 무시한 이유를 표시합니다.")]
    [SerializeField]
    private bool showRejectedPacketLogs = true;

    [SerializeField]
    private bool receivedAnyBowInput;

    [SerializeField]
    private bool currentButtonPressed;

    [SerializeField]
    private bool currentDrawValid;

    [SerializeField]
    private float currentRawDrawValue;

    [Range(0f, 1f)]
    [SerializeField]
    private float currentStrength;

    [SerializeField]
    private long lastSequence = -1;


    private float lastInputReceivedTime;
    private float lastStrengthUpdateTime;

    private bool hasValidDrawSincePress;
    private bool calibrationWarningShown;
    private bool poseTrialActive;
    private bool shotFiredForCurrentTrial;


    private void OnValidate()
    {
        smoothingTime =
            Mathf.Max(0f, smoothingTime);

        inputTimeoutSeconds =
            Mathf.Max(0.1f, inputTimeoutSeconds);

        manualStrength =
            Mathf.Clamp01(manualStrength);
    }

    private void OnEnable()
    {
        if (artemisServer != null)
        {
            artemisServer.RawMessageReceived +=
                OnRawMessageReceived;

            Debug.Log(
                "[ServerBowInputReceiver] " +
                "서버 bow_input 수신 대기 시작",
                this
            );
        }
        else
        {
            Debug.LogError(
                "[ServerBowInputReceiver] " +
                "Artemis Server가 연결되지 않았습니다.",
                this
            );
        }

        if (bowStringController == null)
        {
            Debug.LogError(
                "[ServerBowInputReceiver] " +
                "Bow String Controller가 연결되지 않았습니다.",
                this
            );
        }
    }

    private void OnDisable()
    {
        if (artemisServer != null)
        {
            artemisServer.RawMessageReceived -=
                OnRawMessageReceived;
        }

        CancelActiveInput();
    }

    private void Update()
    {
        if (UnityEngine.InputSystem.Keyboard.current != null &&
            UnityEngine.InputSystem.Keyboard.current.pKey.wasPressedThisFrame)
        {
            FireKeyboardWizardShot();
        }

        if (!currentButtonPressed)
        {
            return;
        }

        if (!receivedAnyBowInput)
        {
            return;
        }

        float elapsed =
            Time.unscaledTime -
            lastInputReceivedTime;

        if (elapsed <= inputTimeoutSeconds)
        {
            return;
        }

        Debug.LogWarning(
            "[ServerBowInputReceiver] " +
            "bow_input 수신 시간이 초과되었습니다." +
            "\n경과 시간: " +
            elapsed.ToString("F2") +
            "초" +
            "\n발사하지 않고 입력을 취소합니다.",
            this
        );

        CancelActiveInput();
    }

    private void FireKeyboardWizardShot()
    {
        if (!poseTrialActive)
        {
            Debug.LogWarning(
                "[ServerBowInputReceiver] P key ignored: start the trial in the app first.",
                this
            );
            return;
        }

        if (shotFiredForCurrentTrial)
        {
            Debug.LogWarning(
                "[ServerBowInputReceiver] P key ignored: this trial already fired one arrow.",
                this
            );
            return;
        }

        if (bowStringController == null)
        {
            Debug.LogError(
                "[ServerBowInputReceiver] P 키 발사 실패: " +
                "Bow String Controller가 연결되지 않았습니다.",
                this
            );

            return;
        }

        if (!bowStringController.IsExternalPullActive)
        {
            bowStringController.BeginExternalPull();
        }

        if (!bowStringController.IsExternalPullActive)
        {
            Debug.LogWarning(
                "[ServerBowInputReceiver] P 키 발사를 시작할 수 없습니다. " +
                "현재 XR 입력으로 시위를 잡고 있는지 확인하세요.",
                this
            );

            return;
        }

        float shotStrength = Mathf.Clamp01(manualStrength);

        bowStringController.SetExternalStrength(shotStrength);
        shotFiredForCurrentTrial = true;
        bowStringController.EndExternalPull();
        ResetInputState();

        Debug.Log(
            "[Artemis][KEYBOARD_RELEASE] P 키로 화살 발사" +
            "\nStrength: " + shotStrength.ToString("F2"),
            this
        );
    }

    /// <summary>
    /// ArtemisServerTest.RawMessageReceived 이벤트에서 호출됩니다.
    /// bow_input이 아닌 feedback, shot_report 메시지는 무시합니다.
    /// </summary>
    private void OnRawMessageReceived(
        string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            if (showRejectedPacketLogs)
            {
                Debug.LogWarning(
                    "[ServerBowInputReceiver] " +
                    "비어 있는 서버 메시지를 받았습니다.",
                    this
                );
            }

            return;
        }

        BowInputMessage message;

        try
        {
            message =
                JsonUtility.FromJson<BowInputMessage>(
                    rawJson
                );
        }
        catch (Exception exception)
        {
            Debug.LogWarning(
                "[ServerBowInputReceiver] " +
                "JSON 해석 실패" +
                "\n오류: " +
                exception.Message +
                "\n원본 JSON: " +
                rawJson,
                this
            );

            return;
        }

        if (message != null && message.type == "pose_draw_update")
        {
            PoseDrawMessage poseMessage =
                JsonUtility.FromJson<PoseDrawMessage>(rawJson);

            if (poseMessage != null && poseMessage.payload != null)
            {
                ProcessPoseDraw(poseMessage.payload);
            }
            return;
        }

        /*
         * feedback, shot_report처럼 bow_input이 아닌
         * 서버 메시지는 이 스크립트에서 처리하지 않습니다.
         */
        if (message == null ||
            message.type != "bow_input")
        {
            return;
        }

        if (message.payload == null)
        {
            Debug.LogWarning(
                "[ServerBowInputReceiver] " +
                "bow_input을 받았지만 payload가 없습니다." +
                "\n원본 JSON: " +
                rawJson,
                this
            );

            return;
        }

        /*
         * 서버에서 bow_input이 Unity까지 도착하면
         * 처리 결과와 관계없이 가장 먼저 출력되는 로그입니다.
         */
        if (showPacketReceivedLogs)
        {
            Debug.Log(
                "[ServerBowInputReceiver] bow_input 패킷 수신" +
                "\nSession ID: " +
                message.payload.session_id +
                "\nSequence: " +
                message.payload.sequence +
                "\nButton: " +
                (
                    message.payload.button_pressed
                        ? "ON"
                        : "OFF"
                ) +
                "\nDraw Value: " +
                message.payload.draw_value.ToString("F2") +
                "\nDraw Valid: " +
                message.payload.draw_valid +
                "\nCaptured At: " +
                message.payload.captured_at,
                this
            );
        }

        ProcessBowInput(message.payload);
    }

    private void ProcessPoseDraw(PoseDrawPayload payload)
    {
        if (bowStringController == null || !IsCorrectSession(payload.session_id))
        {
            return;
        }

        switch (payload.action)
        {
            case "start":
                poseTrialActive = true;
                shotFiredForCurrentTrial = false;
                bowStringController.BeginExternalPull();
                bowStringController.SetExternalStrength(0f);
                break;
            case "update":
                // Unity가 앱의 일회성 start 브로드캐스트 이후 연결되더라도
                // ACTIVE_DRAW 업데이트를 통해 현재 트라이얼을 복구합니다.
                if (!poseTrialActive &&
                    payload.state == "ACTIVE_DRAW")
                {
                    poseTrialActive = true;
                    shotFiredForCurrentTrial = false;
                }

                if (!poseTrialActive ||
                    shotFiredForCurrentTrial ||
                    !payload.draw_valid)
                {
                    return;
                }
                if (!bowStringController.IsExternalPullActive)
                {
                    bowStringController.BeginExternalPull();
                }
                bowStringController.SetExternalStrength(payload.draw_strength);
                break;
            case "release":
                if (!shotFiredForCurrentTrial &&
                    bowStringController.IsExternalPullActive)
                {
                    Debug.Log(
                        "[Artemis][NANO_RELEASE] Nano 버튼 발사 트리거 수신" +
                        "\nRelease strength: " +
                        payload.draw_strength.ToString("F3") +
                        "\nCurrent string stretch: " +
                        bowStringController.CurrentStretch.ToString("F3") + " m",
                        this
                    );
                    bowStringController.SetExternalStrength(payload.draw_strength);
                    shotFiredForCurrentTrial = true;
                    bowStringController.EndExternalPull();
                }
                else
                {
                    Debug.LogWarning(
                        "[Artemis][NANO_RELEASE] 시위가 당김 상태가 아니어서 " +
                        "발사 트리거를 무시했습니다.",
                        this
                    );
                }
                break;
            case "cancel":
                bowStringController.CancelExternalPull();
                poseTrialActive = false;
                break;
        }

        // Pose update packets arrive many times per second. Logging each update
        // (or even several per second) creates large Console stack traces and
        // causes significant Editor slowdown, so only state transitions are logged.
        bool shouldLog = payload.action != "update";

        if (showStateChangeLogs && shouldLog)
        {
            Debug.Log(
                "[Artemis][POSE_DRAW] " + payload.action +
                "\nState: " + payload.state +
                "\nHand distance / shoulder width: " +
                payload.hand_distance_ratio.ToString("F3") +
                "\nNormalized strength: " +
                payload.draw_strength.ToString("F3") +
                "\nApplied string stretch: " +
                bowStringController.CurrentStretch.ToString("F3") + " m",
                this
            );
        }
    }

    private void ProcessBowInput(
        BowInputPayload payload)
    {
        /*
         * 패킷 수신 로그는 이미 출력됐지만,
         * 세션이 다르면 활에는 적용하지 않습니다.
         */
        if (!IsCorrectSession(payload.session_id))
        {
            if (showRejectedPacketLogs)
            {
                string expectedSession =
                    artemisServer != null
                        ? artemisServer.SessionId
                        : "(ArtemisServer 없음)";

                Debug.LogWarning(
                    "[ServerBowInputReceiver] " +
                    "Session ID가 달라 패킷을 무시합니다." +
                    "\n받은 Session ID: " +
                    payload.session_id +
                    "\nUnity Session ID: " +
                    expectedSession,
                    this
                );
            }

            return;
        }

        /*
         * sequence가 1 이상일 때만 중복 검사를 적용합니다.
         * sequence를 생략하면 기본값이 0이므로 계속 처리합니다.
         */
        if (payload.sequence > 0)
        {
            if (payload.sequence <= lastSequence)
            {
                if (showRejectedPacketLogs)
                {
                    Debug.LogWarning(
                        "[ServerBowInputReceiver] " +
                        "중복 또는 오래된 패킷을 무시합니다." +
                        "\n받은 Sequence: " +
                        payload.sequence +
                        "\n마지막 Sequence: " +
                        lastSequence,
                        this
                    );
                }

                return;
            }

            lastSequence =
                payload.sequence;
        }

        receivedAnyBowInput = true;

        lastInputReceivedTime =
            Time.unscaledTime;

        currentRawDrawValue =
            payload.draw_value;

        currentDrawValid =
            payload.draw_valid;

        bool wasPressed =
            currentButtonPressed;

        bool isPressed =
            payload.button_pressed;

        // 버튼 상태: OFF → ON
        if (!wasPressed && isPressed)
        {
            BeginPull();
        }

        /*
         * 버튼을 누르고 있거나 방금 놓은 경우
         * 현재 Strength를 계산하고 시위에 반영합니다.
         */
        if (isPressed || wasPressed)
        {
            ApplyStrength(payload);
        }

        // 버튼 상태: ON → OFF
        if (wasPressed && !isPressed)
        {
            EndPull(payload);
        }
        else
        {
            currentButtonPressed =
                isPressed;
        }
    }

    private void BeginPull()
    {
        if (bowStringController == null)
        {
            Debug.LogError(
                "[ServerBowInputReceiver] " +
                "Bow String Controller가 없어 " +
                "버튼 ON을 처리할 수 없습니다.",
                this
            );

            return;
        }

        currentButtonPressed = true;
        currentStrength = 0f;
        hasValidDrawSincePress = false;

        lastStrengthUpdateTime =
            Time.unscaledTime;

        bowStringController.BeginExternalPull();

        if (strengthMode ==
            StrengthMode.WizardOfOz)
        {
            currentStrength =
                Mathf.Clamp01(manualStrength);

            bowStringController.SetExternalStrength(
                currentStrength
            );

            hasValidDrawSincePress = true;
        }
        else
        {
            bowStringController.SetExternalStrength(0f);
        }

        if (showStateChangeLogs)
        {
            Debug.Log(
                "[ServerBowInputReceiver] " +
                "버튼 ON / 외부 시위 잡기 시작" +
                "\n입력 모드: " +
                strengthMode +
                "\n현재 Strength: " +
                currentStrength.ToString("F2"),
                this
            );
        }
    }

    private void ApplyStrength(
        BowInputPayload payload)
    {
        if (bowStringController == null)
        {
            return;
        }

        if (!bowStringController.IsExternalPullActive)
        {
            if (showRejectedPacketLogs)
            {
                Debug.LogWarning(
                    "[ServerBowInputReceiver] " +
                    "외부 시위 잡기 상태가 아니어서 " +
                    "Strength 적용을 건너뜁니다.",
                    this
                );
            }

            return;
        }

        /*
         * Wizard-of-Oz 모드에서는 draw_value를 무시하고
         * Inspector의 Manual Strength를 사용합니다.
         */
        if (strengthMode ==
            StrengthMode.WizardOfOz)
        {
            currentStrength =
                Mathf.Clamp01(manualStrength);

            bowStringController.SetExternalStrength(
                currentStrength
            );

            hasValidDrawSincePress = true;
            return;
        }

        /*
         * Sensor 모드에서 draw_valid가 false면
         * 해당 센서값은 적용하지 않습니다.
         */
        if (!payload.draw_valid)
        {
            if (showRejectedPacketLogs)
            {
                Debug.LogWarning(
                    "[ServerBowInputReceiver] " +
                    "draw_valid가 false라서 " +
                    "센서 Strength를 적용하지 않습니다." +
                    "\nDraw Value: " +
                    payload.draw_value.ToString("F2"),
                    this
                );
            }

            return;
        }

        if (sensorMaximum <= sensorMinimum)
        {
            if (!calibrationWarningShown)
            {
                Debug.LogError(
                    "[ServerBowInputReceiver] " +
                    "Sensor Maximum은 Sensor Minimum보다 " +
                    "커야 합니다." +
                    "\nSensor Minimum: " +
                    sensorMinimum +
                    "\nSensor Maximum: " +
                    sensorMaximum,
                    this
                );

                calibrationWarningShown = true;
            }

            return;
        }

        calibrationWarningShown = false;

        float targetStrength =
            Mathf.InverseLerp(
                sensorMinimum,
                sensorMaximum,
                payload.draw_value
            );

        if (invertInput)
        {
            targetStrength =
                1f - targetStrength;
        }

        targetStrength =
            Mathf.Clamp01(targetStrength);

        if (smoothingTime <= 0f)
        {
            currentStrength =
                targetStrength;
        }
        else
        {
            float deltaTime =
                Mathf.Max(
                    0f,
                    Time.unscaledTime -
                    lastStrengthUpdateTime
                );

            float interpolation =
                1f -
                Mathf.Exp(
                    -deltaTime / smoothingTime
                );

            currentStrength =
                Mathf.Lerp(
                    currentStrength,
                    targetStrength,
                    interpolation
                );
        }

        lastStrengthUpdateTime =
            Time.unscaledTime;

        hasValidDrawSincePress = true;

        bowStringController.SetExternalStrength(
            currentStrength
        );
    }

    private void EndPull(
        BowInputPayload payload)
    {
        if (bowStringController == null)
        {
            Debug.LogError(
                "[ServerBowInputReceiver] " +
                "Bow String Controller가 없어 " +
                "버튼 OFF를 처리할 수 없습니다.",
                this
            );

            ResetInputState();
            return;
        }

        bool canFire;

        if (strengthMode ==
            StrengthMode.WizardOfOz)
        {
            canFire = true;
        }
        else
        {
            /*
             * Sensor 모드는 버튼 OFF 패킷에도
             * 유효한 draw_value가 포함돼야 발사합니다.
             */
            canFire =
                payload.draw_valid &&
                hasValidDrawSincePress &&
                sensorMaximum > sensorMinimum;
        }

        if (canFire)
        {
            bowStringController.SetExternalStrength(
                currentStrength
            );

            bowStringController.EndExternalPull();

            if (showStateChangeLogs)
            {
                Debug.Log(
                    "[ServerBowInputReceiver] " +
                    "버튼 OFF / 화살 발사" +
                    "\n입력 모드: " +
                    strengthMode +
                    "\n발사 Strength: " +
                    currentStrength.ToString("F2") +
                    "\n마지막 Draw Value: " +
                    payload.draw_value.ToString("F2"),
                    this
                );
            }
        }
        else
        {
            bowStringController.CancelExternalPull();

            Debug.LogWarning(
                "[ServerBowInputReceiver] " +
                "유효한 센서값이 없어 발사를 취소했습니다." +
                "\nDraw Valid: " +
                payload.draw_valid +
                "\nValid Draw Since Press: " +
                hasValidDrawSincePress,
                this
            );
        }

        ResetInputState();
    }

    private bool IsCorrectSession(
        string receivedSessionId)
    {
        if (!rejectMismatchedSession ||
            artemisServer == null)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(
            receivedSessionId))
        {
            return false;
        }

        return string.Equals(
            receivedSessionId,
            artemisServer.SessionId,
            StringComparison.Ordinal
        );
    }

    private void CancelActiveInput()
    {
        if (bowStringController != null &&
            bowStringController.IsExternalPullActive)
        {
            bowStringController.CancelExternalPull();
        }

        ResetInputState();
    }

    private void ResetInputState()
    {
        currentButtonPressed = false;
        currentDrawValid = false;
        currentStrength = 0f;
        hasValidDrawSincePress = false;
    }


    [Serializable]
    private class BowInputMessage
    {
        public string type;
        public BowInputPayload payload;
    }

    [Serializable]
    private class BowInputPayload
    {
        public string session_id;
        public long sequence;
        public bool button_pressed;
        public float draw_value;
        public bool draw_valid;
        public string captured_at;
    }

    [Serializable]
    private class PoseDrawMessage
    {
        public string type;
        public PoseDrawPayload payload;
    }

    [Serializable]
    private class PoseDrawPayload
    {
        public string session_id;
        public string action;
        public string state;
        public float draw_strength;
        public bool draw_valid;
        public float hand_distance_ratio;
        public string captured_at;
    }
}
