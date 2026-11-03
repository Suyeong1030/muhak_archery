using System.Collections;
using UnityEngine;

/// <summary>
/// 활시위를 잡으면 UI_Feedback 전체를 숨기고,
/// 활시위를 놓으면 다시 표시합니다.
/// 
/// UI_Feedback 오브젝트에
/// CanvasGroup과 이 스크립트를 함께 추가하세요.
/// </summary>
[RequireComponent(typeof(CanvasGroup))]
public class BowStringUIFader : MonoBehaviour
{
    [Header("Bow String Reference")]
    [Tooltip("활에 붙어 있는 BowStringController를 연결하세요.")]
    [SerializeField]
    private BowStringController bowStringController;

    [Header("UI Reference")]
    [Tooltip("UI_Feedback에 붙어 있는 CanvasGroup입니다. 비워 두면 자동으로 찾습니다.")]
    [SerializeField]
    private CanvasGroup uiCanvasGroup;

    [Header("Alpha Settings")]
    [Tooltip("활시위를 잡고 있지 않을 때 UI 투명도")]
    [Range(0f, 1f)]
    [SerializeField]
    private float visibleAlpha = 1f;

    [Tooltip("활시위를 잡고 있을 때 UI 투명도. 완전히 숨기려면 0")]
    [Range(0f, 1f)]
    [SerializeField]
    private float hiddenAlpha = 0f;

    [Header("Fade Settings")]
    [Tooltip("UI가 사라지고 다시 나타나는 데 걸리는 시간")]
    [Min(0f)]
    [SerializeField]
    private float fadeDuration = 0.2f;

    [Tooltip("Time.timeScale의 영향을 받지 않고 전환합니다.")]
    [SerializeField]
    private bool useUnscaledTime = true;

    private Coroutine fadeCoroutine;

    private void Awake()
    {
        /*
         * 이 스크립트와 같은 UI_Feedback 오브젝트에 있는
         * CanvasGroup을 자동으로 가져옵니다.
         */
        if (uiCanvasGroup == null)
        {
            uiCanvasGroup = GetComponent<CanvasGroup>();
        }

        /*
         * Inspector에서 활을 연결하지 않았으면
         * Scene에서 BowStringController를 자동으로 찾습니다.
         */
        if (bowStringController == null)
        {
            bowStringController =
                FindFirstObjectByType<BowStringController>();
        }

        if (uiCanvasGroup == null)
        {
            Debug.LogError(
                "[BowStringUIFader] CanvasGroup을 찾을 수 없습니다.",
                this
            );

            enabled = false;
            return;
        }

        /*
         * 게임 시작 시 UI가 보이도록 초기화합니다.
         */
        ApplyAlpha(visibleAlpha);
    }

    private void OnEnable()
    {
        /*
         * Awake 시점에 못 찾았을 가능성을 고려해
         * 한 번 더 찾습니다.
         */
        if (bowStringController == null)
        {
            bowStringController =
                FindFirstObjectByType<BowStringController>();
        }

        if (bowStringController == null)
        {
            Debug.LogWarning(
                "[BowStringUIFader] BowStringController를 찾을 수 없습니다.",
                this
            );

            return;
        }

        /*
         * 활시위를 잡는 이벤트와 놓는 이벤트를 구독합니다.
         */
        bowStringController.StringPullStarted +=
            HandleStringPullStarted;

        bowStringController.BowReleased +=
            HandleBowReleased;
    }

    private void OnDisable()
    {
        /*
         * 오브젝트가 비활성화되거나 Scene이 종료될 때
         * 이벤트 연결을 해제합니다.
         */
        if (bowStringController != null)
        {
            bowStringController.StringPullStarted -=
                HandleStringPullStarted;

            bowStringController.BowReleased -=
                HandleBowReleased;
        }

        if (fadeCoroutine != null)
        {
            StopCoroutine(fadeCoroutine);
            fadeCoroutine = null;
        }
    }

    /// <summary>
    /// 활시위를 잡았을 때 호출됩니다.
    /// UI 전체를 숨깁니다.
    /// </summary>
    private void HandleStringPullStarted()
    {
        StartFade(hiddenAlpha);
    }

    /// <summary>
    /// 활시위를 놓았을 때 호출됩니다.
    /// UI 전체를 다시 표시합니다.
    /// </summary>
    private void HandleBowReleased(float releasedStrength)
    {
        StartFade(visibleAlpha);
    }

    /// <summary>
    /// 기존 Fade가 실행 중이면 중지하고
    /// 새로운 목표 투명도로 전환합니다.
    /// </summary>
    private void StartFade(float targetAlpha)
    {
        if (uiCanvasGroup == null)
        {
            return;
        }

        if (fadeCoroutine != null)
        {
            StopCoroutine(fadeCoroutine);
        }

        fadeCoroutine =
            StartCoroutine(
                FadeCanvasGroup(targetAlpha)
            );
    }

    /// <summary>
    /// 현재 투명도에서 목표 투명도까지 부드럽게 전환합니다.
    /// </summary>
    private IEnumerator FadeCanvasGroup(
        float targetAlpha)
    {
        targetAlpha =
            Mathf.Clamp01(targetAlpha);

        float startAlpha =
            uiCanvasGroup.alpha;

        /*
         * Fade Duration이 0이면 즉시 변경합니다.
         */
        if (fadeDuration <= 0f)
        {
            ApplyAlpha(targetAlpha);
            fadeCoroutine = null;
            yield break;
        }

        float elapsedTime = 0f;

        while (elapsedTime < fadeDuration)
        {
            float deltaTime =
                useUnscaledTime
                    ? Time.unscaledDeltaTime
                    : Time.deltaTime;

            elapsedTime += deltaTime;

            float progress =
                Mathf.Clamp01(
                    elapsedTime / fadeDuration
                );

            /*
             * SmoothStep을 사용해 시작과 끝이
             * 조금 더 자연스럽게 보이도록 합니다.
             */
            progress =
                Mathf.SmoothStep(
                    0f,
                    1f,
                    progress
                );

            uiCanvasGroup.alpha =
                Mathf.Lerp(
                    startAlpha,
                    targetAlpha,
                    progress
                );

            yield return null;
        }

        ApplyAlpha(targetAlpha);
        fadeCoroutine = null;
    }

    /// <summary>
    /// 최종 투명도를 적용하고,
    /// 숨겨진 UI가 입력을 막지 않게 설정합니다.
    /// </summary>
    private void ApplyAlpha(float alpha)
    {
        if (uiCanvasGroup == null)
        {
            return;
        }

        alpha =
            Mathf.Clamp01(alpha);

        uiCanvasGroup.alpha =
            alpha;

        bool isVisible =
            alpha > 0.01f;

        uiCanvasGroup.interactable =
            isVisible;

        uiCanvasGroup.blocksRaycasts =
            isVisible;
    }

    /// <summary>
    /// 다른 스크립트에서 UI를 즉시 표시할 때 사용할 수 있습니다.
    /// </summary>
    public void ShowUIImmediately()
    {
        if (fadeCoroutine != null)
        {
            StopCoroutine(fadeCoroutine);
            fadeCoroutine = null;
        }

        ApplyAlpha(visibleAlpha);
    }

    /// <summary>
    /// 다른 스크립트에서 UI를 즉시 숨길 때 사용할 수 있습니다.
    /// </summary>
    public void HideUIImmediately()
    {
        if (fadeCoroutine != null)
        {
            StopCoroutine(fadeCoroutine);
            fadeCoroutine = null;
        }

        ApplyAlpha(hiddenAlpha);
    }
}