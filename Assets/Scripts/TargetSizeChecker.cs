using UnityEngine;

public class TargetSizeChecker : MonoBehaviour
{
    [SerializeField] private Transform scoreCenter;
    [SerializeField] private Transform scoreOuterEdge;

    private void Start()
    {
        float radius = Vector3.Distance(
            scoreCenter.position,
            scoreOuterEdge.position
        );

        float diameter = radius * 2f;

        Debug.Log($"타겟 월드 반지름: {radius:F3} m");
        Debug.Log($"타겟 월드 지름: {diameter:F3} m");
        Debug.Log($"타겟 지름: {diameter * 100f:F1} cm");
    }
}