using System.Collections;
using TMPro;
using UnityEngine;

/// <summary>
/// 서버의 실시간 feedback과 한 발 shot_report를
/// 기존 Feedback UI에 표시합니다.
///
/// session_report는 이 스크립트에서 처리하지 않습니다.
/// session_report 이미지는 SessionFeedbackImageCarousel에서
/// 별도로 표시됩니다.
/// </summary>
public class ServerFeedbackUI : MonoBehaviour
{
    // =========================================================
    // Receiver
    // =========================================================

    [Header("Receiver")]

    [Tooltip(
        "서버 메시지를 분류하여 전달하는 " +
        "ServerFeedbackReceiver입니다."
    )]
    [SerializeField]
    private ServerFeedbackReceiver feedbackReceiver;

    // =========================================================
    // Feedback UI
    // =========================================================

    [Header("Feedback UI")]

    [Tooltip(
        "실시간 feedback과 한 발 shot_report를 " +
        "표시할 TMP Text입니다."
    )]
    [SerializeField]
    private TMP_Text feedbackText;

    [Tooltip(
        "Feedback UI의 표시와 숨김에 사용할 " +
        "CanvasGroup입니다."
    )]
    [SerializeField]
    private CanvasGroup feedbackCanvasGroup;

    // =========================================================
    // Display Settings
    // =========================================================

    [Header("Display Settings")]

    [Tooltip(
        "Feedback이 화면에 유지되는 시간입니다."
    )]
    [Min(0.1f)]
    [SerializeField]
    private float displaySeconds = 3f;

    [Tooltip(
        "Feedback UI가 나타나고 사라질 때의 " +
        "Fade 시간입니다."
    )]
    [Min(0f)]
    [SerializeField]
    private float fadeSeconds = 0.2f;

    [Tooltip(
        "켜면 메시지 앞에 [GUIDE], [ALERT], " +
        "[RESULT] 등의 타입을 표시합니다."
    )]
    [SerializeField]
    private bool showTypePrefix = false;

    // =========================================================
    // Internal State
    // =========================================================

    private Coroutine displayCoroutine;

    // =========================================================
    // Unity Lifecycle
    // =========================================================

    private void Awake()
    {
        FindReferences();

        HideFeedbackImmediately();
    }

    private void OnEnable()
    {
        FindReferences();

        if (feedbackReceiver == null)
        {
            Debug.LogWarning(
                "[ServerFeedbackUI] " +
                "ServerFeedbackReceiver를 찾지 못했습니다.",
                this
            );

            return;
        }

        // 일반 feedback과 한 발 shot_report만 구독합니다.
        feedbackReceiver.FeedbackReceived +=
            HandleFeedbackReceived;

        // SessionReportReceived는 구독하지 않습니다.
        // 따라서 session_report가 기존 Feedback UI에
        // 표시되지 않습니다.
    }

    private void OnDisable()
    {
        if (feedbackReceiver != null)
        {
            feedbackReceiver.FeedbackReceived -=
                HandleFeedbackReceived;
        }

        StopDisplayCoroutine();
    }

    // =========================================================
    // Reference Search
    // =========================================================

    private void FindReferences()
    {
        if (feedbackReceiver == null)
        {
            feedbackReceiver =
                FindAnyObjectByType<ServerFeedbackReceiver>();
        }

        if (feedbackText == null)
        {
            feedbackText =
                GetComponent<TMP_Text>();
        }

        if (
            feedbackCanvasGroup == null &&
            feedbackText != null
        )
        {
            feedbackCanvasGroup =
                feedbackText.GetComponent<CanvasGroup>();
        }

        if (feedbackCanvasGroup == null)
        {
            feedbackCanvasGroup =
                GetComponent<CanvasGroup>();
        }
    }

    // =========================================================
    // Feedback Handling
    // =========================================================

    /// <summary>
    /// 일반 feedback 또는 한 발 shot_report를 받았을 때
    /// 호출됩니다.
    /// </summary>
    private void HandleFeedbackReceived(
        string message,
        string feedbackType,
        string code)
    {
        if (feedbackText == null)
        {
            Debug.LogWarning(
                "[ServerFeedbackUI] " +
                "Feedback Text가 연결되지 않았습니다.",
                this
            );

            return;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        StopDisplayCoroutine();

        displayCoroutine =
            StartCoroutine(
                ShowFeedbackRoutine(
                    message,
                    feedbackType
                )
            );
    }

    /// <summary>
    /// Feedback UI를 표시하고 일정 시간이 지나면
    /// 다시 숨깁니다.
    /// </summary>
    private IEnumerator ShowFeedbackRoutine(
        string message,
        string feedbackType)
    {
        feedbackText.text =
            showTypePrefix
                ? $"{GetTypePrefix(feedbackType)} {message}"
                : message;

        SetCanvasInteraction(
            feedbackCanvasGroup,
            true
        );

        yield return FadeCanvasTo(
            feedbackCanvasGroup,
            1f,
            fadeSeconds
        );

        yield return new WaitForSecondsRealtime(
            displaySeconds
        );

        yield return FadeCanvasTo(
            feedbackCanvasGroup,
            0f,
            fadeSeconds
        );

        if (feedbackText != null)
        {
            feedbackText.text = string.Empty;
        }

        SetCanvasInteraction(
            feedbackCanvasGroup,
            false
        );

        displayCoroutine = null;
    }

    // =========================================================
    // Public Methods
    // =========================================================

    /// <summary>
    /// 현재 표시 중인 Feedback을 즉시 숨깁니다.
    /// UI Button 등에서도 호출할 수 있습니다.
    /// </summary>
    public void HideFeedback()
    {
        StopDisplayCoroutine();

        HideFeedbackImmediately();
    }

    /// <summary>
    /// 다음 세션 시작 시 기존 Feedback UI를 초기화합니다.
    /// </summary>
    public void ResetReportUI()
    {
        StopDisplayCoroutine();

        HideFeedbackImmediately();
    }

    // =========================================================
    // Fade
    // =========================================================

    private IEnumerator FadeCanvasTo(
        CanvasGroup canvasGroup,
        float targetAlpha,
        float duration)
    {
        if (canvasGroup == null)
        {
            yield break;
        }

        if (duration <= 0f)
        {
            canvasGroup.alpha = targetAlpha;
            yield break;
        }

        float startAlpha =
            canvasGroup.alpha;

        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed +=
                Time.unscaledDeltaTime;

            float progress =
                Mathf.Clamp01(
                    elapsed / duration
                );

            canvasGroup.alpha =
                Mathf.Lerp(
                    startAlpha,
                    targetAlpha,
                    progress
                );

            yield return null;
        }

        canvasGroup.alpha = targetAlpha;
    }

    // =========================================================
    // Hide / Stop
    // =========================================================

    private void HideFeedbackImmediately()
    {
        if (feedbackText != null)
        {
            feedbackText.text = string.Empty;
        }

        SetCanvasAlpha(
            feedbackCanvasGroup,
            0f
        );

        SetCanvasInteraction(
            feedbackCanvasGroup,
            false
        );
    }

    private void StopDisplayCoroutine()
    {
        if (displayCoroutine == null)
        {
            return;
        }

        StopCoroutine(displayCoroutine);

        displayCoroutine = null;
    }

    // =========================================================
    // CanvasGroup Helpers
    // =========================================================

    private static void SetCanvasAlpha(
        CanvasGroup canvasGroup,
        float alpha)
    {
        if (canvasGroup == null)
        {
            return;
        }

        canvasGroup.alpha = alpha;
    }

    private static void SetCanvasInteraction(
        CanvasGroup canvasGroup,
        bool enabled)
    {
        if (canvasGroup == null)
        {
            return;
        }

        canvasGroup.interactable = enabled;
        canvasGroup.blocksRaycasts = enabled;
    }

    // =========================================================
    // Feedback Type Prefix
    // =========================================================

    private static string GetTypePrefix(
        string feedbackType)
    {
        switch (
            feedbackType?
                .Trim()
                .ToLowerInvariant()
        )
        {
            case "voice":
                return "[GUIDE]";

            case "alert":
                return "[ALERT]";

            case "report":
                // shot_report가 이 타입으로 들어옵니다.
                return "[RESULT]";

            default:
                return "[FEEDBACK]";
        }
    }
}