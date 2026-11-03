using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 서버·Arduino 연결 전에 키보드로 외부 활 입력을 시험합니다.
///
/// Space 누름:
/// - 외부 시위 잡기 시작
///
/// Space 유지:
/// - 가상 Strength 갱신
///
/// Space 놓음:
/// - 마지막 Strength로 발사
///
/// R:
/// - 발사하지 않고 취소
/// </summary>
public class BowInputSimulator : MonoBehaviour
{
    [Header("References")]

    [Tooltip("외부 입력을 적용할 BowStringController")]
    [SerializeField]
    private BowStringController bowStringController;


    [Header("Simulator Settings")]

    [Tooltip("켜면 Space를 누르는 동안 Strength가 자동 증가합니다.")]
    [SerializeField]
    private bool autoIncreaseWhileHeld = true;

    [Tooltip("Space를 새로 누를 때 Strength를 0으로 초기화합니다.")]
    [SerializeField]
    private bool resetStrengthOnPress = true;

    [Tooltip("Inspector에서 직접 설정하는 테스트 Strength입니다.")]
    [Range(0f, 1f)]
    [SerializeField]
    private float testStrength = 0.8f;

    [Tooltip("자동 증가 모드에서 초당 Strength 증가량입니다.")]
    [Min(0f)]
    [SerializeField]
    private float increaseSpeed = 0.5f;

    [Tooltip("자동 증가할 수 있는 최대 Strength입니다.")]
    [Range(0f, 1f)]
    [SerializeField]
    private float maximumTestStrength = 1f;


    [Header("Debug")]

    [Tooltip("현재 시뮬레이터 버튼이 눌린 상태인지 표시합니다.")]
    [SerializeField]
    private bool isButtonPressed;

    [Tooltip("현재 BowStringController에 전달한 Strength입니다.")]
    [Range(0f, 1f)]
    [SerializeField]
    private float currentAppliedStrength;


    private void OnValidate()
    {
        testStrength = Mathf.Clamp01(testStrength);
        maximumTestStrength =
            Mathf.Clamp01(maximumTestStrength);

        increaseSpeed =
            Mathf.Max(0f, increaseSpeed);
    }

    private void Update()
    {
        if (bowStringController == null)
        {
            return;
        }

        Keyboard keyboard = Keyboard.current;

        if (keyboard == null)
        {
            return;
        }

        // Space 누르기 시작
        if (keyboard.spaceKey.wasPressedThisFrame)
        {
            BeginSimulatedPull();
        }

        // Space를 누르고 있는 동안 Strength 갱신
        if (isButtonPressed &&
            keyboard.spaceKey.isPressed)
        {
            UpdateSimulatedStrength();
        }

        // Space 놓기
        if (keyboard.spaceKey.wasReleasedThisFrame)
        {
            EndSimulatedPull();
        }

        // R 키를 누르면 발사하지 않고 취소
        if (keyboard.rKey.wasPressedThisFrame)
        {
            CancelSimulatedPull();
        }
    }

    private void OnDisable()
    {
        /*
         * Play 중 오브젝트가 꺼지거나 Scene이 바뀌면
         * 화살을 발사하지 않고 외부 입력을 취소합니다.
         */
        if (isButtonPressed &&
            bowStringController != null)
        {
            bowStringController
                .CancelExternalPull();
        }

        isButtonPressed = false;
        currentAppliedStrength = 0f;
    }

    /// <summary>
    /// Arduino 버튼 OFF → ON에 해당합니다.
    /// </summary>
    public void BeginSimulatedPull()
    {
        if (isButtonPressed)
        {
            return;
        }

        isButtonPressed = true;

        if (resetStrengthOnPress)
        {
            currentAppliedStrength = 0f;
        }
        else
        {
            currentAppliedStrength =
                Mathf.Clamp01(testStrength);
        }

        bowStringController
            .BeginExternalPull();

        bowStringController
            .SetExternalStrength(
                currentAppliedStrength
            );

        Debug.Log(
            "[BowInputSimulator] 버튼 ON / 외부 잡기 시작",
            this
        );
    }

    /// <summary>
    /// Space를 누르고 있는 동안 호출됩니다.
    /// </summary>
    private void UpdateSimulatedStrength()
    {
        if (autoIncreaseWhileHeld)
        {
            currentAppliedStrength =
                Mathf.MoveTowards(
                    currentAppliedStrength,
                    maximumTestStrength,
                    increaseSpeed * Time.deltaTime
                );
        }
        else
        {
            /*
             * 자동 증가를 끄면 Play 중에도 Inspector의
             * Test Strength 슬라이더로 값을 변경할 수 있습니다.
             */
            currentAppliedStrength =
                Mathf.Clamp01(testStrength);
        }

        bowStringController
            .SetExternalStrength(
                currentAppliedStrength
            );
    }

    /// <summary>
    /// Arduino 버튼 ON → OFF에 해당합니다.
    /// 마지막 Strength를 적용하고 발사합니다.
    /// </summary>
    public void EndSimulatedPull()
    {
        if (!isButtonPressed)
        {
            return;
        }

        /*
         * 버튼 OFF 직전의 마지막 Strength를 한 번 더 적용합니다.
         * 실제 서버 수신기에도 같은 순서가 들어갑니다.
         */
        bowStringController
            .SetExternalStrength(
                currentAppliedStrength
            );

        isButtonPressed = false;

        bowStringController
            .EndExternalPull();

        Debug.Log(
            "[BowInputSimulator] 버튼 OFF / 발사, Strength: " +
            currentAppliedStrength.ToString("F2"),
            this
        );
    }

    /// <summary>
    /// 통신 끊김 상황을 모의합니다.
    /// 발사하지 않고 시위를 취소합니다.
    /// </summary>
    public void CancelSimulatedPull()
    {
        if (!isButtonPressed &&
            !bowStringController.IsExternalPullActive)
        {
            return;
        }

        isButtonPressed = false;

        bowStringController
            .CancelExternalPull();

        currentAppliedStrength = 0f;

        Debug.LogWarning(
            "[BowInputSimulator] 외부 입력 취소",
            this
        );
    }
}