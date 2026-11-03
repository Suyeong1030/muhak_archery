using UnityEngine;
using UnityEngine.UI;

public class FeedbackCanvasToggle : MonoBehaviour
{
    [Header("Feedback Canvas")]

    [Tooltip("켜고 끌 일반 Canvas_Feedback 오브젝트")]
    [SerializeField]
    private GameObject feedbackCanvas;

    [Tooltip("일반 피드백과 함께 켜고 끌 Session Feedback UI")]
    [SerializeField]
    private GameObject sessionFeedbackCanvas;

    [Header("Toggle")]

    [Tooltip("사용자가 터치할 UI Toggle")]
    [SerializeField]
    private Toggle feedbackToggle;

    [Header("Start Setting")]

    [Tooltip("게임 시작 시 Feedback UI 표시 여부")]
    [SerializeField]
    private bool showOnStart = true;

    private void Awake()
    {
        if (feedbackToggle == null)
        {
            feedbackToggle = GetComponent<Toggle>();
        }

        ApplyFeedbackState(showOnStart);

        if (feedbackToggle != null)
        {
            feedbackToggle.SetIsOnWithoutNotify(showOnStart);
        }
    }

    private void OnEnable()
    {
        if (feedbackToggle != null)
        {
            feedbackToggle.onValueChanged.AddListener(
                HandleToggleChanged
            );
        }
    }

    private void OnDisable()
    {
        if (feedbackToggle != null)
        {
            feedbackToggle.onValueChanged.RemoveListener(
                HandleToggleChanged
            );
        }
    }

    private void HandleToggleChanged(bool isOn)
    {
        ApplyFeedbackState(isOn);
    }

    private void ApplyFeedbackState(bool isOn)
    {
        if (feedbackCanvas != null)
        {
            feedbackCanvas.SetActive(isOn);
        }

        if (
            sessionFeedbackCanvas != null &&
            sessionFeedbackCanvas != feedbackCanvas
        )
        {
            sessionFeedbackCanvas.SetActive(isOn);
        }
    }

    public void ShowFeedback()
    {
        SetToggleState(true);
    }

    public void HideFeedback()
    {
        SetToggleState(false);
    }

    public void ToggleFeedback()
    {
        bool nextState;

        if (feedbackCanvas != null)
        {
            nextState = !feedbackCanvas.activeSelf;
        }
        else if (sessionFeedbackCanvas != null)
        {
            nextState = !sessionFeedbackCanvas.activeSelf;
        }
        else
        {
            nextState = true;
        }

        SetToggleState(nextState);
    }

    private void SetToggleState(bool isOn)
    {
        ApplyFeedbackState(isOn);

        if (feedbackToggle != null)
        {
            feedbackToggle.SetIsOnWithoutNotify(isOn);
        }
    }
}