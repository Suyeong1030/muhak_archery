using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// VR 컨트롤러 없이 키보드 Space 키로
/// 한 발의 전체 서버 파이프라인을 테스트합니다.
///
/// 전송 순서:
/// DRAW -> ANCHOR -> AIM -> RELEASE -> SCORE_UPDATED
///
/// TargetScoring.RegisterHit()을 직접 호출하므로
/// 실제 물리 충돌 여부와 관계없이 지정한 위치에 명중합니다.
/// </summary>
public class KeyboardShotPipelineTest : MonoBehaviour
{
    [Header("Server")]
    [SerializeField]
    private ArtemisServerTest artemisServer;

    [Header("Scoring")]
    [SerializeField]
    private TargetScoring targetScoring;

    [Tooltip(
        "점수 판정에 사용할 위치입니다. " +
        "10점 테스트는 TargetScoring의 ScoreCenter를 연결하세요."
    )]
    [SerializeField]
    private Transform scoreHitPoint;

    [Header("Optional Visual Arrow")]
    [Tooltip(
        "과녁에 꽂힌 모습을 표시할 화살 프리팹입니다. " +
        "비워 두어도 상태와 점수 테스트는 정상 동작합니다."
    )]
    [SerializeField]
    private GameObject arrowPrefab;

    [SerializeField]
    private bool spawnVisualArrow = true;

    [Tooltip(
        "화살 중심을 충돌 지점에서 뒤로 떨어뜨리는 거리입니다."
    )]
    [Min(0f)]
    [SerializeField]
    private float arrowBackOffset = 0.35f;

    [Tooltip(
        "켜면 ScoreHitPoint의 -Forward 방향으로 화살이 날아온 것으로 배치합니다. " +
        "화살 방향이 반대라면 이 값을 끄세요."
    )]
    [SerializeField]
    private bool useNegativeTargetForward = true;

    [Tooltip(
        "다음 테스트 때 이전 테스트 화살을 삭제합니다."
    )]
    [SerializeField]
    private bool destroyPreviousTestArrow = true;

    [Header("Test Timing")]
    [Tooltip(
        "DRAW, ANCHOR, AIM, RELEASE 사이의 전송 간격입니다."
    )]
    [Min(0.05f)]
    [SerializeField]
    private float phaseIntervalSeconds = 0.15f;

    [Tooltip(
        "RELEASE 전송 후 SCORE_UPDATED를 보내기까지 기다리는 시간입니다. " +
        "서버의 점수 대기 시간보다 짧아야 합니다."
    )]
    [Min(0.05f)]
    [SerializeField]
    private float scoreDelaySeconds = 0.25f;

    [Header("Debug")]
    [SerializeField]
    private bool showDebugLog = true;

    private bool testRunning;
    private Coroutine testCoroutine;
    private GameObject previousTestArrow;

    private void Awake()
    {
        if (artemisServer == null)
        {
            artemisServer =
                FindFirstObjectByType<ArtemisServerTest>();
        }

        if (targetScoring == null)
        {
            targetScoring =
                FindFirstObjectByType<TargetScoring>();
        }
    }

    private void OnValidate()
    {
        phaseIntervalSeconds =
            Mathf.Max(
                0.05f,
                phaseIntervalSeconds
            );

        scoreDelaySeconds =
            Mathf.Max(
                0.05f,
                scoreDelaySeconds
            );

        arrowBackOffset =
            Mathf.Max(
                0f,
                arrowBackOffset
            );
    }

    private void Update()
    {
        if (
            Keyboard.current != null &&
            Keyboard.current.spaceKey.wasPressedThisFrame
        )
        {
            StartTestShot();
        }
    }

    /// <summary>
    /// UI Button 또는 다른 테스트 스크립트에서도 호출할 수 있습니다.
    /// </summary>
    public void StartTestShot()
    {
        if (testRunning)
        {
            Debug.LogWarning(
                "[KeyboardShotPipelineTest] " +
                "이전 테스트 샷이 아직 진행 중입니다.",
                this
            );

            return;
        }

        if (artemisServer == null)
        {
            Debug.LogError(
                "[KeyboardShotPipelineTest] " +
                "ArtemisServerTest가 연결되지 않았습니다.",
                this
            );

            return;
        }

        if (!artemisServer.IsConnected)
        {
            Debug.LogWarning(
                "[KeyboardShotPipelineTest] " +
                "서버 WebSocket 연결이 완료된 뒤 " +
                "Space 키를 누르세요.",
                this
            );

            return;
        }

        if (targetScoring == null)
        {
            Debug.LogError(
                "[KeyboardShotPipelineTest] " +
                "TargetScoring이 연결되지 않았습니다.",
                this
            );

            return;
        }

        if (scoreHitPoint == null)
        {
            Debug.LogError(
                "[KeyboardShotPipelineTest] " +
                "Score Hit Point가 연결되지 않았습니다. " +
                "10점 테스트는 ScoreCenter를 연결하세요.",
                this
            );

            return;
        }

        testCoroutine =
            StartCoroutine(
                RunTestShot()
            );
    }

    private IEnumerator RunTestShot()
    {
        testRunning = true;

        if (showDebugLog)
        {
            Debug.Log(
                "[KeyboardShotPipelineTest] " +
                "테스트 샷 시작",
                this
            );
        }

        artemisServer.SendUnityFrame(
            "DRAW"
        );

        yield return new WaitForSecondsRealtime(
            phaseIntervalSeconds
        );

        artemisServer.SendUnityFrame(
            "ANCHOR"
        );

        yield return new WaitForSecondsRealtime(
            phaseIntervalSeconds
        );

        artemisServer.SendUnityFrame(
            "AIM"
        );

        yield return new WaitForSecondsRealtime(
            phaseIntervalSeconds
        );

        /*
         * 서버는 RELEASE를 받은 뒤 SCORE_UPDATED를 기다립니다.
         */
        artemisServer.SendUnityFrame(
            "RELEASE"
        );

        /*
         * 실제 ArrowController 발사 대신 Shot Count를 직접 증가시킵니다.
         * RegisterShot()은 서버에 0점을 보내지 않습니다.
         */
        int shotCount =
            targetScoring.RegisterShot();

        SpawnStuckArrowVisual();

        yield return new WaitForSecondsRealtime(
            scoreDelaySeconds
        );

        /*
         * 지정한 월드 좌표로 점수를 계산합니다.
         * RegisterHit() 내부에서 SCORE_UPDATED가 서버로 전송됩니다.
         */
        int score =
            targetScoring.RegisterHit(
                scoreHitPoint.position
            );

        if (showDebugLog)
        {
            Debug.Log(
                "[KeyboardShotPipelineTest] " +
                "테스트 샷 완료" +
                $"\nScore: {score}" +
                $"\nShot Count: {shotCount}",
                this
            );
        }

        testRunning = false;
        testCoroutine = null;
    }

    private void SpawnStuckArrowVisual()
    {
        if (
            !spawnVisualArrow ||
            arrowPrefab == null ||
            scoreHitPoint == null
        )
        {
            return;
        }

        if (
            destroyPreviousTestArrow &&
            previousTestArrow != null
        )
        {
            Destroy(
                previousTestArrow
            );
        }

        Vector3 approachDirection =
            useNegativeTargetForward
                ? -scoreHitPoint.forward
                : scoreHitPoint.forward;

        approachDirection.Normalize();

        Vector3 spawnPosition =
            scoreHitPoint.position -
            approachDirection *
            arrowBackOffset;

        previousTestArrow =
            Instantiate(
                arrowPrefab,
                spawnPosition,
                Quaternion.identity
            );

        /*
         * 프로젝트 화살은 local -Z가 화살촉 방향이므로
         * ArrowFlight의 기존 정렬 함수를 사용합니다.
         */
        ArrowFlight arrowFlight =
            previousTestArrow
                .GetComponent<ArrowFlight>();

        if (arrowFlight != null)
        {
            arrowFlight.AlignToDirection(
                approachDirection,
                scoreHitPoint.up
            );
        }
        else
        {
            previousTestArrow.transform.rotation =
                Quaternion.LookRotation(
                    -approachDirection,
                    scoreHitPoint.up
                );
        }

        /*
         * 이 화살은 시각 효과 전용입니다.
         * 실제 충돌 스크립트가 점수를 한 번 더 계산하지 않도록
         * 물리와 동작 스크립트를 정지합니다.
         */
        MonoBehaviour[] behaviours =
            previousTestArrow
                .GetComponentsInChildren<MonoBehaviour>(
                    true
                );

        foreach (
            MonoBehaviour behaviour
            in behaviours
        )
        {
            behaviour.enabled = false;
        }

        Collider[] colliders =
            previousTestArrow
                .GetComponentsInChildren<Collider>(
                    true
                );

        foreach (
            Collider arrowCollider
            in colliders
        )
        {
            arrowCollider.enabled = false;
        }

        Rigidbody[] rigidbodies =
            previousTestArrow
                .GetComponentsInChildren<Rigidbody>(
                    true
                );

        foreach (
            Rigidbody arrowRigidbody
            in rigidbodies
        )
        {
#if UNITY_6000_0_OR_NEWER
            arrowRigidbody.linearVelocity =
                Vector3.zero;
#else
            arrowRigidbody.velocity =
                Vector3.zero;
#endif

            arrowRigidbody.angularVelocity =
                Vector3.zero;

            arrowRigidbody.useGravity = false;
            arrowRigidbody.isKinematic = true;
        }
    }

    private void OnDisable()
    {
        if (testCoroutine != null)
        {
            StopCoroutine(
                testCoroutine
            );
        }

        testCoroutine = null;
        testRunning = false;
    }

    [ContextMenu("Run Test Shot")]
    private void RunTestShotFromContextMenu()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning(
                "[KeyboardShotPipelineTest] " +
                "Play Mode에서 실행하세요.",
                this
            );

            return;
        }

        StartTestShot();
    }
}