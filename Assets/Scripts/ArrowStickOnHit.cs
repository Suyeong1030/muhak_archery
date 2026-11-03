using UnityEngine;

/// <summary>
/// 화살의 진행 방향 앞쪽을 SphereCast로 검사하여
/// Target에 닿기 전에 화살을 정지시키고 현재 자세로 고정합니다.
///
/// TargetScoring 컴포넌트가 타겟 또는 타겟의 부모에 있으면
/// 충돌 지점을 전달하여 점수를 계산합니다.
///
/// ArrowTip 오브젝트는 필요하지 않습니다.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(Collider))]
public class ArrowStickOnHit : MonoBehaviour
{
    [Header("Target Detection")]

    [Tooltip("Target Layer만 선택하세요.")]
    [SerializeField]
    private LayerMask stickableLayers;

    [Tooltip("빠른 화살이 과녁을 통과하지 않도록 검사하는 반지름입니다.")]
    [Min(0.001f)]
    [SerializeField]
    private float sweepRadius = 0.01f;

    [Tooltip("다음 물리 프레임보다 추가로 검사할 거리입니다.")]
    [Min(0f)]
    [SerializeField]
    private float extraSweepDistance = 0.15f;


    [Header("Stick Settings")]

    [Tooltip("화살 앞부분을 과녁 안쪽으로 넣는 거리입니다.")]
    [Min(0f)]
    [SerializeField]
    private float penetrationDepth = 0.03f;

    [Tooltip("꽂힌 후 화살 Collider를 비활성화합니다.")]
    [SerializeField]
    private bool disableColliderAfterStick = true;


    [Header("Score Settings")]

    [Tooltip("타겟에 명중했을 때 점수를 계산합니다.")]
    [SerializeField]
    private bool calculateScore = true;


    [Header("Runtime Debug")]

    [SerializeField]
    private bool hasStuck;

    [SerializeField]
    private bool hasScored;


    private Rigidbody arrowRigidbody;
    private Collider arrowCollider;
    private ArrowFlight arrowFlight;


    private void Awake()
    {
        arrowRigidbody = GetComponent<Rigidbody>();
        arrowCollider = GetComponent<Collider>();
        arrowFlight = GetComponent<ArrowFlight>();

        hasStuck = false;
        hasScored = false;
    }


    private void FixedUpdate()
    {
        if (hasStuck ||
            arrowRigidbody == null ||
            arrowCollider == null ||
            arrowRigidbody.isKinematic)
        {
            return;
        }

        Vector3 velocity = GetVelocity();

        if (velocity.sqrMagnitude < 0.0001f)
        {
            return;
        }

        float speed = velocity.magnitude;
        Vector3 flightDirection = velocity / speed;

        /*
         * 화살 모델의 Local 축이나 ArrowTip 위치를 사용하지 않고
         * 현재 진행 방향에서 Collider의 가장 앞쪽을 찾습니다.
         */
        Vector3 frontPoint =
            GetColliderFrontPoint(flightDirection);

        float sweepDistance =
            speed * Time.fixedDeltaTime +
            extraSweepDistance;

        bool targetDetected =
            Physics.SphereCast(
                frontPoint,
                sweepRadius,
                flightDirection,
                out RaycastHit hit,
                sweepDistance,
                stickableLayers,
                QueryTriggerInteraction.Collide
            );

        if (!targetDetected)
        {
            return;
        }

        StickArrow(
            hit.point,
            flightDirection,
            hit.collider
        );
    }


    /// <summary>
    /// Target가 Trigger Collider인 경우의 보조 처리입니다.
    /// </summary>
    private void OnTriggerEnter(Collider other)
    {
        if (hasStuck || other == null)
        {
            return;
        }

        if (!IsTargetLayer(other.gameObject.layer))
        {
            return;
        }

        Vector3 velocity = GetVelocity();

        Vector3 flightDirection =
            velocity.sqrMagnitude > 0.0001f
                ? velocity.normalized
                : transform.forward;

        Vector3 frontPoint =
            GetColliderFrontPoint(flightDirection);

        Vector3 hitPoint =
            other.ClosestPoint(frontPoint);

        StickArrow(
            hitPoint,
            flightDirection,
            other
        );
    }


    /// <summary>
    /// Target가 일반 Collider인 경우의 보조 처리입니다.
    /// </summary>
    private void OnCollisionEnter(Collision collision)
    {
        if (hasStuck ||
            collision == null ||
            collision.collider == null)
        {
            return;
        }

        if (!IsTargetLayer(
                collision.collider.gameObject.layer))
        {
            return;
        }

        Vector3 velocity = GetVelocity();

        Vector3 flightDirection =
            velocity.sqrMagnitude > 0.0001f
                ? velocity.normalized
                : transform.forward;

        Vector3 hitPoint =
            collision.contactCount > 0
                ? collision.GetContact(0).point
                : collision.collider.ClosestPoint(
                    GetColliderFrontPoint(flightDirection)
                );

        StickArrow(
            hitPoint,
            flightDirection,
            collision.collider
        );
    }


    /// <summary>
    /// 점수를 계산한 후 화살을 충돌 위치에 고정합니다.
    /// SphereCast, Trigger, Collision 처리가 모두 이 함수로 들어옵니다.
    /// </summary>
    private void StickArrow(
        Vector3 hitPoint,
        Vector3 flightDirection,
        Collider targetCollider)
    {
        if (hasStuck)
        {
            return;
        }

        /*
         * SphereCast와 Trigger 이벤트가 같은 프레임에 들어와도
         * 중복으로 처리되지 않도록 가장 먼저 true로 변경합니다.
         */
        hasStuck = true;

        /*
         * 화살 위치나 Collider를 변경하기 전에
         * 원래 충돌 위치를 이용하여 점수를 계산합니다.
         */
        RegisterScore(
            hitPoint,
            targetCollider
        );

        if (flightDirection.sqrMagnitude < 0.0001f)
        {
            flightDirection = transform.forward;
        }

        flightDirection.Normalize();

        /*
         * 충돌 직전의 회전을 그대로 보존합니다.
         */
        Quaternion rotationToKeep =
            arrowRigidbody.rotation;

        if (arrowFlight != null)
        {
            arrowFlight.enabled = false;
        }

        /*
         * 화살이 날아가는 방향에서 Collider 앞쪽을 구합니다.
         */
        Vector3 currentFrontPoint =
            GetColliderFrontPoint(flightDirection);

        /*
         * 화살 앞부분이 충돌 지점보다 약간 안쪽에 위치하게 합니다.
         */
        Vector3 desiredFrontPoint =
            hitPoint +
            flightDirection * penetrationDepth;

        Vector3 finalPosition =
            arrowRigidbody.position +
            (desiredFrontPoint - currentFrontPoint);

        /*
         * 모든 물리 움직임을 중지합니다.
         */
#if UNITY_6000_0_OR_NEWER
        arrowRigidbody.linearVelocity =
            Vector3.zero;
#else
        arrowRigidbody.velocity =
            Vector3.zero;
#endif

        arrowRigidbody.angularVelocity =
            Vector3.zero;

        arrowRigidbody.useGravity =
            false;

        arrowRigidbody.collisionDetectionMode =
            CollisionDetectionMode.Discrete;

        arrowRigidbody.position =
            finalPosition;

        arrowRigidbody.rotation =
            rotationToKeep;

        transform.SetPositionAndRotation(
            finalPosition,
            rotationToKeep
        );

        arrowRigidbody.isKinematic =
            true;

        if (disableColliderAfterStick &&
            arrowCollider != null)
        {
            arrowCollider.enabled = false;
        }

        /*
         * Target의 자식으로 만들지 않습니다.
         * Target의 비균일 Scale 때문에 화살이 비틀리는 것을 방지합니다.
         */
        Debug.Log(
            $"Arrow stuck to: {targetCollider.name}",
            this
        );
    }


    /// <summary>
    /// 타겟에 있는 TargetScoring 컴포넌트를 찾아
    /// 충돌 위치를 전달합니다.
    /// </summary>
    private void RegisterScore(
        Vector3 hitPoint,
        Collider targetCollider)
    {
        if (!calculateScore ||
            hasScored ||
            targetCollider == null)
        {
            return;
        }

        /*
         * Collider가 Target의 하위 오브젝트여도
         * 부모에서 TargetScoring을 찾습니다.
         */
        TargetScoring targetScoring =
            targetCollider.GetComponentInParent<TargetScoring>();

        if (targetScoring == null)
        {
            Debug.LogWarning(
                $"TargetScoring을 찾지 못했습니다. " +
                $"Target 또는 부모 오브젝트에 " +
                $"TargetScoring을 추가하세요. " +
                $"Collider: {targetCollider.name}",
                targetCollider
            );

            return;
        }

        int score =
            targetScoring.RegisterHit(hitPoint);

        hasScored = true;

        Debug.Log(
            $"Arrow score: {score}",
            this
        );
    }


    private Vector3 GetColliderFrontPoint(
        Vector3 flightDirection)
    {
        if (arrowCollider == null)
        {
            return transform.position;
        }

        if (flightDirection.sqrMagnitude < 0.0001f)
        {
            flightDirection = transform.forward;
        }

        flightDirection.Normalize();

        /*
         * 진행 방향으로 매우 먼 지점을 지정하면
         * ClosestPoint가 Collider의 가장 앞쪽 지점을 반환합니다.
         */
        Vector3 farForwardPoint =
            arrowCollider.bounds.center +
            flightDirection * 100f;

        return arrowCollider.ClosestPoint(
            farForwardPoint
        );
    }


    private bool IsTargetLayer(int layer)
    {
        return
            (stickableLayers.value &
             (1 << layer)) != 0;
    }


    private Vector3 GetVelocity()
    {
        if (arrowRigidbody == null)
        {
            return Vector3.zero;
        }

#if UNITY_6000_0_OR_NEWER
        return arrowRigidbody.linearVelocity;
#else
        return arrowRigidbody.velocity;
#endif
    }


    private void OnDrawGizmosSelected()
    {
        Collider currentCollider =
            GetComponent<Collider>();

        if (currentCollider == null)
        {
            return;
        }

        Vector3 direction =
            transform.forward;

        Rigidbody currentRigidbody =
            GetComponent<Rigidbody>();

        if (currentRigidbody != null)
        {
#if UNITY_6000_0_OR_NEWER
            Vector3 velocity =
                currentRigidbody.linearVelocity;
#else
            Vector3 velocity =
                currentRigidbody.velocity;
#endif

            if (velocity.sqrMagnitude > 0.0001f)
            {
                direction = velocity.normalized;
            }
        }

        Vector3 farForwardPoint =
            currentCollider.bounds.center +
            direction.normalized * 100f;

        Vector3 frontPoint =
            currentCollider.ClosestPoint(
                farForwardPoint
            );

        Gizmos.DrawWireSphere(
            frontPoint,
            sweepRadius
        );

        Gizmos.DrawLine(
            frontPoint,
            frontPoint +
            direction.normalized *
            extraSweepDistance
        );
    }
}