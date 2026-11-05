using UnityEngine;
using UnityEngine.InputSystem;

public class EMGCalibration : MonoBehaviour
{
    [Header("EMG Source")]
    public EMGManager emgManager;

    [Header("Baseline Calibration")]
    public float baselineDuration = 3f;

    public bool isMeasuringBaseline = false;
    public float baseline = 0f;

    [Header("MVC Calibration")]
    public float mvcDuration = 3f;
    public int requiredMvcTrials = 3;

    public bool isMeasuringMVC = false;
    public int completedMvcTrials = 0;

    public float mvcTrial1 = 0f;
    public float mvcTrial2 = 0f;
    public float mvcTrial3 = 0f;

    [Header("Final Calibration")]
    public float mvcValue = 0f;

    [Tooltip("현재 근활성도 (%MVC)")]
    public float normalizedEMG = 0f;

    [Header("Status")]
    public float measurementTimeRemaining = 0f;

    private float timer = 0f;

    // Baseline 계산용
    private float baselineSum = 0f;
    private int baselineSampleCount = 0;

    // MVC 측정용
    private float currentMvcPeak = 0f;

    void Update()
    {
        if (emgManager == null)
            return;

        HandleInput();

        if (isMeasuringBaseline)
        {
            UpdateBaselineMeasurement();
        }

        if (isMeasuringMVC)
        {
            UpdateMVCMeasurement();
        }

        CalculateNormalizedEMG();
    }

    // =========================
    // INPUT
    // =========================

    void HandleInput()
    {
        // 새 Input System 사용
        if (Keyboard.current == null)
            return;

        // B = Baseline 측정
        if (Keyboard.current.bKey.wasPressedThisFrame)
        {
            if (!isMeasuringBaseline && !isMeasuringMVC)
            {
                StartBaselineMeasurement();
            }
            else
            {
                Debug.Log("현재 Calibration 측정 중입니다.");
            }
        }

        // P = MVC 측정
        if (Keyboard.current.pKey.wasPressedThisFrame)
        {
            if (isMeasuringBaseline || isMeasuringMVC)
            {
                Debug.Log("현재 Calibration 측정 중입니다.");
                return;
            }

            if (completedMvcTrials >= requiredMvcTrials)
            {
                Debug.Log("MVC 측정이 이미 완료되었습니다.");
                return;
            }

            StartMVCMeasurement();
        }

        // R = 전체 Calibration 초기화
        if (Keyboard.current.rKey.wasPressedThisFrame)
        {
            ResetCalibration();
        }
    }

    // =========================
    // BASELINE
    // =========================

    void StartBaselineMeasurement()
    {
        timer = 0f;

        baselineSum = 0f;
        baselineSampleCount = 0;

        isMeasuringBaseline = true;

        measurementTimeRemaining = baselineDuration;

        Debug.Log(
            "Baseline 측정 시작 | " +
            baselineDuration +
            "초 동안 힘을 완전히 빼고 유지하세요."
        );
    }

    void UpdateBaselineMeasurement()
    {
        timer += Time.deltaTime;

        measurementTimeRemaining =
            Mathf.Max(0f, baselineDuration - timer);

        // 실제 사용할 filteredEMG 기준으로 calibration
        baselineSum += emgManager.filteredEMG;
        baselineSampleCount++;

        if (timer >= baselineDuration)
        {
            FinishBaselineMeasurement();
        }
    }

    void FinishBaselineMeasurement()
    {
        isMeasuringBaseline = false;

        if (baselineSampleCount > 0)
        {
            baseline =
                baselineSum / baselineSampleCount;
        }

        measurementTimeRemaining = 0f;

        Debug.Log(
            "Baseline 측정 완료 | Baseline = " +
            baseline.ToString("F4")
        );
    }

    // =========================
    // MVC
    // =========================

    void StartMVCMeasurement()
    {
        timer = 0f;
        currentMvcPeak = 0f;

        isMeasuringMVC = true;
        measurementTimeRemaining = mvcDuration;

        Debug.Log(
            "MVC Trial " +
            (completedMvcTrials + 1) +
            " / " +
            requiredMvcTrials +
            " 시작 | " +
            mvcDuration +
            "초 동안 최대 힘을 주세요."
        );
    }

    void UpdateMVCMeasurement()
    {
        timer += Time.deltaTime;

        measurementTimeRemaining =
            Mathf.Max(0f, mvcDuration - timer);

        // Baseline 제거
        float correctedEMG =
            Mathf.Max(
                0f,
                emgManager.filteredEMG - baseline
            );

        // 현재 Trial에서 가장 높은 값 기록
        if (correctedEMG > currentMvcPeak)
        {
            currentMvcPeak = correctedEMG;
        }

        if (timer >= mvcDuration)
        {
            FinishMVCMeasurement();
        }
    }

    void FinishMVCMeasurement()
    {
        isMeasuringMVC = false;
        measurementTimeRemaining = 0f;

        completedMvcTrials++;

        switch (completedMvcTrials)
        {
            case 1:
                mvcTrial1 = currentMvcPeak;
                break;

            case 2:
                mvcTrial2 = currentMvcPeak;
                break;

            case 3:
                mvcTrial3 = currentMvcPeak;
                break;
        }

        Debug.Log(
            "MVC Trial " +
            completedMvcTrials +
            " 완료 | Peak = " +
            currentMvcPeak.ToString("F4")
        );

        if (completedMvcTrials >= requiredMvcTrials)
        {
            CalculateFinalMVC();
        }
        else
        {
            Debug.Log(
                "휴식 후 P를 눌러 다음 MVC Trial을 진행하세요."
            );
        }
    }

    void CalculateFinalMVC()
    {
        // 현재 설정은 3회 측정을 전제로 함
        mvcValue =
            (mvcTrial1 +
             mvcTrial2 +
             mvcTrial3) / 3f;

        Debug.Log(
            "MVC Calibration 완료 | Final MVC = " +
            mvcValue.ToString("F4")
        );
    }

    // =========================
    // NORMALIZATION
    // =========================

    void CalculateNormalizedEMG()
    {
        if (mvcValue <= 0f)
        {
            normalizedEMG = 0f;
            return;
        }

        float correctedEMG =
            Mathf.Max(
                0f,
                emgManager.filteredEMG - baseline
            );

        normalizedEMG =
            (correctedEMG / mvcValue) * 100f;

        // 일부러 100으로 Clamp하지 않음.
        // 실제 측정에서 MVC calibration보다 높은 값이
        // 발생했는지 확인할 수 있도록 하기 위함.
    }

    // =========================
    // RESET
    // =========================

    public void ResetCalibration()
    {
        baseline = 0f;

        baselineSum = 0f;
        baselineSampleCount = 0;

        mvcTrial1 = 0f;
        mvcTrial2 = 0f;
        mvcTrial3 = 0f;

        currentMvcPeak = 0f;

        mvcValue = 0f;
        normalizedEMG = 0f;

        completedMvcTrials = 0;

        isMeasuringBaseline = false;
        isMeasuringMVC = false;

        timer = 0f;
        measurementTimeRemaining = 0f;

        Debug.Log("EMG Calibration 초기화 완료");
    }
}