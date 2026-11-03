using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 오른쪽 실제 XR Controller의 Pose만 읽어서
/// 지정한 오브젝트에 적용합니다.
///
/// XRHandDevice의 Pose는 사용하지 않으므로
/// Hand Tracking이 활성화되어 있어도 손을 따라가지 않습니다.
/// </summary>
public class ControllerObjectMapper : MonoBehaviour
{
    [Header("Required References")]

    [Tooltip(
        "XR Origin 아래의 Camera Offset을 넣으세요."
    )]
    [SerializeField]
    private Transform trackingSpace;

    [Tooltip(
        "실제로 오른쪽 컨트롤러를 따라갈 오브젝트"
    )]
    [SerializeField]
    private Transform mappedObject;

    [Header("Offsets")]

    [Tooltip(
        "컨트롤러 로컬 좌표 기준 위치 보정"
    )]
    [SerializeField]
    private Vector3 positionOffset =
        Vector3.zero;

    [Tooltip(
        "컨트롤러 기준 회전 보정"
    )]
    [SerializeField]
    private Vector3 rotationOffset =
        Vector3.zero;

    [Header("Follow Settings")]

    [SerializeField]
    private bool followPosition = true;

    [SerializeField]
    private bool followRotation = true;

    [Tooltip(
        "컨트롤러가 추적되지 않을 때 오브젝트를 숨깁니다."
    )]
    [SerializeField]
    private bool hideWhenNotTracked = false;

    [Header("Runtime State")]

    [SerializeField]
    private bool controllerConnected;

    [SerializeField]
    private bool controllerTracked;

    [SerializeField]
    private Vector3 currentControllerPosition;

    [SerializeField]
    private Vector3 currentControllerRotation;

    private InputAction positionAction;
    private InputAction rotationAction;
    private InputAction trackedAction;

    private void Awake()
    {
        /*
         * 중요:
         * XRHandDevice가 아니라 XRController만 지정합니다.
         *
         * devicePosition/deviceRotation은
         * 실제 컨트롤러의 Grip/Device Pose입니다.
         */
        positionAction = new InputAction(
            name: "Right Controller Position",
            type: InputActionType.Value,
            binding:
                "<XRController>{RightHand}/devicePosition",
            expectedControlType: "Vector3"
        );

        rotationAction = new InputAction(
            name: "Right Controller Rotation",
            type: InputActionType.Value,
            binding:
                "<XRController>{RightHand}/deviceRotation",
            expectedControlType: "Quaternion"
        );

        trackedAction = new InputAction(
            name: "Right Controller Is Tracked",
            type: InputActionType.Button,
            binding:
                "<XRController>{RightHand}/isTracked",
            expectedControlType: "Button"
        );
    }

    private void OnEnable()
    {
        positionAction?.Enable();
        rotationAction?.Enable();
        trackedAction?.Enable();
    }

    private void OnDisable()
    {
        positionAction?.Disable();
        rotationAction?.Disable();
        trackedAction?.Disable();
    }

    private void OnDestroy()
    {
        positionAction?.Dispose();
        rotationAction?.Dispose();
        trackedAction?.Dispose();
    }

    private void LateUpdate()
    {
        if (mappedObject == null)
        {
            controllerConnected = false;
            controllerTracked = false;
            return;
        }

        /*
         * 현재 Action을 실제로 구동하는 장치가 있는지 확인합니다.
         * 손만 인식되고 컨트롤러가 없으면 activeControl은 null입니다.
         */
        controllerConnected =
            positionAction.activeControl != null &&
            rotationAction.activeControl != null;

        controllerTracked =
            controllerConnected &&
            trackedAction.ReadValue<float>() > 0.5f;

        if (!controllerTracked)
        {
            if (hideWhenNotTracked)
            {
                mappedObject.gameObject.SetActive(false);
            }

            return;
        }

        if (
            hideWhenNotTracked &&
            !mappedObject.gameObject.activeSelf
        )
        {
            mappedObject.gameObject.SetActive(true);
        }

        Vector3 controllerPosition =
            positionAction.ReadValue<Vector3>();

        Quaternion controllerRotation =
            rotationAction.ReadValue<Quaternion>();

        currentControllerPosition =
            controllerPosition;

        currentControllerRotation =
            controllerRotation.eulerAngles;

        Vector3 localTargetPosition =
            controllerPosition +
            controllerRotation * positionOffset;

        Quaternion localTargetRotation =
            controllerRotation *
            Quaternion.Euler(rotationOffset);

        if (followPosition)
        {
            mappedObject.position =
                trackingSpace != null
                    ? trackingSpace.TransformPoint(
                        localTargetPosition
                    )
                    : localTargetPosition;
        }

        if (followRotation)
        {
            mappedObject.rotation =
                trackingSpace != null
                    ? trackingSpace.rotation *
                      localTargetRotation
                    : localTargetRotation;
        }
    }
}