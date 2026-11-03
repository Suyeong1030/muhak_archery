using UnityEngine;

/// <summary>
/// 활시위를 LineRenderer로 그리는 스크립트.
/// 
/// Edit Mode와 Play Mode 둘 다에서 시위 위치를 갱신합니다.
/// 사용 위치:
/// - BowStringVisualization 오브젝트에 붙입니다.
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(LineRenderer))]
public class BowString : MonoBehaviour
{
    [Header("String Points")]
    [SerializeField] private Transform endPoint1;
    [SerializeField] private Transform endPoint2;
    [SerializeField] private Transform midpoint;

    [Header("Renderer")]
    [SerializeField] private float stringWidth = 0.005f;

    private LineRenderer lineRenderer;

    private void Awake()
    {
        SetupLineRenderer();
        DrawString();
    }

    private void OnEnable()
    {
        SetupLineRenderer();
        DrawString();
    }

    private void OnValidate()
    {
        SetupLineRenderer();
        DrawString();
    }

    private void LateUpdate()
    {
        DrawString();
    }

    /// <summary>
    /// LineRenderer 기본 설정.
    /// Edit Mode에서도 계속 안전하게 적용되도록 분리했습니다.
    /// </summary>
    private void SetupLineRenderer()
    {
        if (lineRenderer == null)
            lineRenderer = GetComponent<LineRenderer>();

        if (lineRenderer == null)
            return;

        // 활과 같이 움직이게 하기 위해 로컬 공간 사용
        lineRenderer.useWorldSpace = false;

        // End_1 → MidpointVisual → End_2
        lineRenderer.positionCount = 3;

        lineRenderer.startWidth = stringWidth;
        lineRenderer.endWidth = stringWidth;
    }

    /// <summary>
    /// EndPoint와 Midpoint를 기준으로 시위를 그림.
    /// </summary>
    private void DrawString()
    {
        if (lineRenderer == null)
            lineRenderer = GetComponent<LineRenderer>();

        if (lineRenderer == null)
            return;

        if (endPoint1 == null || endPoint2 == null || midpoint == null)
            return;

        lineRenderer.SetPosition(0, transform.InverseTransformPoint(endPoint1.position));
        lineRenderer.SetPosition(1, transform.InverseTransformPoint(midpoint.position));
        lineRenderer.SetPosition(2, transform.InverseTransformPoint(endPoint2.position));
    }
}