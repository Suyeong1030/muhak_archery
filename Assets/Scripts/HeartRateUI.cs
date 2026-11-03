using TMPro;
using UnityEngine;

public class HeartRateUI : MonoBehaviour
{
    [Header("References")]

    [SerializeField]
    private VitalSignsReceiver vitalSignsReceiver;

    [SerializeField]
    private TMP_Text heartRateText;


    private void Awake()
    {
        if (vitalSignsReceiver == null)
        {
            vitalSignsReceiver =
                FindFirstObjectByType<VitalSignsReceiver>();
        }

        if (heartRateText == null)
        {
            heartRateText =
                GetComponent<TMP_Text>();
        }
    }


    private void OnEnable()
    {
        if (vitalSignsReceiver != null)
        {
            vitalSignsReceiver.HeartRateUpdated +=
                UpdateHeartRate;
        }
    }


    private void OnDisable()
    {
        if (vitalSignsReceiver != null)
        {
            vitalSignsReceiver.HeartRateUpdated -=
                UpdateHeartRate;
        }
    }


    private void UpdateHeartRate(float heartRate)
    {
        if (heartRateText == null)
        {
            return;
        }

        heartRateText.text =
            $"HR: {heartRate:F0} BPM";
    }
}