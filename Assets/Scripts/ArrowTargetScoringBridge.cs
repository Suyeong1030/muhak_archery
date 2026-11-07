using UnityEngine;

/// <summary>
/// 발사된 화살의 충돌을 TargetScoring에 전달합니다.
/// 점수 계산은 TargetScoring.RegisterHit()만 수행합니다.
/// </summary>
public class ArrowTargetScoringBridge : MonoBehaviour
{
    private TargetScoring scoring;
    private bool resultSubmitted;

    public void Initialize(TargetScoring targetScoring)
    {
        scoring = targetScoring;
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (resultSubmitted)
            return;

        TargetScoring hitScoring =
            collision.collider.GetComponentInParent<TargetScoring>();

        if (hitScoring == null)
            hitScoring = scoring;

        if (hitScoring == null)
        {
            Debug.LogWarning(
                "[ArrowTargetScoringBridge] 충돌한 오브젝트에서 TargetScoring을 찾지 못했습니다.",
                this
            );
            return;
        }

        ContactPoint contact = collision.GetContact(0);
        int result = hitScoring.RegisterHit(contact.point);

        if (result >= 0)
        {
            resultSubmitted = true;
            Debug.Log($"[ArrowTargetScoringBridge] TargetScoring 점수 전달 완료: {result}", this);
        }
    }
}
