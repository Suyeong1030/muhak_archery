using UnityEngine;

/// <summary>
/// 화살 Rigidbody의 실제 이동 속도를 측정하여 Console에 출력합니다.
///
/// 출력 내용:
/// - 발사 직후 실제 Rigidbody 속도
/// - 비행 중 현재속도, 평균속도, 최대속도
/// - 충돌 또는 과녁 고정 직전 속도
///
/// ArrowProjectile 프리팹에 추가해서 사용합니다.
/// 기존 ArrowController, ArrowFlight, ArrowStickOnHit 코드는 수정하지 않습니다.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class ArrowSpeedMonitor : MonoBehaviour
{
    [Header("Speed Logging")]

    [Tooltip("발사 순간의 실제 Rigidbody 속도를 출력합니다.")]
    [SerializeField]
    private bool logLaunchSpeed = true;

    [Tooltip("비행 중 속도를 일정한 간격으로 출력합니다.")]
    [SerializeField]
    private bool logDuringFlight = true;

    [Tooltip("충돌하거나 Kinematic으로 고정될 때 마지막 비행 속도를 출력합니다.")]
    [SerializeField]
    private bool logImpactSpeed = true;

    [Tooltip("비행 중 속도 출력 간격입니다.")]
    [Min(0.05f)]
    [SerializeField]
    private float logIntervalSeconds = 0.25f;

    [Tooltip("이 속도 이상이 되면 발사가 시작된 것으로 판단합니다.")]
    [Min(0.01f)]
    [SerializeField]
    private float launchDetectionSpeedMps = 0.1f;

    [Header("Impact Detection")]

    [Tooltip(
        "충돌 속도를 기록할 Layer입니다. " +
        "현재 프로젝트의 Target Layer 8이 기본값입니다."
    )]
    [SerializeField]
    private LayerMask impactLayers = 1 << 8;


    private Rigidbody arrowRigidbody;

    private bool launchDetected;
    private bool impactLogged;
    private bool measurementCompleted;

    private Vector3 lastPosition;

    private float launchFixedTime;
    private float nextLogFixedTime;

    private float traveledDistanceM;
    private float maximumSpeedMps;
    private float lastMovingSpeedMps;


    private void Awake()
    {
        arrowRigidbody =
            GetComponent<Rigidbody>();

        ResetMeasurement();
    }


    private void OnEnable()
    {
        ResetMeasurement();
    }


    private void FixedUpdate()
    {
        if (arrowRigidbody == null)
        {
            return;
        }

        if (measurementCompleted)
        {
            return;
        }

        Vector3 currentPosition =
            arrowRigidbody.position;

        Vector3 velocity =
            GetLinearVelocity();

        float currentSpeedMps =
            velocity.magnitude;

        if (!launchDetected)
        {
            lastPosition =
                currentPosition;

            if (
                currentSpeedMps >=
                launchDetectionSpeedMps
            )
            {
                DetectLaunch(
                    currentSpeedMps
                );
            }

            return;
        }

        traveledDistanceM +=
            Vector3.Distance(
                lastPosition,
                currentPosition
            );

        lastPosition =
            currentPosition;

        if (
            currentSpeedMps >=
            launchDetectionSpeedMps
        )
        {
            lastMovingSpeedMps =
                currentSpeedMps;

            maximumSpeedMps =
                Mathf.Max(
                    maximumSpeedMps,
                    currentSpeedMps
                );
        }

        /*
         * ArrowStickOnHit가 충돌 후 isKinematic을 true로 바꾸므로,
         * 실제 Collision 이벤트가 발생하지 않은 SphereCast 명중도
         * 이 조건을 통해 감지할 수 있습니다.
         */
        if (arrowRigidbody.isKinematic)
        {
            if (!impactLogged)
            {
                LogImpact(
                    "Arrow fixed"
                );
            }

            measurementCompleted = true;

            return;
        }

        if (
            logDuringFlight &&
            Time.fixedTime >=
            nextLogFixedTime
        )
        {
            LogFlightSpeed(
                currentSpeedMps
            );

            nextLogFixedTime =
                Time.fixedTime +
                logIntervalSeconds;
        }
    }


    private void OnCollisionEnter(
        Collision collision)
    {
        if (
            !launchDetected ||
            impactLogged ||
            collision == null ||
            collision.collider == null ||
            !IsImpactLayer(
                collision.collider.gameObject.layer
            )
        )
        {
            return;
        }

        LogImpact(
            collision.collider.name
        );
    }


    private void OnTriggerEnter(
        Collider other)
    {
        if (
            !launchDetected ||
            impactLogged ||
            other == null ||
            !IsImpactLayer(
                other.gameObject.layer
            )
        )
        {
            return;
        }

        LogImpact(
            other.name
        );
    }


    private void DetectLaunch(
        float initialSpeedMps)
    {
        launchDetected = true;
        impactLogged = false;
        measurementCompleted = false;

        launchFixedTime =
            Time.fixedTime;

        nextLogFixedTime =
            Time.fixedTime +
            logIntervalSeconds;

        traveledDistanceM = 0f;

        maximumSpeedMps =
            initialSpeedMps;

        lastMovingSpeedMps =
            initialSpeedMps;

        lastPosition =
            arrowRigidbody.position;

        if (!logLaunchSpeed)
        {
            return;
        }

        Debug.Log(
            "[Arrow Speed] LAUNCH"
            + $"\nMass: {arrowRigidbody.mass:F3} kg"
            + $"\nMeasured Speed: {initialSpeedMps:F2} m/s"
            + $"\nMeasured Speed: {ToKmh(initialSpeedMps):F2} km/h",
            this
        );
    }


    private void LogFlightSpeed(
        float currentSpeedMps)
    {
        float elapsedTimeSeconds =
            Mathf.Max(
                Time.fixedTime -
                launchFixedTime,
                0.0001f
            );

        float averageSpeedMps =
            traveledDistanceM /
            elapsedTimeSeconds;

        Debug.Log(
            "[Arrow Speed] FLIGHT"
            + $"\nTime: {elapsedTimeSeconds:F3} s"
            + $"\nDistance: {traveledDistanceM:F3} m"
            + $"\nCurrent: {currentSpeedMps:F2} m/s"
            + $"\nCurrent: {ToKmh(currentSpeedMps):F2} km/h"
            + $"\nAverage: {averageSpeedMps:F2} m/s"
            + $"\nMaximum: {maximumSpeedMps:F2} m/s",
            this
        );
    }


    private void LogImpact(
        string targetName)
    {
        if (impactLogged)
        {
            return;
        }

        impactLogged = true;
        measurementCompleted = true;

        if (!logImpactSpeed)
        {
            return;
        }

        float elapsedTimeSeconds =
            Mathf.Max(
                Time.fixedTime -
                launchFixedTime,
                0.0001f
            );

        float averageSpeedMps =
            traveledDistanceM /
            elapsedTimeSeconds;

        Debug.Log(
            "[Arrow Speed] IMPACT"
            + $"\nTarget: {targetName}"
            + $"\nFlight Time: {elapsedTimeSeconds:F3} s"
            + $"\nTravel Distance: {traveledDistanceM:F3} m"
            + $"\nLast Moving Speed: {lastMovingSpeedMps:F2} m/s"
            + $"\nLast Moving Speed: {ToKmh(lastMovingSpeedMps):F2} km/h"
            + $"\nAverage Speed: {averageSpeedMps:F2} m/s"
            + $"\nMaximum Speed: {maximumSpeedMps:F2} m/s",
            this
        );
    }


    private void ResetMeasurement()
    {
        launchDetected = false;
        impactLogged = false;
        measurementCompleted = false;

        launchFixedTime = 0f;
        nextLogFixedTime = 0f;

        traveledDistanceM = 0f;
        maximumSpeedMps = 0f;
        lastMovingSpeedMps = 0f;

        lastPosition =
            arrowRigidbody != null
                ? arrowRigidbody.position
                : transform.position;
    }


    private Vector3 GetLinearVelocity()
    {
#if UNITY_6000_0_OR_NEWER
        return arrowRigidbody.linearVelocity;
#else
        return arrowRigidbody.velocity;
#endif
    }


    private static float ToKmh(
        float speedMps)
    {
        return speedMps * 3.6f;
    }


    private bool IsImpactLayer(
        int layer)
    {
        return (
            impactLayers.value &
            (1 << layer)
        ) != 0;
    }

}