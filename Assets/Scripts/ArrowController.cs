using System;
using UnityEngine;

/// <summary>
/// 시위를 당길 때 VisualArrow를 표시하고,
/// 놓을 때 실제 ArrowProjectile을 생성해 발사합니다.
///
/// Inspector의 Launch Speed Mode에서 다음 방식을 선택할 수 있습니다.
/// - Draw Distance: 당김 거리와 활 에너지로 속도 계산
/// - Fixed Speed: Inspector에 입력한 고정 속도 사용
/// - Legacy Impulse: 기존 Impulse 발사 방식
///
/// 실제 Projectile 생성에 성공하면 ArrowLaunched 이벤트를 발생시켜
/// 발사 물리 데이터를 다른 스크립트에 전달합니다.
/// </summary>
public class ArrowController : MonoBehaviour
{
    private static readonly
        System.Collections.Generic.Dictionary<int, Material>
        XrSafeMaterialCache =
            new System.Collections.Generic.Dictionary<int, Material>();

    // =========================================================
    // 발사 속도 모드
    // =========================================================

    public enum LaunchSpeedMode
    {
        [InspectorName("Draw Distance")]
        DrawDistance,

        [InspectorName("Fixed Speed")]
        FixedSpeed,

        [InspectorName("Legacy Impulse")]
        LegacyImpulse
    }


    // =========================================================
    // 발사 데이터
    // =========================================================

    [Serializable]
    public class ArrowLaunchData
    {
        public float strength;

        public float drawLengthM;
        public float drawForceN;

        public float storedEnergyJ;
        public float transferredEnergyJ;

        public float arrowMassKg;
        public float launchSpeedMps;

        public Vector3 launchPosition;
        public Vector3 launchDirection;

        public float dragCoefficient;
        public float referenceAreaM2;

        public Vector3 windVelocityMps;
    }

    /// <summary>
    /// 실제 화살 Projectile 생성에 성공했을 때 호출됩니다.
    /// </summary>
    public event Action<ArrowLaunchData> ArrowLaunched;


    // =========================================================
    // Inspector 설정
    // =========================================================

    [Header("Bow")]

    [SerializeField]
    private BowStringController bowStringController;


    [Header("Visual Arrow")]

    [Tooltip("시위를 당길 때만 표시되는 장전용 화살입니다.")]
    [SerializeField]
    private GameObject visualArrow;


    [Header("Projectile")]

    [Tooltip("실제로 날아가는 ArrowProjectile 프리팹입니다.")]
    [SerializeField]
    private GameObject arrowProjectilePrefab;

    [Tooltip("화살 생성 위치이며 local +Z가 발사 방향입니다.")]
    [SerializeField]
    private Transform spawnPoint;


    [Header("Launch Speed Mode")]

    [Tooltip(
        "Draw Distance: 당김 거리와 에너지로 속도를 계산합니다.\n" +
        "Fixed Speed: Manual Launch Speed Mps에 입력한 속도로 발사합니다.\n" +
        "Legacy Impulse: 기존 Impulse 방식으로 발사합니다."
    )]
    [SerializeField]
    private LaunchSpeedMode launchSpeedMode =
        LaunchSpeedMode.DrawDistance;

    [Tooltip(
        "Fixed Speed 모드에서 실제로 적용할 초기속도입니다. " +
        "예: 50을 입력하면 50 m/s로 발사됩니다."
    )]
    [Min(0f)]
    [SerializeField]
    private float manualLaunchSpeedMps = 50f;


    [Header("Launch Settings")]

    [Tooltip(
        "이 Strength보다 약하게 당기면 화살을 생성하지 않습니다. " +
        "Fixed Speed 모드에서도 이 조건은 유지됩니다."
    )]
    [Range(0f, 1f)]
    [SerializeField]
    private float minimumFireStrength = 0.08f;

    [Tooltip("Legacy Impulse 모드의 최소 Impulse입니다.")]
    [Min(0f)]
    [SerializeField]
    private float minimumImpulse = 0.3f;

    [Tooltip("Legacy Impulse 모드의 최대 Impulse입니다.")]
    [Min(0f)]
    [SerializeField]
    private float maximumImpulse = 2.5f;


    [Header("Draw Distance Speed")]

    [Tooltip(
        "Draw Distance 모드에서 계산값 오류로 지나치게 빠른 화살이 " +
        "발생하지 않도록 제한하는 최대 속도입니다. 0이면 제한하지 않습니다."
    )]
    [Min(0f)]
    [SerializeField]
    private float maximumLaunchSpeedMps = 80f;


    [Header("VR Feel Correction (Draw Distance Only)")]

    [Tooltip(
        "Draw Distance 모드에서 물리식으로 계산된 속도에 곱하는 " +
        "VR 체감 보정 배율입니다. Fixed Speed에는 적용되지 않습니다. " +
        "1.0은 계산값 그대로, 1.5는 1.5배, 2.0은 2배입니다."
    )]
    [Range(0.5f, 3f)]
    [SerializeField]
    private float vrFeelSpeedMultiplier = 1f;


    [Header("Bow Physics Approximation")]

    [Tooltip(
        "실물 활을 최대 기준 당김 길이까지 당겼을 때의 힘입니다. " +
        "20 lbf 활은 약 89 N입니다."
    )]
    [Min(0f)]
    [SerializeField]
    private float maximumDrawForceN = 89f;

    [Tooltip("활의 저장 에너지 중 화살에 전달되는 비율입니다.")]
    [Range(0f, 1f)]
    [SerializeField]
    private float energyTransferEfficiency = 0.75f;


    [Header("Aerodynamics")]

    [Tooltip("화살의 대략적인 항력계수입니다.")]
    [Min(0f)]
    [SerializeField]
    private float dragCoefficient = 0.45f;

    [Tooltip("화살 진행 방향 기준 단면적입니다.")]
    [Min(0f)]
    [SerializeField]
    private float referenceAreaM2 = 0.00005f;

    [Tooltip("Unity World 기준 바람 속도입니다.")]
    [SerializeField]
    private Vector3 windVelocityMps = Vector3.zero;


    [Header("Bow Collision Ignore")]

    [Tooltip("발사된 화살과 충돌하지 않게 할 활의 루트입니다.")]
    [SerializeField]
    private Transform bowRoot;

    [Header("Optional Audio")]

    [SerializeField]
    private AudioSource releaseAudioSource;


    // =========================================================
    // 내부 변수
    // =========================================================

    private Collider[] bowColliders;


    // =========================================================
    // Unity 생명주기
    // =========================================================

    private void Awake()
    {
        SetVisualArrowActive(false);
        ApplyXrSafeMaterials(visualArrow);
        CacheBowColliders();

        if (releaseAudioSource != null)
        {
            releaseAudioSource.playOnAwake = false;
            releaseAudioSource.spatialBlend = 1f;
        }
    }


    private void OnEnable()
    {
        if (bowStringController == null)
        {
            Debug.LogWarning(
                "[ArrowController] " +
                "BowStringController가 연결되지 않았습니다.",
                this
            );

            return;
        }

        bowStringController.BowReleased +=
            HandleBowReleased;

        bowStringController.StringPullStarted +=
            HandleStringPullStarted;
    }


    private void OnDisable()
    {
        if (bowStringController == null)
            return;

        bowStringController.BowReleased -=
            HandleBowReleased;

        bowStringController.StringPullStarted -=
            HandleStringPullStarted;
    }


    private void ApplyXrSafeMaterials(GameObject targetRoot)
    {
        if (targetRoot == null)
        {
            return;
        }

        Shader safeShader =
            Shader.Find("Universal Render Pipeline/Unlit");

        if (safeShader == null)
        {
            return;
        }

        Renderer[] visualRenderers =
            targetRoot.GetComponentsInChildren<Renderer>(true);

        foreach (Renderer visualRenderer in visualRenderers)
        {
            Material[] sourceMaterials = visualRenderer.sharedMaterials;
            Material[] safeMaterials = new Material[sourceMaterials.Length];

            for (int index = 0; index < sourceMaterials.Length; index++)
            {
                Material sourceMaterial = sourceMaterials[index];
                Material safeMaterial;

                if (
                    sourceMaterial != null &&
                    XrSafeMaterialCache.TryGetValue(
                        sourceMaterial.GetInstanceID(),
                        out safeMaterial)
                )
                {
                    safeMaterials[index] = safeMaterial;
                    continue;
                }

                safeMaterial = new Material(safeShader);

                if (sourceMaterial != null)
                {
                    safeMaterial.color = sourceMaterial.color;
                    safeMaterial.mainTexture = sourceMaterial.mainTexture;

                    XrSafeMaterialCache[
                        sourceMaterial.GetInstanceID()
                    ] = safeMaterial;
                }

                safeMaterials[index] = safeMaterial;
            }

            visualRenderer.sharedMaterials = safeMaterials;
        }
    }


    // =========================================================
    // 활시위 이벤트
    // =========================================================

    private void HandleStringPullStarted()
    {
        SetVisualArrowActive(true);
    }


    private void HandleBowReleased(float strength)
    {
        strength = Mathf.Clamp01(strength);

        SetVisualArrowActive(false);

        if (strength < minimumFireStrength)
        {
            Debug.Log(
                "[ArrowController] 발사 강도가 너무 낮아 " +
                "화살을 생성하지 않았습니다. " +
                $"Strength: {strength:F3}",
                this
            );

            return;
        }

        FireProjectile(strength);
    }


    // =========================================================
    // 실제 화살 발사
    // =========================================================

    private void FireProjectile(float strength)
    {
        if (arrowProjectilePrefab == null)
        {
            Debug.LogWarning(
                "[ArrowController] " +
                "Arrow Projectile Prefab이 연결되지 않았습니다.",
                this
            );

            return;
        }

        if (spawnPoint == null)
        {
            Debug.LogWarning(
                "[ArrowController] " +
                "Spawn Point가 연결되지 않았습니다.",
                this
            );

            return;
        }

        GameObject projectile =
            Instantiate(
                arrowProjectilePrefab,
                spawnPoint.position,
                Quaternion.identity
            );

        ApplyXrSafeMaterials(projectile);

        Rigidbody projectileRigidbody =
            projectile.GetComponent<Rigidbody>();

        if (projectileRigidbody == null)
        {
            Debug.LogError(
                "[ArrowController] " +
                "화살 프리팹 루트에 Rigidbody가 없습니다.",
                projectile
            );

            Destroy(projectile);
            return;
        }

        ArrowFlight arrowFlight =
            projectile.GetComponent<ArrowFlight>();

        Vector3 launchDirection =
            spawnPoint.forward.normalized;

        if (arrowFlight != null)
        {
            arrowFlight.AlignToDirection(
                launchDirection,
                spawnPoint.up
            );
        }
        else
        {
            Debug.LogWarning(
                "[ArrowController] " +
                "화살에 ArrowFlight가 없습니다.",
                projectile
            );
        }

        projectileRigidbody.isKinematic = false;
        projectileRigidbody.useGravity = true;

        projectileRigidbody.interpolation =
            RigidbodyInterpolation.Interpolate;

        projectileRigidbody.collisionDetectionMode =
            CollisionDetectionMode.ContinuousDynamic;

#if UNITY_6000_0_OR_NEWER
        projectileRigidbody.linearVelocity =
            Vector3.zero;
#else
        projectileRigidbody.velocity =
            Vector3.zero;
#endif

        projectileRigidbody.angularVelocity =
            Vector3.zero;

        IgnoreBowCollisions(projectile);


        // =====================================================
        // 활·화살 물리 데이터 계산
        // =====================================================

        float arrowMassKg =
            Mathf.Max(
                projectileRigidbody.mass,
                0.0001f
            );

        /*
         * 발사 직전 실제 시위 당김 거리입니다.
         * BowStringController.CurrentStretch를 그대로 사용합니다.
         */
        float drawLengthM =
            GetReleasedDrawLength();

        /*
         * Strength 비율을 기준으로 현재 당김 힘을 근사합니다.
         */
        float drawForceN =
            maximumDrawForceN *
            strength;

        /*
         * 선형 Force-Draw Curve를 가정한 저장 에너지:
         * E_bow = 1/2 × F × x
         */
        float storedEnergyJ =
            0.5f *
            drawForceN *
            drawLengthM;

        /*
         * 화살에 실제로 전달된다고 가정한 에너지입니다.
         */
        float transferredEnergyJ =
            storedEnergyJ *
            energyTransferEfficiency;

        /*
         * 물리식 계산값은 선택 모드와 관계없이 계산해 둡니다.
         * Fixed Speed 사용 시에도 로그와 서버 텔레메트리에서
         * 물리 계산값과 실제 적용 속도를 비교할 수 있습니다.
         */
        float physicsLaunchSpeedMps = 0f;

        if (transferredEnergyJ > 0f)
        {
            physicsLaunchSpeedMps =
                Mathf.Sqrt(
                    2f *
                    transferredEnergyJ /
                    arrowMassKg
                );
        }

        float finalLaunchSpeedMps = 0f;
        string appliedLaunchMode;


        // =====================================================
        // Inspector에서 선택한 발사 방식 적용
        // =====================================================

        switch (launchSpeedMode)
        {
            case LaunchSpeedMode.DrawDistance:
                {
                    finalLaunchSpeedMps =
                        physicsLaunchSpeedMps *
                        vrFeelSpeedMultiplier;

                    if (maximumLaunchSpeedMps > 0f)
                    {
                        finalLaunchSpeedMps =
                            Mathf.Min(
                                finalLaunchSpeedMps,
                                maximumLaunchSpeedMps
                            );
                    }

                    SetProjectileVelocity(
                        projectileRigidbody,
                        launchDirection,
                        finalLaunchSpeedMps
                    );

                    appliedLaunchMode =
                        "DRAW_DISTANCE";

                    break;
                }


            case LaunchSpeedMode.FixedSpeed:
                {
                    /*
                     * 사용자가 Inspector에 입력한 m/s 값을
                     * Rigidbody 초기속도로 직접 적용합니다.
                     *
                     * Maximum Launch Speed와 VR Feel Multiplier는
                     * Fixed Speed 모드에 적용하지 않습니다.
                     */
                    finalLaunchSpeedMps =
                        Mathf.Max(
                            0f,
                            manualLaunchSpeedMps
                        );

                    SetProjectileVelocity(
                        projectileRigidbody,
                        launchDirection,
                        finalLaunchSpeedMps
                    );

                    appliedLaunchMode =
                        "FIXED_SPEED";

                    break;
                }


            case LaunchSpeedMode.LegacyImpulse:
                {
                    float impulse =
                        Mathf.Lerp(
                            minimumImpulse,
                            maximumImpulse,
                            strength
                        );

                    projectileRigidbody.AddForce(
                        launchDirection * impulse,
                        ForceMode.Impulse
                    );

                    /*
                     * Impulse = mass × velocity change
                     * 따라서 초기속도 = Impulse / mass
                     */
                    finalLaunchSpeedMps =
                        impulse /
                        arrowMassKg;

                    appliedLaunchMode =
                        "LEGACY_IMPULSE";

                    break;
                }


            default:
                {
                    finalLaunchSpeedMps =
                        physicsLaunchSpeedMps;

                    SetProjectileVelocity(
                        projectileRigidbody,
                        launchDirection,
                        finalLaunchSpeedMps
                    );

                    appliedLaunchMode =
                        "DRAW_DISTANCE";

                    break;
                }
        }

        if (releaseAudioSource != null)
        {
            releaseAudioSource.Play();
        }

        ArrowLaunchData launchData =
            BuildLaunchData(
                strength,
                drawLengthM,
                drawForceN,
                storedEnergyJ,
                transferredEnergyJ,
                arrowMassKg,
                finalLaunchSpeedMps,
                launchDirection
            );

        Debug.Log(
            "[ArrowController] 실제 화살 발사"
            + $"\nLaunch Mode: {appliedLaunchMode}"
            + $"\nStrength: {launchData.strength:F3}"
            + $"\nMass: {launchData.arrowMassKg:F4} kg"
            + $"\nPhysics Speed: {physicsLaunchSpeedMps:F2} m/s"
            + $"\nVR Feel Multiplier: {vrFeelSpeedMultiplier:F2}"
            + $"\nManual Speed: {manualLaunchSpeedMps:F2} m/s"
            + $"\nApplied Initial Speed: "
            + $"{launchData.launchSpeedMps:F2} m/s"
            + $"\nDraw Length: {launchData.drawLengthM:F3} m"
            + $"\nDraw Force: {launchData.drawForceN:F2} N"
            + $"\nStored Energy: {launchData.storedEnergyJ:F2} J"
            + $"\nTransferred Energy: "
            + $"{launchData.transferredEnergyJ:F2} J"
,
            this
        );

        /*
         * Projectile이 실제로 생성되고 Rigidbody까지 확인된 뒤에만
         * 발사 성공 이벤트를 발생시킵니다.
         */
        ArrowLaunched?.Invoke(launchData);
    }


    /// <summary>
    /// Unity 버전에 맞는 Rigidbody 속성에 초기속도를 적용합니다.
    /// </summary>
    private void SetProjectileVelocity(
        Rigidbody projectileRigidbody,
        Vector3 launchDirection,
        float launchSpeedMps)
    {
#if UNITY_6000_0_OR_NEWER
        projectileRigidbody.linearVelocity =
            launchDirection *
            launchSpeedMps;
#else
        projectileRigidbody.velocity =
            launchDirection *
            launchSpeedMps;
#endif
    }

    // =========================================================
    // 물리 데이터 계산
    // =========================================================

    /// <summary>
    /// 발사 직전의 실제 시위 당김 거리를 가져옵니다.
    /// </summary>
    private float GetReleasedDrawLength()
    {
        if (bowStringController == null)
        {
            return 0f;
        }

        return Mathf.Max(
            0f,
            bowStringController.CurrentStretch
        );
    }


    /// <summary>
    /// 실제 발사에 사용한 물리 데이터를 생성합니다.
    /// launchSpeedMps에는 선택한 모드에서 실제 적용된 속도가 저장됩니다.
    /// </summary>
    private ArrowLaunchData BuildLaunchData(
        float strength,
        float drawLengthM,
        float drawForceN,
        float storedEnergyJ,
        float transferredEnergyJ,
        float arrowMassKg,
        float launchSpeedMps,
        Vector3 launchDirection)
    {
        return new ArrowLaunchData
        {
            strength =
                strength,

            drawLengthM =
                drawLengthM,

            drawForceN =
                drawForceN,

            storedEnergyJ =
                storedEnergyJ,

            transferredEnergyJ =
                transferredEnergyJ,

            arrowMassKg =
                arrowMassKg,

            launchSpeedMps =
                launchSpeedMps,

            launchPosition =
                spawnPoint.position,

            launchDirection =
                launchDirection,

            dragCoefficient =
                dragCoefficient,

            referenceAreaM2 =
                referenceAreaM2,

            windVelocityMps =
                windVelocityMps
        };
    }


    // =========================================================
    // 충돌 처리
    // =========================================================

    private void CacheBowColliders()
    {
        if (bowRoot == null)
        {
            bowColliders =
                Array.Empty<Collider>();

            return;
        }

        bowColliders =
            bowRoot.GetComponentsInChildren<Collider>(
                true
            );
    }


    private void IgnoreBowCollisions(
        GameObject projectile)
    {
        if (
            projectile == null ||
            bowColliders == null ||
            bowColliders.Length == 0
        )
        {
            return;
        }

        Collider[] arrowColliders =
            projectile.GetComponentsInChildren<Collider>(
                true
            );

        foreach (
            Collider arrowCollider
            in arrowColliders
        )
        {
            if (arrowCollider == null)
                continue;

            foreach (
                Collider bowCollider
                in bowColliders
            )
            {
                if (bowCollider == null)
                    continue;

                Physics.IgnoreCollision(
                    arrowCollider,
                    bowCollider,
                    true
                );
            }
        }
    }


    // =========================================================
    // Visual Arrow
    // =========================================================

    private void SetVisualArrowActive(
        bool isActive)
    {
        if (visualArrow != null)
        {
            visualArrow.SetActive(isActive);
        }
    }
}
