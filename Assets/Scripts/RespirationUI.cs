using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 호흡 데이터를 VR UI에 표시합니다.
///
/// 표시 항목:
/// - 호흡수: RR 14.0 BPM
/// - 호흡 단계: 들이마시는 중 / 내쉬는 중 / 멈춤
/// - 호흡 파형: Slider 및 원형 Indicator 크기
/// - 데이터가 오래 들어오지 않으면 대기 상태 표시
/// </summary>
public class RespirationUI : MonoBehaviour
{
    [Header("Receiver")]

    [SerializeField]
    private VitalSignsReceiver vitalSignsReceiver;


    [Header("Text UI")]

    [Tooltip("예: RR: 14.0 /min")]
    [SerializeField]
    private TMP_Text respirationRateText;

    [Tooltip("예: 들이마시는 중")]
    [SerializeField]
    private TMP_Text respirationPhaseText;


    [Header("Wave UI - Optional")]

    [Tooltip("0~1 범위의 호흡 파형을 표시할 Slider입니다.")]
    [SerializeField]
    private Slider respirationWaveSlider;

    [Tooltip(
        "호흡에 따라 커지고 작아질 원형 UI입니다. " +
        "사용하지 않으면 비워둘 수 있습니다."
    )]
    [SerializeField]
    private RectTransform breathingIndicator;


    [Header("Indicator Animation")]

    [Min(0.01f)]
    [SerializeField]
    private float minimumIndicatorScale = 0.75f;

    [Min(0.01f)]
    [SerializeField]
    private float maximumIndicatorScale = 1.25f;

    [Min(0f)]
    [SerializeField]
    private float smoothingSpeed = 8f;


    [Header("Stale Data")]

    [Tooltip(
        "이 시간 동안 새 데이터가 없으면 " +
        "호흡 데이터 대기 상태로 표시합니다."
    )]
    [Min(0.1f)]
    [SerializeField]
    private float staleAfterSeconds = 5f;


    private float currentWave;
    private float targetWave;

    private float lastReceivedTime =
        float.NegativeInfinity;

    private bool hasReceivedData;


    private void Awake()
    {
        if (vitalSignsReceiver == null)
        {
            vitalSignsReceiver =
                FindFirstObjectByType<VitalSignsReceiver>();
        }

        if (respirationWaveSlider != null)
        {
            respirationWaveSlider.minValue = 0f;
            respirationWaveSlider.maxValue = 1f;
            respirationWaveSlider.value = 0f;
        }

        ShowWaitingState();
    }


    private void OnEnable()
    {
        if (vitalSignsReceiver != null)
        {
            vitalSignsReceiver.RespirationUpdated +=
                HandleRespirationUpdated;
        }
        else
        {
            Debug.LogWarning(
                "[RespirationUI] " +
                "VitalSignsReceiver를 찾지 못했습니다.",
                this
            );
        }
    }


    private void OnDisable()
    {
        if (vitalSignsReceiver != null)
        {
            vitalSignsReceiver.RespirationUpdated -=
                HandleRespirationUpdated;
        }
    }


    private void Update()
    {
        UpdateWaveAnimation();

        if (
            hasReceivedData &&
            Time.unscaledTime -
            lastReceivedTime >
            staleAfterSeconds
        )
        {
            hasReceivedData = false;
            ShowWaitingState();
        }
    }


    private void HandleRespirationUpdated(
        float respiratoryRateBpm,
        string phase,
        float normalizedWave)
    {
        hasReceivedData = true;

        lastReceivedTime =
            Time.unscaledTime;

        targetWave =
            Mathf.Clamp01(
                normalizedWave
            );

        if (respirationRateText != null)
        {
            respirationRateText.text =
                $"RR: {respiratoryRateBpm:F1} /min";
        }

        if (respirationPhaseText != null)
        {
            respirationPhaseText.text =
                GetPhaseDisplayText(
                    phase
                );
        }
    }


    private void UpdateWaveAnimation()
    {
        float interpolation =
            1f -
            Mathf.Exp(
                -smoothingSpeed *
                Time.unscaledDeltaTime
            );

        currentWave =
            Mathf.Lerp(
                currentWave,
                targetWave,
                interpolation
            );

        if (respirationWaveSlider != null)
        {
            respirationWaveSlider.value =
                currentWave;
        }

        if (breathingIndicator != null)
        {
            float scale =
                Mathf.Lerp(
                    minimumIndicatorScale,
                    maximumIndicatorScale,
                    currentWave
                );

            breathingIndicator.localScale =
                Vector3.one * scale;
        }
    }


    private void ShowWaitingState()
    {
        targetWave = 0f;

        if (respirationRateText != null)
        {
            respirationRateText.text =
                "RR: -- /min";
        }

        if (respirationPhaseText != null)
        {
            respirationPhaseText.text =
                "호흡 데이터 대기";
        }
    }


    private static string GetPhaseDisplayText(
        string phase)
    {
        if (string.IsNullOrWhiteSpace(phase))
        {
            return "호흡 상태 확인 중";
        }

        switch (
            phase.Trim().ToLowerInvariant()
        )
        {
            case "inhale":
                return "들이마시는 중";

            case "exhale":
                return "내쉬는 중";

            case "hold":
                return "호흡 멈춤";

            default:
                return phase;
        }
    }
}