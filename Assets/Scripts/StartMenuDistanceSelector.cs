using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;

/// <summary>
/// StartMenu에서 거리를 선택하면
/// XR Origin, 일반 Feedback UI, Session Feedback UI의
/// World X 좌표를 변경하고 StartMenu를 닫습니다.
///
/// 게임 진행 중 다음 입력으로
/// 거리 선택 메뉴를 다시 표시할 수 있습니다.
///
/// - 키보드 D키
/// - 왼쪽 컨트롤러 Y 버튼
/// </summary>
public class StartMenuDistanceSelector : MonoBehaviour
{
    // =========================================================
    // Scene References
    // =========================================================

    [Header("Scene References")]

    [Tooltip(
        "Hierarchy의 XR Origin (XR Rig) 최상위 오브젝트"
    )]
    [SerializeField]
    private Transform xrOrigin;

    [Tooltip(
        "일반 Feedback Panel들을 포함하는 UI_Feedback 부모"
    )]
    [SerializeField]
    private Transform uiFeedback;

    [Tooltip(
        "3발 발사 후 종합 레포트를 표시하는 " +
        "SessionFeedbackUI 최상위 오브젝트"
    )]
    [SerializeField]
    private Transform sessionFeedbackUI;

    [Tooltip(
        "거리 선택 버튼들이 들어 있는 StartMenu"
    )]
    [SerializeField]
    private GameObject startMenu;


    // =========================================================
    // Feedback UI Control
    // =========================================================

    [Header("Feedback UI Control")]

    [Tooltip(
        "UI_Feedback에 붙어 있는 BowStringUIFader"
    )]
    [SerializeField]
    private BowStringUIFader bowStringUIFader;

    [Tooltip(
        "Feedback 토글을 제어하는 FeedbackCanvasToggle"
    )]
    [SerializeField]
    private FeedbackCanvasToggle feedbackCanvasToggle;


    // =========================================================
    // Distance Menu Shortcut
    // =========================================================

    [Header("Distance Menu Shortcut")]

    [Tooltip(
        "게임 진행 중 키보드 D키로 " +
        "거리 선택 메뉴를 다시 엽니다."
    )]
    [SerializeField]
    private bool enableKeyboardShortcut = true;

    [Tooltip(
        "게임 진행 중 왼쪽 컨트롤러 Y 버튼으로 " +
        "거리 선택 메뉴를 다시 엽니다."
    )]
    [SerializeField]
    private bool enableLeftControllerYButton = true;

    [Tooltip(
        "입력 감지 내용을 Console에 출력합니다."
    )]
    [SerializeField]
    private bool printInputDebugLog = true;


    // =========================================================
    // Internal State
    // =========================================================

    private bool distanceSelected;

    /*
     * 게임 시작 시 XR Origin과 StartMenu 사이의
     * World X 간격을 저장합니다.
     *
     * 거리 변경 후에도 이 간격을 유지하여
     * 현재 사용자 위치 앞에 메뉴가 나타나게 합니다.
     */
    private float startMenuXOffsetFromXrOrigin;

    private bool startMenuOffsetInitialized;

    /*
     * 왼쪽 컨트롤러 Y 버튼의 이전 프레임 상태입니다.
     *
     * 버튼을 누르고 있는 동안 ShowStartMenu가
     * 매 프레임 반복 실행되는 것을 방지합니다.
     */
    private bool previousLeftYButtonPressed;

    /*
     * 현재 사용 중인 왼쪽 XR 컨트롤러입니다.
     *
     * UnityEngine.InputSystem.InputDevice와 이름이 겹치므로
     * UnityEngine.XR.InputDevice 전체 이름을 사용합니다.
     */
    private UnityEngine.XR.InputDevice leftControllerDevice;


    // =========================================================
    // Unity Lifecycle
    // =========================================================

    private void Awake()
    {
        InitializeStartMenuOffset();
    }

    private void OnEnable()
    {
        previousLeftYButtonPressed = false;
        TryFindLeftController();
    }

    private void OnDisable()
    {
        previousLeftYButtonPressed = false;
    }

    private void Update()
    {
        CheckKeyboardShortcut();
        CheckLeftControllerYButton();
    }


    // =========================================================
    // Keyboard Input
    // =========================================================

    /// <summary>
    /// 키보드 D키 입력을 확인합니다.
    /// </summary>
    private void CheckKeyboardShortcut()
    {
        if (!enableKeyboardShortcut)
        {
            return;
        }

        /*
         * 프로젝트에 Keyboard.cs라는 별도 스크립트가 있어도
         * 충돌하지 않도록 전체 이름을 사용합니다.
         */
        UnityEngine.InputSystem.Keyboard keyboard =
            UnityEngine.InputSystem.Keyboard.current;

        if (keyboard == null)
        {
            return;
        }

        if (keyboard.dKey.wasPressedThisFrame)
        {
            LogInput(
                "키보드 D키 입력 → 거리 메뉴 표시"
            );

            ShowStartMenu();
        }
    }


    // =========================================================
    // Left Controller Input
    // =========================================================

    /// <summary>
    /// 왼쪽 컨트롤러의 Y 버튼을 확인합니다.
    ///
    /// Meta Quest 기준:
    /// - 왼쪽 X 버튼 = primaryButton
    /// - 왼쪽 Y 버튼 = secondaryButton
    /// </summary>
    private void CheckLeftControllerYButton()
    {
        if (!enableLeftControllerYButton)
        {
            previousLeftYButtonPressed = false;
            return;
        }

        /*
         * 컨트롤러가 연결되지 않았거나
         * Quest Link 재연결로 Device가 무효화된 경우
         * 다시 왼쪽 컨트롤러를 찾습니다.
         */
        if (!leftControllerDevice.isValid)
        {
            TryFindLeftController();
        }

        if (!leftControllerDevice.isValid)
        {
            previousLeftYButtonPressed = false;
            return;
        }

        bool currentLeftYButtonPressed;

        /*
         * UnityEngine.InputSystem.CommonUsages와
         * UnityEngine.XR.CommonUsages의 이름이 겹치므로
         * UnityEngine.XR.CommonUsages 전체 이름을 사용합니다.
         */
        bool valueReadSuccessfully =
            leftControllerDevice.TryGetFeatureValue(
                UnityEngine.XR.CommonUsages.secondaryButton,
                out currentLeftYButtonPressed
            );

        if (!valueReadSuccessfully)
        {
            previousLeftYButtonPressed = false;
            return;
        }

        /*
         * 이전 프레임에는 눌리지 않았고
         * 현재 프레임에 눌렸을 때만 실행합니다.
         *
         * 버튼을 누르고 있는 동안 ShowStartMenu가
         * 반복 실행되는 것을 방지합니다.
         */
        bool wasPressedThisFrame =
            currentLeftYButtonPressed &&
            !previousLeftYButtonPressed;

        if (wasPressedThisFrame)
        {
            LogInput(
                "왼쪽 컨트롤러 Y 버튼 입력 " +
                "→ 거리 메뉴 표시"
            );

            ShowStartMenu();
        }

        previousLeftYButtonPressed =
            currentLeftYButtonPressed;
    }

    /// <summary>
    /// 현재 연결된 왼쪽 XR 컨트롤러를 찾습니다.
    /// </summary>
    private void TryFindLeftController()
    {
        leftControllerDevice =
            UnityEngine.XR.InputDevices.GetDeviceAtXRNode(
                UnityEngine.XR.XRNode.LeftHand
            );

        if (leftControllerDevice.isValid)
        {
            LogInput(
                "왼쪽 XR 컨트롤러 연결 확인 | " +
                $"Device: {leftControllerDevice.name}"
            );
        }
      
    }


    // =========================================================
    // Distance Selection
    // =========================================================

    public void Select10m()
    {
        ApplyDistance(
            distanceName: "10m",
            xrOriginX: 4f,
            feedbackX: 0f,
            sessionFeedbackX: 1f
        );
    }

    public void Select15m()
    {
        ApplyDistance(
            distanceName: "15m",
            xrOriginX: 0f,
            feedbackX: -4f,
            sessionFeedbackX: -3f
        );
    }

    public void Select30m()
    {
        ApplyDistance(
            distanceName: "30m",
            xrOriginX: -3.5f,
            feedbackX: -7.5f,
            sessionFeedbackX: -6f
        );
    }

    private void ApplyDistance(
        string distanceName,
        float xrOriginX,
        float feedbackX,
        float sessionFeedbackX
    )
    {
        /*
         * 같은 메뉴가 열린 상태에서 버튼이
         * 중복으로 눌리는 것을 방지합니다.
         */
        if (distanceSelected)
        {
            return;
        }

        if (xrOrigin == null)
        {
            Debug.LogError(
                "[StartMenuDistanceSelector] " +
                "XR Origin이 연결되지 않았습니다.",
                this
            );

            return;
        }

        if (uiFeedback == null)
        {
            Debug.LogError(
                "[StartMenuDistanceSelector] " +
                "UI_Feedback이 연결되지 않았습니다.",
                this
            );

            return;
        }

        distanceSelected = true;

        // XR Origin의 World X 변경
        SetWorldX(
            xrOrigin,
            xrOriginX
        );

        // 일반 Feedback UI의 World X 변경
        SetWorldX(
            uiFeedback,
            feedbackX
        );

        // Session Feedback UI의 World X 변경
        if (sessionFeedbackUI != null)
        {
            SetWorldX(
                sessionFeedbackUI,
                sessionFeedbackX
            );
        }
        else
        {
            Debug.LogWarning(
                "[StartMenuDistanceSelector] " +
                "SessionFeedbackUI가 연결되지 않았습니다.",
                this
            );
        }

        /*
         * 이전 토글 상태로 UI가 비활성화되어 있을 수 있으므로
         * 거리 선택 후 다시 활성화합니다.
         */
        if (!uiFeedback.gameObject.activeSelf)
        {
            uiFeedback.gameObject.SetActive(true);
        }

        /*
         * 일반 Feedback UI와 Session Feedback UI를
         * 토글 ON 상태로 변경합니다.
         */
        if (feedbackCanvasToggle != null)
        {
            feedbackCanvasToggle.ShowFeedback();
        }

        /*
         * 일반 Feedback UI의 CanvasGroup을
         * 즉시 보이도록 설정합니다.
         */
        if (bowStringUIFader != null)
        {
            bowStringUIFader.ShowUIImmediately();
        }
        else
        {
            Debug.LogWarning(
                "[StartMenuDistanceSelector] " +
                "BowStringUIFader가 연결되지 않았습니다.",
                this
            );
        }

        // 거리 선택 완료 후 StartMenu 숨기기
        if (startMenu != null)
        {
            startMenu.SetActive(false);
        }

        Debug.Log(
            $"[StartMenuDistanceSelector] " +
            $"{distanceName} 선택 완료 | " +
            $"XR Origin X: {xrOrigin.position.x} | " +
            $"UI Feedback X: {uiFeedback.position.x} | " +
            $"Session Feedback X: " +
            $"{GetTransformX(sessionFeedbackUI)}",
            this
        );
    }


    // =========================================================
    // Start Menu
    // =========================================================

    /// <summary>
    /// 게임 시작 시 XR Origin과 StartMenu 사이의
    /// World X 간격을 저장합니다.
    /// </summary>
    private void InitializeStartMenuOffset()
    {
        if (startMenu == null || xrOrigin == null)
        {
            startMenuOffsetInitialized = false;
            return;
        }

        startMenuXOffsetFromXrOrigin =
            startMenu.transform.position.x -
            xrOrigin.position.x;

        startMenuOffsetInitialized = true;

        Debug.Log(
            "[StartMenuDistanceSelector] " +
            "StartMenu 초기 X 간격 저장 완료 | " +
            $"Offset X: {startMenuXOffsetFromXrOrigin}",
            this
        );
    }

    /// <summary>
    /// D키 또는 왼쪽 컨트롤러 Y 버튼으로 호출됩니다.
    ///
    /// 현재 XR Origin 위치에 맞춰
    /// 거리 선택 메뉴를 표시합니다.
    /// </summary>
    public void ShowStartMenu()
    {
        if (startMenu == null)
        {
            Debug.LogWarning(
                "[StartMenuDistanceSelector] " +
                "StartMenu가 연결되지 않았습니다.",
                this
            );

            return;
        }

        /*
         * 새로운 거리 버튼을 다시 누를 수 있도록
         * 선택 상태를 초기화합니다.
         */
        distanceSelected = false;

        if (!startMenuOffsetInitialized)
        {
            InitializeStartMenuOffset();
        }

        /*
         * StartMenu가 XR Origin의 자식이면
         * XR Origin 이동을 자동으로 따라가므로
         * 좌표를 중복으로 변경하지 않습니다.
         *
         * XR Origin의 자식이 아닌 경우에만
         * 저장된 X 간격을 사용해 위치를 옮깁니다.
         */
        if (
            xrOrigin != null &&
            !startMenu.transform.IsChildOf(xrOrigin)
        )
        {
            Vector3 menuPosition =
                startMenu.transform.position;

            menuPosition.x =
                xrOrigin.position.x +
                startMenuXOffsetFromXrOrigin;

            startMenu.transform.position =
                menuPosition;
        }

        startMenu.SetActive(true);

        Debug.Log(
            "[StartMenuDistanceSelector] " +
            "거리 선택 메뉴 다시 표시 | " +
            $"XR Origin X: {GetTransformX(xrOrigin)} | " +
            $"StartMenu X: " +
            $"{startMenu.transform.position.x}",
            this
        );
    }


    // =========================================================
    // Utility
    // =========================================================

    /// <summary>
    /// 대상의 World Y와 Z는 유지하고
    /// World X만 변경합니다.
    /// </summary>
    private static void SetWorldX(
        Transform target,
        float x
    )
    {
        if (target == null)
        {
            return;
        }

        Vector3 position =
            target.position;

        position.x = x;

        target.position =
            position;
    }

    private static float GetTransformX(
        Transform target
    )
    {
        return target != null
            ? target.position.x
            : 0f;
    }

    private void LogInput(
        string message
    )
    {
        if (!printInputDebugLog)
        {
            return;
        }

        Debug.Log(
            "[StartMenuDistanceSelector] " +
            message,
            this
        );
    }
}