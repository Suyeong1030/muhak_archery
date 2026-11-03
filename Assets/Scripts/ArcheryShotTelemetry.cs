using UnityEngine;

/// <summary>
/// XR 입력과 외부 하드웨어 입력에서 공통으로 발생하는
/// BowStringController 이벤트를 이용해 샷 상태를 서버로 전송합니다.
///
/// 서버 전송 상태:
/// - DRAW: 시위 잡기 시작
/// - ANCHOR: Strength 0.6 이상
/// - AIM: Strength 0.8 이상
/// - RELEASE: 시위 놓기
///
/// 실제 화살이 생성되면 Shot Count를 증가시킵니다.
/// 점수 전송은 TargetScoring.RegisterHit()에서 처리합니다.
/// </summary>
public class ArcheryShotTelemetry : MonoBehaviour
{
    [Header("Server")]

    [SerializeField]
    private ArtemisServerTest artemisServer;


    [Header("Bow String")]

    [SerializeField]
    private BowStringController bowStringController;


    [Header("Pilot State Thresholds")]

    [Tooltip("Strength가 이 값 이상이면 ANCHOR 상태입니다.")]
    [Range(0f, 1f)]
    [SerializeField]
    private float anchorStrengthThreshold = 0.6f;

    [Tooltip("Strength가 이 값 이상이면 AIM 상태입니다.")]
    [Range(0f, 1f)]
    [SerializeField]
    private float aimStrengthThreshold = 0.8f;

    [Tooltip("Strength 상태를 확인하는 시간 간격입니다.")]
    [Min(0.05f)]
    [SerializeField]
    private float stateSendIntervalSeconds = 0.05f;


    [Header("Score Manager")]

    [SerializeField]
    private TargetScoring targetScoring;


    [Header("Actual Arrow Launch Source")]

    [SerializeField]
    private ArrowController arrowController;


    [Header("Debug")]

    [SerializeField]
    private bool showDebugLog = true;


    private bool shotSequenceActive;
    private float nextStateSendTime;
    private string lastSentState = string.Empty;


    private void Awake()
    {
        if (artemisServer == null)
        {
            artemisServer =
                FindFirstObjectByType<ArtemisServerTest>();
        }

        if (bowStringController == null)
        {
            bowStringController =
                FindFirstObjectByType<BowStringController>();
        }

        if (targetScoring == null)
        {
            targetScoring =
                FindFirstObjectByType<TargetScoring>();
        }

        if (arrowController == null)
        {
            arrowController =
                FindFirstObjectByType<ArrowController>();
        }
    }


    private void OnValidate()
    {
        anchorStrengthThreshold =
            Mathf.Clamp01(
                anchorStrengthThreshold
            );

        aimStrengthThreshold =
            Mathf.Clamp(
                aimStrengthThreshold,
                anchorStrengthThreshold,
                1f
            );

        stateSendIntervalSeconds =
            Mathf.Max(
                0.05f,
                stateSendIntervalSeconds
            );
    }


    private void OnEnable()
    {
        /*
         * XRGrabInteractable 이벤트가 아니라
         * BowStringController 공통 이벤트를 구독합니다.
         *
         * 따라서 XR 입력과 외부 버튼 입력이
         * 완전히 같은 텔레메트리 경로를 사용합니다.
         */
        if (bowStringController != null)
        {
            bowStringController.StringPullStarted +=
                OnStringPullStarted;

            bowStringController.BowReleased +=
                OnBowReleased;
        }
        else
        {
            Debug.LogError(
                "[ArcheryShotTelemetry] " +
                "BowStringController가 연결되지 않았습니다.",
                this
            );
        }

        if (arrowController != null)
        {
            arrowController.ArrowLaunched +=
                OnActualArrowLaunched;
        }
        else
        {
            Debug.LogWarning(
                "[ArcheryShotTelemetry] " +
                "ArrowController가 연결되지 않았습니다.",
                this
            );
        }
    }


    private void OnDisable()
    {
        if (bowStringController != null)
        {
            bowStringController.StringPullStarted -=
                OnStringPullStarted;

            bowStringController.BowReleased -=
                OnBowReleased;
        }

        if (arrowController != null)
        {
            arrowController.ArrowLaunched -=
                OnActualArrowLaunched;
        }

        shotSequenceActive = false;
        lastSentState = string.Empty;
    }


    private void Update()
    {
        if (
            !shotSequenceActive ||
            artemisServer == null ||
            bowStringController == null ||
            !bowStringController.IsGrabbed ||
            Time.unscaledTime < nextStateSendTime
        )
        {
            return;
        }

        nextStateSendTime =
            Time.unscaledTime +
            stateSendIntervalSeconds;

        float strength =
            bowStringController.Strength;

        string state =
            GetStateFromStrength(strength);

        SendStateFrame(
            state,
            strength
        );
    }


    /// <summary>
    /// XR 시위 잡기와 외부 버튼 ON에서 모두 호출됩니다.
    /// </summary>
    private void OnStringPullStarted()
    {
        shotSequenceActive = true;
        lastSentState = string.Empty;

        nextStateSendTime =
            Time.unscaledTime +
            stateSendIntervalSeconds;

        if (artemisServer != null)
        {
            artemisServer.SendUnityFrame(
                "DRAW"
            );

            lastSentState = "DRAW";
        }

        if (showDebugLog)
        {
            Debug.Log(
                "[ArcheryShotTelemetry] DRAW",
                this
            );
        }
    }


    /// <summary>
    /// XR 시위 놓기와 외부 버튼 OFF에서 모두 호출됩니다.
    ///
    /// 입력이 너무 빨라 Update가 실행되기 전에 놓더라도
    /// 최종 Strength를 이용해 ANCHOR와 AIM을 보완 전송합니다.
    /// </summary>
    private void OnBowReleased(
        float releasedStrength)
    {
        if (!shotSequenceActive)
        {
            if (showDebugLog)
            {
                Debug.LogWarning(
                    "[ArcheryShotTelemetry] " +
                    "활성화된 샷 시퀀스가 없어 " +
                    "RELEASE 전송을 건너뜁니다.",
                    this
                );
            }

            return;
        }

        if (artemisServer != null)
        {
            /*
             * ON과 OFF가 매우 빠르게 들어와도
             * 최종 Strength에 필요한 상태가 누락되지 않게 합니다.
             *
             * 예:
             * DRAW 상태에서 Strength 0.8로 즉시 놓으면
             * ANCHOR → AIM을 먼저 보내고 RELEASE를 보냅니다.
             */
            string finalState =
                GetStateFromStrength(
                    releasedStrength
                );

            SendStateFrame(
                finalState,
                releasedStrength
            );

            artemisServer.SendUnityFrame(
                "RELEASE"
            );
        }

        shotSequenceActive = false;
        lastSentState = "RELEASE";

        if (showDebugLog)
        {
            Debug.Log(
                "[ArcheryShotTelemetry] RELEASE" +
                $"\nStrength: {releasedStrength:F3}",
                this
            );
        }
    }


    private string GetStateFromStrength(
        float strength)
    {
        float safeStrength =
            Mathf.Clamp01(strength);

        if (
            safeStrength >=
            aimStrengthThreshold
        )
        {
            return "AIM";
        }

        if (
            safeStrength >=
            anchorStrengthThreshold
        )
        {
            return "ANCHOR";
        }

        return "DRAW";
    }


    /// <summary>
    /// 상태가 변경된 경우에만 전송합니다.
    /// DRAW → ANCHOR → AIM 순서를 유지합니다.
    /// </summary>
    private void SendStateFrame(
        string state,
        float strength)
    {
        if (artemisServer == null)
        {
            return;
        }

        if (state == lastSentState)
        {
            return;
        }

        // AIM 이후에는 RELEASE 전까지 이전 상태로 돌아가지 않습니다.
        if (lastSentState == "AIM")
        {
            return;
        }

        // ANCHOR 이후에는 DRAW로 돌아가지 않습니다.
        if (
            lastSentState == "ANCHOR" &&
            state == "DRAW"
        )
        {
            return;
        }

        /*
         * DRAW 상태에서 Strength가 빠르게 증가해
         * AIM으로 바로 넘어가면 ANCHOR를 먼저 전송합니다.
         */
        if (
            lastSentState == "DRAW" &&
            state == "AIM"
        )
        {
            artemisServer.SendUnityFrame(
                "ANCHOR"
            );

            if (showDebugLog)
            {
                Debug.Log(
                    "[ArcheryShotTelemetry] " +
                    "상태 변경: ANCHOR" +
                    $"\nStrength: {strength:F3}",
                    this
                );
            }

            lastSentState = "ANCHOR";
        }

        artemisServer.SendUnityFrame(
            state
        );

        if (showDebugLog)
        {
            Debug.Log(
                "[ArcheryShotTelemetry] " +
                $"상태 변경: {state}" +
                $"\nStrength: {strength:F3}",
                this
            );
        }

        lastSentState = state;
    }


    /// <summary>
    /// 실제 화살 프리팹 생성에 성공했을 때만
    /// Shot Count를 증가시킵니다.
    ///
    /// 점수는 타깃 충돌 후
    /// TargetScoring.RegisterHit()에서 전송됩니다.
    /// </summary>
    private void OnActualArrowLaunched(
        ArrowController.ArrowLaunchData launchData)
    {
        if (targetScoring == null)
        {
            Debug.LogWarning(
                "[ArcheryShotTelemetry] " +
                "TargetScoring이 연결되지 않아 " +
                "Shot Count를 증가시키지 못했습니다.",
                this
            );

            return;
        }

        int currentShotCount =
            targetScoring.RegisterShot();

        if (showDebugLog)
        {
            Debug.Log(
                "[ArcheryShotTelemetry] " +
                "Actual Arrow Launched" +
                $"\nShot Count: {currentShotCount}" +
                $"\nStrength: {launchData.strength:F3}" +
                $"\nLaunch Speed: " +
                $"{launchData.launchSpeedMps:F2} m/s",
                this
            );
        }
    }
}