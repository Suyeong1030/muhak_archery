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

        // 과녁이 아닌 바닥·벽 등의 충돌은 점수로 처리하지 않습니다.
        // 실제 과녁 오브젝트(또는 부모)에 TargetScoring이 있어야 합니다.
        if (hitScoring == null)
            return;

        ContactPoint contact = collision.GetContact(0);
        int result = hitScoring.RegisterHit(contact.point);

        if (result >= 0)
        {
            resultSubmitted = true;
            Debug.Log($"[ArrowTargetScoringBridge] TargetScoring 점수 전달 완료: {result}", this);
        }
    }
}
