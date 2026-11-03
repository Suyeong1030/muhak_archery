using System;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// 활시위의 XR 입력과 외부 하드웨어 입력을 함께 처리합니다.
///
/// XR 입력:
/// - 시위 Select Enter → 잡기 시작
/// - 손/컨트롤러 이동 → Strength 갱신
/// - Select Exit → 발사
///
/// 외부 입력:
/// - BeginExternalPull() → 버튼 ON
/// - SetExternalStrength() → 당김값 갱신
/// - EndExternalPull() → 버튼 OFF 및 발사
/// - CancelExternalPull() → 통신 오류 시 발사 없이 취소
/// </summary>
public class BowStringController : MonoBehaviour
{
    private enum PullSource
    {
        None,
        XR,
        External
    }

    [Header("Bow String References")]

    [SerializeField]
    private XRGrabInteractable midpointGrabInteractable;

    [SerializeField]
    private Transform midpointGrabObject;

    [SerializeField]
    private Transform midpointVisual;

    [SerializeField]
    private Transform midpointParent;

    [Header("Pull Settings")]

    [Tooltip("VR에서 시위가 시각적으로 이동하는 최대 거리입니다.")]
    [Min(0.001f)]
    [SerializeField]
    private float stretchLimit = 0.5f;

    [Tooltip("시위를 놓거나 취소했을 때 원래 위치로 돌아가는 속도입니다.")]
    [Min(0f)]
    [SerializeField]
    private float resetSpeed = 20f;

    /// <summary>
    /// XR 또는 외부 입력으로 시위 잡기가 시작됐을 때 발생합니다.
    /// ArrowController, UI, Audio 등이 이 이벤트를 사용합니다.
    /// </summary>
    public event Action StringPullStarted;

    /// <summary>
    /// XR 또는 외부 입력으로 시위를 놓았을 때 발생합니다.
    /// 인자로 발사 직전 Strength가 전달됩니다.
    /// </summary>
    public event Action<float> BowReleased;

    public float Strength { get; private set; }

    public float CurrentStretch { get; private set; }

    public bool IsGrabbed =>
        currentPullSource != PullSource.None;

    public bool IsExternalPullActive =>
        currentPullSource == PullSource.External;

    private PullSource currentPullSource =
        PullSource.None;

    // 현재 XR Select를 시작한 Interactor입니다.
    private Transform currentInteractorRoot;

    // 컨트롤러 또는 손의 실제 잡기 지점입니다.
    private Transform currentGrabTransform;

    // XR 시위 잡기가 시작된 지점입니다.
    private Vector3 grabPointStartLocalPosition;

    private Vector3 visualStartLocalPosition;
    private Vector3 grabObjectStartLocalPosition;

    private void Awake()
    {
        if (midpointParent == null)
        {
            midpointParent = transform;
        }

        if (midpointVisual != null)
        {
            visualStartLocalPosition =
                midpointVisual.localPosition;
        }

        if (midpointGrabObject != null)
        {
            grabObjectStartLocalPosition =
                midpointGrabObject.localPosition;
        }
    }

    private void OnValidate()
    {
        stretchLimit =
            Mathf.Max(0.001f, stretchLimit);

        resetSpeed =
            Mathf.Max(0f, resetSpeed);
    }

    private void OnEnable()
    {
        /*
         * XRGrabInteractable이 없어도 외부 입력 모드는 사용할 수 있습니다.
         * 따라서 null이라고 스크립트 전체를 중단하지 않습니다.
         */
        if (midpointGrabInteractable != null)
        {
            midpointGrabInteractable
                .selectEntered
                .AddListener(OnSelectEntered);

            midpointGrabInteractable
                .selectExited
                .AddListener(OnSelectExited);
        }
        else
        {
            Debug.LogWarning(
                "[BowStringController] " +
                "Midpoint Grab Interactable이 연결되지 않았습니다. " +
                "XR 입력은 사용할 수 없지만 외부 입력은 사용할 수 있습니다.",
                this
            );
        }
    }

    private void OnDisable()
    {
        if (midpointGrabInteractable != null)
        {
            midpointGrabInteractable
                .selectEntered
                .RemoveListener(OnSelectEntered);

            midpointGrabInteractable
                .selectExited
                .RemoveListener(OnSelectExited);
        }

        ClearPullState();
        ResetStringImmediately();
    }

    private void Update()
    {
        switch (currentPullSource)
        {
            case PullSource.XR:
                if (currentGrabTransform != null)
                {
                    UpdateStringFromGrabPoint();
                }
                else
                {
                    CancelCurrentPull();
                }
                break;

            case PullSource.External:
                /*
                 * 외부 입력은 ServerBowInputReceiver 또는
                 * BowInputSimulator가 SetExternalStrength()를 호출합니다.
                 *
                 * 여기서 ResetString()을 호출하면 외부 Strength가
                 * 매 프레임 0으로 돌아가므로 아무것도 하지 않습니다.
                 */
                break;

            default:
                ResetString();
                break;
        }
    }

    // =========================================================
    // XR 입력
    // =========================================================

    private void OnSelectEntered(
        SelectEnterEventArgs args)
    {
        // 이미 XR 또는 외부 입력이 시위를 잡고 있으면 무시합니다.
        if (currentPullSource != PullSource.None)
        {
            return;
        }

        if (args.interactorObject == null)
        {
            return;
        }

        currentInteractorRoot =
            args.interactorObject.transform;

        /*
         * 손의 실제 핀치 위치 또는 컨트롤러의 Attach 위치를 가져옵니다.
         */
        currentGrabTransform =
            args.interactorObject.GetAttachTransform(
                midpointGrabInteractable
            );

        if (currentGrabTransform == null)
        {
            currentGrabTransform =
                currentInteractorRoot;
        }

        grabPointStartLocalPosition =
            midpointParent.InverseTransformPoint(
                currentGrabTransform.position
            );

        currentPullSource = PullSource.XR;

        SetStringStretch(0f);

        StringPullStarted?.Invoke();
    }

    private void OnSelectExited(
        SelectExitEventArgs args)
    {
        if (currentPullSource != PullSource.XR)
        {
            return;
        }

        /*
         * 현재 시위를 잡고 있는 Interactor가 아닌 곳에서 발생한
         * Exit 이벤트는 무시합니다.
         */
        if (currentInteractorRoot != null &&
            args.interactorObject != null &&
            args.interactorObject.transform !=
            currentInteractorRoot)
        {
            return;
        }

        ReleaseCurrentPull();
    }

    private void UpdateStringFromGrabPoint()
    {
        if (currentGrabTransform == null)
        {
            return;
        }

        Vector3 currentGrabLocalPosition =
            midpointParent.InverseTransformPoint(
                currentGrabTransform.position
            );

        /*
         * MidpointParent 기준 local -Z 방향으로 이동한 거리만
         * 활시위 당김 거리로 사용합니다.
         */
        float pulledDistance =
            grabPointStartLocalPosition.z -
            currentGrabLocalPosition.z;

        pulledDistance =
            Mathf.Clamp(
                pulledDistance,
                0f,
                stretchLimit
            );

        SetStringStretch(pulledDistance);
    }

    // =========================================================
    // 외부 하드웨어 입력
    // =========================================================

    /// <summary>
    /// Arduino 버튼이 OFF에서 ON으로 변경됐을 때 호출합니다.
    /// 화살 장전 및 시위 잡기를 시작합니다.
    /// </summary>
    public void BeginExternalPull()
    {
        if (currentPullSource ==
            PullSource.External)
        {
            // 동일한 ON 신호가 반복 수신돼도 다시 장전하지 않습니다.
            return;
        }

        if (currentPullSource ==
            PullSource.XR)
        {
            Debug.LogWarning(
                "[BowStringController] " +
                "XR 입력이 시위를 잡고 있어 외부 입력을 시작할 수 없습니다.",
                this
            );

            return;
        }

        currentInteractorRoot = null;
        currentGrabTransform = null;

        currentPullSource =
            PullSource.External;

        SetStringStretch(0f);

        StringPullStarted?.Invoke();

        Debug.Log(
            "[BowStringController] 외부 입력 잡기 시작",
            this
        );
    }

    /// <summary>
    /// 서버에서 받은 당김값을 0~1 Strength로 변환한 뒤 호출합니다.
    /// 이 함수에는 센서 원본값이 아니라 정규화된 Strength를 전달합니다.
    /// </summary>
    public void SetExternalStrength(
        float normalizedStrength)
    {
        if (currentPullSource !=
            PullSource.External)
        {
            return;
        }

        float safeStrength =
            Mathf.Clamp01(normalizedStrength);

        float externalStretch =
            stretchLimit * safeStrength;

        SetStringStretch(externalStretch);
    }

    /// <summary>
    /// Arduino 버튼이 ON에서 OFF로 변경됐을 때 호출합니다.
    /// 마지막 Strength로 BowReleased 이벤트를 발생시킵니다.
    /// </summary>
    public void EndExternalPull()
    {
        if (currentPullSource !=
            PullSource.External)
        {
            // 반복된 OFF 메시지로 중복 발사되는 것을 방지합니다.
            return;
        }

        ReleaseCurrentPull();

        Debug.Log(
            "[BowStringController] 외부 입력 놓기",
            this
        );
    }

    /// <summary>
    /// 서버 연결 끊김, 센서 오류 등에서 호출합니다.
    /// 화살을 발사하지 않고 시위만 원위치로 복귀시킵니다.
    /// </summary>
    public void CancelExternalPull()
    {
        if (currentPullSource !=
            PullSource.External)
        {
            return;
        }

        CancelCurrentPull();

        Debug.LogWarning(
            "[BowStringController] " +
            "외부 입력이 취소되었습니다. 화살은 발사하지 않습니다.",
            this
        );
    }

    // =========================================================
    // 공통 잡기 종료 처리
    // =========================================================

    private void ReleaseCurrentPull()
    {
        if (currentPullSource ==
            PullSource.None)
        {
            return;
        }

        /*
         * 이벤트 발생 전에 발사 Strength를 저장합니다.
         * BowReleased 이벤트 안에서 ArrowController가
         * CurrentStretch를 읽으므로 아직 0으로 초기화하면 안 됩니다.
         */
        float releasedStrength =
            Strength;

        ClearPullState();

        BowReleased?.Invoke(
            releasedStrength
        );

        /*
         * 시위 복귀는 다음 Update부터 ResetString()이 처리합니다.
         */
    }

    private void CancelCurrentPull()
    {
        ClearPullState();

        /*
         * BowReleased를 발생시키지 않으므로
         * ArrowController에서 화살이 발사되지 않습니다.
         */
    }

    private void ClearPullState()
    {
        currentPullSource =
            PullSource.None;

        currentInteractorRoot = null;
        currentGrabTransform = null;
    }

    // =========================================================
    // 시위 위치와 Strength
    // =========================================================

    private void SetStringStretch(
        float stretch)
    {
        CurrentStretch =
            Mathf.Clamp(
                stretch,
                0f,
                stretchLimit
            );

        if (stretchLimit > 0f)
        {
            Strength =
                Mathf.Clamp01(
                    CurrentStretch /
                    stretchLimit
                );
        }
        else
        {
            Strength = 0f;
        }

        if (midpointVisual == null)
        {
            return;
        }

        Vector3 visualPosition =
            visualStartLocalPosition;

        visualPosition.z -=
            CurrentStretch;

        midpointVisual.localPosition =
            visualPosition;
    }

    private void ResetString()
    {
        if (midpointVisual != null)
        {
            midpointVisual.localPosition =
                Vector3.Lerp(
                    midpointVisual.localPosition,
                    visualStartLocalPosition,
                    resetSpeed * Time.deltaTime
                );
        }

        if (midpointGrabObject != null)
        {
            midpointGrabObject.localPosition =
                Vector3.Lerp(
                    midpointGrabObject.localPosition,
                    grabObjectStartLocalPosition,
                    resetSpeed * Time.deltaTime
                );
        }

        CurrentStretch =
            Mathf.MoveTowards(
                CurrentStretch,
                0f,
                resetSpeed * Time.deltaTime
            );

        if (stretchLimit > 0f)
        {
            Strength =
                Mathf.Clamp01(
                    CurrentStretch /
                    stretchLimit
                );
        }
        else
        {
            Strength = 0f;
        }
    }

    private void ResetStringImmediately()
    {
        if (midpointVisual != null)
        {
            midpointVisual.localPosition =
                visualStartLocalPosition;
        }

        if (midpointGrabObject != null)
        {
            midpointGrabObject.localPosition =
                grabObjectStartLocalPosition;
        }

        CurrentStretch = 0f;
        Strength = 0f;
    }
}
