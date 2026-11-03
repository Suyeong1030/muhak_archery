using System.Collections;
using TMPro;
using UnityEngine;

/// <summary>
/// 현재 점수, 누적 점수, 화살 발사 횟수를 관리합니다.
///
/// 주요 동작:
/// - 실제 화살 발사 시 Shot Count 증가
/// - 발사 순간에는 서버로 0점을 전송하지 않음
/// - 타깃 충돌 후 확정된 점수만 서버로 전송
/// - 여러 TargetScoring 인스턴스가 점수 상태를 공유
/// </summary>
public class TargetScoring : MonoBehaviour
{
    [Header("Target Points")]
    [SerializeField]
    private Transform scoreCenter;

    [SerializeField]
    private Transform scoreOuterEdge;

    [Header("Score UI")]
    [SerializeField]
    private TMP_Text currentScoreText;

    [SerializeField]
    private TMP_Text totalScoreText;

    [SerializeField]
    private TMP_Text shotCountText;

    [Header("Server")]
    [SerializeField]
    private ArtemisServerTest artemisServer;

    [Header("Debug")]
    [SerializeField]
    private bool showDebugLog = true;

    [Header("Miss Handling")]

    [Tooltip(
        "화살 발사 후 이 시간 안에 과녁 충돌이 없으면 " +
        "해당 발을 0점으로 확정하고 서버에 SCORE_UPDATED를 전송합니다."
    )]
    [Min(0.1f)]
    [SerializeField]
    private float missConfirmationSeconds = 1.5f;

    /*
     * 여러 TargetScoring이 있어도
     * 점수와 Shot Count를 공유하기 위해 static으로 관리합니다.
     */
    private static int currentScore;
    private static int totalScore;
    private static int shotCount;
    private static bool waitingForHit;

    private Coroutine missConfirmationCoroutine;
    private int pendingShotNumber;

    public int CurrentScore => currentScore;
    public int TotalScore => totalScore;
    public int ShotCount => shotCount;

    /// <summary>
    /// 과녁 원판 안쪽의 임의 월드 좌표를 반환합니다.
    /// </summary>
    public bool TryGetRandomHitPoint(
        out Vector3 hitPoint,
        float radiusRatio = 0.85f)
    {
        hitPoint = Vector3.zero;

        if (scoreCenter == null || scoreOuterEdge == null)
        {
            return false;
        }

        Vector3 center = scoreCenter.position;
        Vector3 radiusVector = scoreOuterEdge.position - center;
        float radius = radiusVector.magnitude;

        if (radius <= 0.001f)
        {
            return false;
        }

        Vector3 horizontalAxis = radiusVector / radius;
        Vector3 verticalAxis = Vector3.ProjectOnPlane(
            scoreCenter.up,
            horizontalAxis
        ).normalized;

        if (verticalAxis.sqrMagnitude < 0.99f)
        {
            verticalAxis = Vector3.Cross(
                scoreCenter.forward,
                horizontalAxis
            ).normalized;
        }

        Vector2 randomPoint = Random.insideUnitCircle;
        float safeRadius = radius * Mathf.Clamp01(radiusRatio);

        hitPoint =
            center +
            horizontalAxis * randomPoint.x * safeRadius +
            verticalAxis * randomPoint.y * safeRadius;

        return true;
    }

    /// <summary>
    /// Play Mode를 새로 시작할 때만
    /// 공유 점수 상태를 초기화합니다.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.SubsystemRegistration
    )]
    private static void ResetSharedStateOnPlayStart()
    {
        currentScore = 0;
        totalScore = 0;
        shotCount = 0;
        waitingForHit = false;
    }

    private void Awake()
    {
        if (artemisServer == null)
        {
            artemisServer =
                FindFirstObjectByType<ArtemisServerTest>();
        }

        RefreshUI();
    }

    private void OnEnable()
    {
        RefreshUI();
    }

    private void OnDisable()
    {
        if (missConfirmationCoroutine != null)
        {
            StopCoroutine(missConfirmationCoroutine);
            missConfirmationCoroutine = null;
        }
    }

    private void OnValidate()
    {
        missConfirmationSeconds =
            Mathf.Max(0.1f, missConfirmationSeconds);
    }

    /// <summary>
    /// 실제 화살이 생성되어 발사될 때 호출합니다.
    ///
    /// Shot Count는 증가시키지만,
    /// 아직 타깃 결과가 나오지 않았으므로
    /// 서버로 SCORE_UPDATED를 전송하지 않습니다.
    /// </summary>
    public int RegisterShot()
    {
        if (waitingForHit)
        {
            ConfirmMiss(pendingShotNumber);
        }

        shotCount++;
        currentScore = 0;
        waitingForHit = true;
        pendingShotNumber = shotCount;

        if (missConfirmationCoroutine != null)
        {
            StopCoroutine(missConfirmationCoroutine);
        }

        missConfirmationCoroutine = StartCoroutine(
            ConfirmMissAfterDelay(pendingShotNumber)
        );

        // 로컬 Unity UI만 갱신합니다.
        RefreshUI();

        if (showDebugLog)
        {
            Debug.Log(
                "[TargetScoring] 화살 발사" +
                $"\nCurrent Score: {currentScore}" +
                $"\nTotal Score: {totalScore}" +
                $"\nShot Count: {shotCount}",
                this
            );
        }

        return shotCount;
    }

    /// <summary>
    /// 화살이 타깃과 충돌했을 때 호출합니다.
    ///
    /// 타깃 중심으로부터 충돌 지점까지의 거리를 이용해
    /// 점수를 계산하고 최종 결과를 서버로 전송합니다.
    /// </summary>
    public int RegisterHit(
        Vector3 worldHitPoint)
    {
        if (!waitingForHit)
        {
            if (showDebugLog)
            {
                Debug.LogWarning(
                    "[TargetScoring] 이미 확정된 발의 늦은 충돌을 무시합니다.",
                    this
                );
            }

            return currentScore;
        }

        if (
            scoreCenter == null ||
            scoreOuterEdge == null
        )
        {
            Debug.LogError(
                "[TargetScoring] " +
                "ScoreCenter 또는 ScoreOuterEdge가 없습니다.",
                this
            );

            return -1;
        }

        Vector3 outerOffset =
            Vector3.ProjectOnPlane(
                scoreOuterEdge.position -
                scoreCenter.position,
                scoreCenter.forward
            );

        float outerRadius =
            outerOffset.magnitude;

        if (outerRadius <= Mathf.Epsilon)
        {
            Debug.LogError(
                "[TargetScoring] " +
                "타깃 반지름이 0입니다.",
                this
            );

            return -1;
        }

        Vector3 hitOffset =
            Vector3.ProjectOnPlane(
                worldHitPoint -
                scoreCenter.position,
                scoreCenter.forward
            );

        float hitRadius =
            hitOffset.magnitude;

        currentScore =
            CalculateScore(
                hitRadius,
                outerRadius
            );

        totalScore += currentScore;
        waitingForHit = false;

        if (missConfirmationCoroutine != null)
        {
            StopCoroutine(missConfirmationCoroutine);
            missConfirmationCoroutine = null;
        }

        RefreshUI();

        // 실제 충돌 후 확정된 점수만 서버로 전송합니다.
        SendCurrentScoreState();

        if (showDebugLog)
        {
            Debug.Log(
                "[TargetScoring] 점수 확정" +
                $"\nCurrent Score: {currentScore}" +
                $"\nTotal Score: {totalScore}" +
                $"\nShot Count: {shotCount}",
                this
            );
        }

        return currentScore;
    }

    private IEnumerator ConfirmMissAfterDelay(int shotNumber)
    {
        yield return new WaitForSecondsRealtime(
            missConfirmationSeconds
        );

        missConfirmationCoroutine = null;
        ConfirmMiss(shotNumber);
    }

    private void ConfirmMiss(int shotNumber)
    {
        if (
            !waitingForHit ||
            shotNumber != pendingShotNumber
        )
        {
            return;
        }

        currentScore = 0;
        waitingForHit = false;
        RefreshUI();
        SendCurrentScoreState();

        if (showDebugLog)
        {
            Debug.Log(
                "[TargetScoring] 과녁 미명중 - 0점 확정" +
                $"\nCurrent Score: {currentScore}" +
                $"\nTotal Score: {totalScore}" +
                $"\nShot Count: {shotCount}",
                this
            );
        }
    }

    /// <summary>
    /// 타깃 중심과 충돌 지점 사이의 거리를 기준으로
    /// 10점부터 1점까지 계산합니다.
    ///
    /// 타깃 바깥이면 0점을 반환합니다.
    /// </summary>
    private int CalculateScore(
        float hitRadius,
        float outerRadius)
    {
        float normalizedRadius =
            hitRadius /
            outerRadius;

        if (normalizedRadius > 1f)
        {
            return 0;
        }

        int ringNumber =
            Mathf.CeilToInt(
                normalizedRadius * 10f
            );

        int score =
            11 - ringNumber;

        return Mathf.Clamp(
            score,
            1,
            10
        );
    }

    /// <summary>
    /// 확정된 현재 점수, 누적점수,
    /// Shot Count를 서버로 전송합니다.
    /// </summary>
    private void SendCurrentScoreState()
    {
        if (artemisServer == null)
        {
            return;
        }

        artemisServer.SendScoreUpdated(
            currentScore,
            totalScore,
            shotCount
        );
    }

    /// <summary>
    /// 현재 점수와 누적점수, Shot Count UI를 갱신합니다.
    /// </summary>
    private void RefreshUI()
    {
        if (currentScoreText != null)
        {
            currentScoreText.text =
                waitingForHit || shotCount == 0
                    ? "-"
                    : currentScore.ToString();
        }

        if (totalScoreText != null)
        {
            totalScoreText.text =
                totalScore.ToString();
        }

        if (shotCountText != null)
        {
            shotCountText.text =
                shotCount.ToString();
        }
    }

    /// <summary>
    /// 사용자가 점수판 Reset을 실행했을 때
    /// 모든 점수 상태를 초기화합니다.
    /// </summary>
    public void ResetScore()
    {
        currentScore = 0;
        totalScore = 0;
        shotCount = 0;
        waitingForHit = false;

        RefreshUI();

        // Reset 결과는 서버에 전달합니다.
        SendCurrentScoreState();
    }

#if UNITY_EDITOR

    /// <summary>
    /// 선택한 타깃에 점수 원을 표시합니다.
    /// </summary>
    private void OnDrawGizmosSelected()
    {
        if (
            scoreCenter == null ||
            scoreOuterEdge == null
        )
        {
            return;
        }

        Vector3 outerOffset =
            Vector3.ProjectOnPlane(
                scoreOuterEdge.position -
                scoreCenter.position,
                scoreCenter.forward
            );

        float outerRadius =
            outerOffset.magnitude;

        if (outerRadius <= 0f)
        {
            return;
        }

        for (int i = 1; i <= 10; i++)
        {
            DrawCircle(
                scoreCenter.position,
                scoreCenter.right,
                scoreCenter.up,
                outerRadius * (i / 10f)
            );
        }
    }

    private static void DrawCircle(
        Vector3 center,
        Vector3 right,
        Vector3 up,
        float radius)
    {
        const int segments = 72;

        right.Normalize();
        up.Normalize();

        Vector3 previous =
            center +
            right * radius;

        for (
            int i = 1;
            i <= segments;
            i++
        )
        {
            float angle =
                i /
                (float)segments *
                Mathf.PI *
                2f;

            Vector3 next =
                center +
                right *
                Mathf.Cos(angle) *
                radius +
                up *
                Mathf.Sin(angle) *
                radius;

            Gizmos.DrawLine(
                previous,
                next
            );

            previous = next;
        }
    }

#endif
}
