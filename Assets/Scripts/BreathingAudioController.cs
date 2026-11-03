using UnityEngine;

/// <summary>
/// 활시위를 잡거나 실제로 당기기 시작했을 때
/// 4초짜리 호흡 음원을 반복 재생합니다.
///
/// 시위를 놓으면 호흡 음원을 즉시 정지합니다.
///
/// Inspector 설정:
/// - Start On String Grab = true:
///   시위를 Grab한 순간부터 재생
///
/// - Start On String Grab = false:
///   시위를 Minimum Pull Distance 이상 실제로 당겼을 때 재생
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class BreathingAudioController : MonoBehaviour
{
    // =========================================================
    // 활시위
    // =========================================================

    [Header("Bow String")]

    [Tooltip("활시위의 Grab, 당김 거리, Release 상태를 관리하는 스크립트")]
    [SerializeField]
    private BowStringController bowStringController;


    // =========================================================
    // 오디오
    // =========================================================

    [Header("Breathing Audio")]

    [Tooltip("반복 재생할 4초짜리 들숨+날숨 WAV 음원")]
    [SerializeField]
    private AudioClip breathingClip;

    [Tooltip(
        "호흡 음원을 재생할 AudioSource입니다. " +
        "비워 두면 같은 오브젝트의 AudioSource를 자동으로 사용합니다."
    )]
    [SerializeField]
    private AudioSource breathingAudioSource;


    // =========================================================
    // 재생 조건
    // =========================================================

    [Header("Start Condition")]

    [Tooltip(
        "활성화하면 시위를 Grab한 순간부터 음원을 재생합니다. " +
        "컨트롤러 Trigger 또는 핸드 Pinch 시점과 연결됩니다."
    )]
    [SerializeField]
    private bool startOnStringGrab = true;

    [Tooltip(
        "Start On String Grab을 끈 경우, " +
        "시위를 이 거리 이상 실제로 당겼을 때 재생합니다."
    )]
    [Min(0f)]
    [SerializeField]
    private float minimumPullDistanceM = 0.01f;


    // =========================================================
    // 재생 설정
    // =========================================================

    [Header("Playback Settings")]

    [Tooltip(
        "호흡 음원 재생 속도입니다.\n" +
        "1.0 = 원본 속도\n" +
        "0.5 = 절반 속도\n" +
        "2.0 = 두 배 속도\n\n" +
        "AudioSource.pitch를 사용하므로 음높이도 함께 변합니다."
    )]
    [Range(0.5f, 2f)]
    [SerializeField]
    private float playbackSpeed = 1f;

    [Tooltip("시위를 잡고 있는 동안 4초 음원을 반복 재생합니다.")]
    [SerializeField]
    private bool loopWhileDrawing = true;

    [Tooltip(
        "시위를 다시 잡을 때마다 음원을 0초부터 다시 시작합니다."
    )]
    [SerializeField]
    private bool restartFromBeginning = true;

    [Tooltip(
        "호흡 안내음이 활 위치가 아니라 사용자의 양쪽 귀에서 " +
        "일정하게 들리도록 2D Audio로 설정합니다."
    )]
    [SerializeField]
    private bool useTwoDimensionalAudio = true;


    // =========================================================
    // Debug
    // =========================================================

    [Header("Debug")]

    [SerializeField]
    private bool printDebugLog = true;


    // =========================================================
    // 내부 상태
    // =========================================================

    private bool isStringGrabbed;
    private bool isBreathingPlaying;


    // =========================================================
    // Unity 생명주기
    // =========================================================

    private void Awake()
    {
        if (breathingAudioSource == null)
        {
            breathingAudioSource =
                GetComponent<AudioSource>();
        }

        if (bowStringController == null)
        {
            bowStringController =
                FindFirstObjectByType<BowStringController>();
        }

        ConfigureAudioSource();
    }

    private void OnValidate()
    {
        playbackSpeed =
            Mathf.Clamp(
                playbackSpeed,
                0.5f,
                2f
            );

        minimumPullDistanceM =
            Mathf.Max(
                0f,
                minimumPullDistanceM
            );

        /*
         * Inspector 값을 Play Mode 중 변경했을 때
         * 현재 AudioSource에도 바로 적용합니다.
         */
        if (breathingAudioSource == null)
        {
            breathingAudioSource =
                GetComponent<AudioSource>();
        }

        ApplyPlaybackSettings();
    }

    private void OnEnable()
    {
        if (bowStringController == null)
        {
            bowStringController =
                FindFirstObjectByType<BowStringController>();
        }

        if (bowStringController == null)
        {
            Debug.LogError(
                "[BreathingAudio] " +
                "BowStringController를 찾을 수 없습니다.",
                this
            );

            return;
        }

        bowStringController.StringPullStarted +=
            HandleStringPullStarted;

        bowStringController.BowReleased +=
            HandleStringReleased;
    }

    private void OnDisable()
    {
        if (bowStringController != null)
        {
            bowStringController.StringPullStarted -=
                HandleStringPullStarted;

            bowStringController.BowReleased -=
                HandleStringReleased;
        }

        isStringGrabbed = false;

        StopBreathingAudio();
    }

    private void Update()
    {
        /*
         * 시위를 Grab하자마자 재생하지 않고
         * 실제 당김 거리로 시작하도록 설정한 경우입니다.
         */
        if (startOnStringGrab)
        {
            return;
        }

        if (
            !isStringGrabbed ||
            isBreathingPlaying ||
            bowStringController == null
        )
        {
            return;
        }

        if (
            bowStringController.CurrentStretch >=
            minimumPullDistanceM
        )
        {
            PlayBreathingAudio();

            Log(
                "실제 당김 시작 → 호흡 음원 반복 재생" +
                $"\nPull Distance: " +
                $"{bowStringController.CurrentStretch:F3} m"
            );
        }
    }


    // =========================================================
    // AudioSource 설정
    // =========================================================

    private void ConfigureAudioSource()
    {
        if (breathingAudioSource == null)
        {
            Debug.LogError(
                "[BreathingAudio] AudioSource가 없습니다.",
                this
            );

            enabled = false;
            return;
        }

        breathingAudioSource.playOnAwake = false;
        breathingAudioSource.dopplerLevel = 0f;

        if (useTwoDimensionalAudio)
        {
            breathingAudioSource.spatialBlend = 0f;
        }

        if (breathingClip != null)
        {
            breathingAudioSource.clip =
                breathingClip;
        }

        ApplyPlaybackSettings();
    }

    private void ApplyPlaybackSettings()
    {
        if (breathingAudioSource == null)
        {
            return;
        }

        breathingAudioSource.pitch =
            playbackSpeed;

        breathingAudioSource.loop =
            loopWhileDrawing;
    }


    // =========================================================
    // 활시위 이벤트
    // =========================================================

    /// <summary>
    /// 시위를 컨트롤러 Trigger 또는 핸드 Pinch로 잡은 순간입니다.
    /// </summary>
    private void HandleStringPullStarted()
    {
        isStringGrabbed = true;

        if (startOnStringGrab)
        {
            PlayBreathingAudio();

            Log(
                "시위 Grab → 호흡 음원 반복 재생" +
                $"\nPlayback Speed: {playbackSpeed:F2}"
            );
        }
        else
        {
            Log(
                "시위 Grab → 실제 당김 거리 대기"
            );
        }
    }

    /// <summary>
    /// 시위를 놓으면 음원을 즉시 정지합니다.
    /// </summary>
    private void HandleStringReleased(
        float releasedStrength)
    {
        isStringGrabbed = false;

        StopBreathingAudio();

        Log(
            "시위 Release → 호흡 음원 정지" +
            $"\nRelease Strength: " +
            $"{Mathf.Clamp01(releasedStrength):F3}"
        );
    }


    // =========================================================
    // 오디오 재생
    // =========================================================

    private void PlayBreathingAudio()
    {
        if (breathingAudioSource == null)
        {
            Debug.LogWarning(
                "[BreathingAudio] " +
                "Breathing Audio Source가 없습니다.",
                this
            );

            return;
        }

        if (breathingClip == null)
        {
            Debug.LogWarning(
                "[BreathingAudio] " +
                "Breathing Clip이 없습니다.",
                this
            );

            return;
        }

        /*
         * 이미 재생 중이면 중복 Play를 방지합니다.
         */
        if (isBreathingPlaying)
        {
            return;
        }

        breathingAudioSource.clip =
            breathingClip;

        breathingAudioSource.pitch =
            playbackSpeed;

        breathingAudioSource.loop =
            loopWhileDrawing;

        if (restartFromBeginning)
        {
            breathingAudioSource.time = 0f;
        }

        breathingAudioSource.Play();

        isBreathingPlaying = true;
    }

    private void StopBreathingAudio()
    {
        if (breathingAudioSource == null)
        {
            isBreathingPlaying = false;
            return;
        }

        breathingAudioSource.Stop();
        breathingAudioSource.time = 0f;

        isBreathingPlaying = false;
    }


    // =========================================================
    // Debug
    // =========================================================

    private void Log(
        string message)
    {
        if (!printDebugLog)
        {
            return;
        }

        Debug.Log(
            $"[BreathingAudio] {message}",
            this
        );
    }
}