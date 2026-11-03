using UnityEngine;

/// <summary>
/// 화살촉의 실제 로컬 축을 비행 속도 방향에 맞춥니다.
///
/// 현재 사용하는 화살 모델:
/// - local -Z: 화살촉 방향
/// - local +Z: 화살 꼬리 방향
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class ArrowFlight : MonoBehaviour
{
    private enum ArrowTipAxis
    {
        PositiveZ,
        NegativeZ
    }

    [Header("Model Axis")]
    [Tooltip("화살 모델에서 실제 화살촉이 향하는 로컬 축입니다.")]
    [SerializeField]
    private ArrowTipAxis tipAxis = ArrowTipAxis.NegativeZ;

    [Header("Flight Rotation")]
    [Tooltip("화살이 비행 속도 방향을 따라가는 속도입니다.")]
    [Min(0f)]
    [SerializeField]
    private float rotationFollowSpeed = 20f;

    [Tooltip("이 속도보다 느리면 방향 정렬을 하지 않습니다.")]
    [Min(0f)]
    [SerializeField]
    private float minimumSpeed = 0.2f;

    private Rigidbody arrowRigidbody;

    /// <summary>
    /// 현재 화살촉이 향하는 월드 방향입니다.
    /// </summary>
    public Vector3 TipDirectionWorld
    {
        get
        {
            Vector3 localTipAxis = GetLocalTipAxis();

            return transform
                .TransformDirection(localTipAxis)
                .normalized;
        }
    }

    private void Awake()
    {
        arrowRigidbody = GetComponent<Rigidbody>();
    }

    /// <summary>
    /// 화살 생성 직후 화살촉을 발사 방향으로 맞춥니다.
    /// </summary>
    public void AlignToDirection(
        Vector3 direction,
        Vector3 up)
    {
        if (direction.sqrMagnitude < 0.0001f)
            return;

        direction.Normalize();

        if (up.sqrMagnitude < 0.0001f)
            up = Vector3.up;

        up.Normalize();

        /*
         * direction과 up이 거의 평행하면
         * LookRotation이 불안정해질 수 있으므로 다른 Up을 사용합니다.
         */
        if (Mathf.Abs(Vector3.Dot(direction, up)) > 0.99f)
        {
            up = Vector3.right;
        }

        /*
         * 기본 LookRotation은 local +Z를 direction에 맞춥니다.
         */
        Quaternion targetRotation =
            Quaternion.LookRotation(direction, up);

        /*
         * 현재 모델은 local -Z가 화살촉이므로
         * Y축 180도에 해당하는 보정을 내부적으로 적용합니다.
         *
         * Inspector나 프리팹 Transform에서 따로 180도를 줄 필요는 없습니다.
         */
        if (tipAxis == ArrowTipAxis.NegativeZ)
        {
            targetRotation *=
                Quaternion.Euler(0f, 180f, 0f);
        }

        arrowRigidbody.rotation = targetRotation;
        transform.rotation = targetRotation;

        arrowRigidbody.angularVelocity = Vector3.zero;
    }

    private void FixedUpdate()
    {
        Vector3 velocity = GetLinearVelocity();

        if (velocity.sqrMagnitude <
            minimumSpeed * minimumSpeed)
        {
            return;
        }

        Vector3 flightDirection =
            velocity.normalized;

        Vector3 currentTipDirection =
            TipDirectionWorld;

        /*
         * 현재 화살촉 방향에서 실제 속도 방향으로 회전할
         * 최소 회전량을 계산합니다.
         */
        Quaternion correction =
            Quaternion.FromToRotation(
                currentTipDirection,
                flightDirection
            );

        Quaternion targetRotation =
            correction * arrowRigidbody.rotation;

        float rotationAmount =
            Mathf.Clamp01(
                rotationFollowSpeed *
                Time.fixedDeltaTime
            );

        Quaternion nextRotation =
            Quaternion.Slerp(
                arrowRigidbody.rotation,
                targetRotation,
                rotationAmount
            );

        /*
         * 물리 충돌이나 초기값으로 인한 무작위 회전을 제거합니다.
         */
        arrowRigidbody.angularVelocity =
            Vector3.zero;

        arrowRigidbody.MoveRotation(nextRotation);
    }

    private Vector3 GetLocalTipAxis()
    {
        return tipAxis == ArrowTipAxis.NegativeZ
            ? Vector3.back
            : Vector3.forward;
    }

    private Vector3 GetLinearVelocity()
    {
#if UNITY_6000_0_OR_NEWER
        return arrowRigidbody.linearVelocity;
#else
        return arrowRigidbody.velocity;
#endif
    }
}