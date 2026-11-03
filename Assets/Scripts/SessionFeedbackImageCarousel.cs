using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

/// <summary>
/// 서버에서 여러 장의 Session AI Feedback 이미지를 다운로드하고,
/// Previous / Next 버튼으로 이미지를 전환합니다.
///
/// 실행 시 이미지 영역을 16:9로 자동 보정하고,
/// 페이지 텍스트와 버튼이 좁게 눌리지 않도록 최소 크기를 적용합니다.
/// </summary>
public class SessionFeedbackImageCarousel : MonoBehaviour
{
    private const float ReportAspectRatio = 16f / 9f;

    [Header("Panel")]

    [Tooltip("Session Feedback 이미지 패널 전체")]
    [SerializeField]
    private GameObject panelRoot;

    [Header("Image UI")]

    [Tooltip("현재 이미지를 표시할 RawImage")]
    [SerializeField]
    private RawImage feedbackRawImage;

    [Tooltip("이미지 원본 비율을 유지할 AspectRatioFitter")]
    [SerializeField]
    private AspectRatioFitter aspectRatioFitter;

    [Header("Navigation")]

    [Tooltip("이전 이미지 버튼")]
    [SerializeField]
    private Button previousButton;

    [Tooltip("다음 이미지 버튼")]
    [SerializeField]
    private Button nextButton;

    [Tooltip("현재 페이지를 표시할 텍스트. 예: 1 / 5")]
    [SerializeField]
    private TMP_Text pageText;

    [Tooltip("패널을 닫는 버튼. 없어도 됩니다.")]
    [SerializeField]
    private Button closeButton;

    [Header("Status")]

    [Tooltip("다운로드 상태 또는 오류 메시지를 표시할 텍스트")]
    [SerializeField]
    private TMP_Text statusText;

    [Header("Automatic Layout")]

    [Tooltip("실행 시 Session Feedback UI 크기와 16:9 비율을 자동 보정합니다.")]
    [SerializeField]
    private bool autoConfigureLayout = true;

    [Tooltip("World Space Canvas 내부 패널의 최소 크기입니다.")]
    [SerializeField]
    private Vector2 minimumPanelSize = new Vector2(960f, 600f);

    [Tooltip("리포트 이미지가 표시될 기준 크기입니다. 16:9 값을 사용하세요.")]
    [SerializeField]
    private Vector2 reportImageSize = new Vector2(800f, 450f);

    [Tooltip("페이지 숫자 텍스트의 최소 크기입니다.")]
    [SerializeField]
    private Vector2 pageTextMinimumSize = new Vector2(120f, 40f);

    [Tooltip("좌우 이동 버튼의 최소 크기입니다.")]
    [SerializeField]
    private Vector2 navigationButtonMinimumSize = new Vector2(52f, 52f);

    [Header("Navigation Settings")]

    [Tooltip("마지막 이미지에서 Next를 누르면 첫 번째 이미지로 이동합니다.")]
    [SerializeField]
    private bool loopImages = true;

    [Tooltip("게임 시작 시 패널을 숨깁니다.")]
    [SerializeField]
    private bool hideOnStart = true;

    [Header("Download Settings")]

    [Header("Server HTTP URL")]

    [Tooltip(
    "서버가 보내는 /reports/... 상대 URL 앞에 붙일 주소입니다.\n" +
    "Unity Editor 또는 Quest Link: http://127.0.0.1:8000\n" +
    "Quest APK 단독 실행: http://PC의IP주소:8000"
)]
    [SerializeField]
    private string serverHttpBaseUrl =
    "http://127.0.0.1:8000";

    [Tooltip(
        "다운로드된 파일이 실제 PNG 형식인지 " +
        "파일 헤더를 검사합니다."
    )]
    [SerializeField]
    private bool verifyPngSignature = true;

    [Tooltip("이미지 하나의 다운로드 제한 시간입니다.")]
    [SerializeField]
    private int timeoutSeconds = 20;

    [Tooltip("같은 URL의 이전 이미지가 캐시되는 것을 방지합니다.")]
    [SerializeField]
    private bool preventCaching = true;

    [Header("Debug")]

    [SerializeField]
    private bool showDebugLog = true;

    private readonly List<Texture2D> downloadedTextures =
        new List<Texture2D>();

    private readonly List<string> imageUrls =
        new List<string>();

    private int currentIndex;
    private Coroutine downloadCoroutine;

    private void Awake()
    {
        ValidateReferences();

        if (autoConfigureLayout)
        {
            ConfigureLayout();
        }

        if (previousButton != null)
        {
            previousButton.onClick.AddListener(ShowPreviousImage);
        }

        if (nextButton != null)
        {
            nextButton.onClick.AddListener(ShowNextImage);
        }

        if (closeButton != null)
        {
            closeButton.onClick.AddListener(Hide);
        }

        if (feedbackRawImage != null)
        {
            feedbackRawImage.texture = null;
            feedbackRawImage.enabled = false;
            feedbackRawImage.raycastTarget = false;
            feedbackRawImage.color = Color.white;
            feedbackRawImage.uvRect = new Rect(0f, 0f, 1f, 1f);
        }

        SetStatus(string.Empty, false);
        UpdatePageUI();

        if (hideOnStart && panelRoot != null)
        {
            panelRoot.SetActive(false);
        }
    }

    private void OnValidate()
    {
        reportImageSize.y = Mathf.Max(1f, reportImageSize.y);
        reportImageSize.x = reportImageSize.y * ReportAspectRatio;

        timeoutSeconds = Mathf.Max(1, timeoutSeconds);

        if (!Application.isPlaying && autoConfigureLayout)
        {
            ConfigureLayout();
        }
    }

    /// <summary>
    /// 패널, RawImage, 페이지 텍스트와 버튼의 최소 크기를 보정합니다.
    /// 씬에 저장된 RectTransform 값이 작거나 깨져 있어도 실행 시 복원합니다.
    /// </summary>
    [ContextMenu("Configure 16:9 Layout")]
    public void ConfigureLayout()
    {
        ConfigurePanelRect();
        ConfigureImageRect();
        ConfigurePageText();
        ConfigureButton(previousButton);
        ConfigureButton(nextButton);
    }

    private void ConfigurePanelRect()
    {
        if (panelRoot == null)
        {
            return;
        }

        RectTransform panelRect =
            panelRoot.GetComponent<RectTransform>();

        if (panelRect == null)
        {
            return;
        }

        Vector2 size = panelRect.sizeDelta;
        size.x = Mathf.Max(size.x, minimumPanelSize.x);
        size.y = Mathf.Max(size.y, minimumPanelSize.y);
        panelRect.sizeDelta = size;
    }

    private void ConfigureImageRect()
    {
        if (feedbackRawImage == null)
        {
            return;
        }

        RectTransform imageRect =
            feedbackRawImage.rectTransform;

        imageRect.anchorMin = new Vector2(0.5f, 0.5f);
        imageRect.anchorMax = new Vector2(0.5f, 0.5f);
        imageRect.pivot = new Vector2(0.5f, 0.5f);
        imageRect.sizeDelta = reportImageSize;

        feedbackRawImage.color = Color.white;
        feedbackRawImage.uvRect = new Rect(0f, 0f, 1f, 1f);

        if (aspectRatioFitter == null)
        {
            aspectRatioFitter =
                feedbackRawImage.GetComponent<AspectRatioFitter>();
        }

        if (aspectRatioFitter == null)
        {
            aspectRatioFitter =
                feedbackRawImage.gameObject.AddComponent<AspectRatioFitter>();
        }

        aspectRatioFitter.aspectMode =
            AspectRatioFitter.AspectMode.FitInParent;
        aspectRatioFitter.aspectRatio = ReportAspectRatio;
    }

    private void ConfigurePageText()
    {
        if (pageText == null)
        {
            return;
        }

        RectTransform pageRect = pageText.rectTransform;
        Vector2 size = pageRect.sizeDelta;
        size.x = Mathf.Max(size.x, pageTextMinimumSize.x);
        size.y = Mathf.Max(size.y, pageTextMinimumSize.y);
        pageRect.sizeDelta = size;

        pageText.enableWordWrapping = false;
        pageText.overflowMode = TextOverflowModes.Overflow;
        pageText.alignment = TextAlignmentOptions.Center;
        pageText.maxVisibleLines = 1;
    }

    private void ConfigureButton(Button button)
    {
        if (button == null)
        {
            return;
        }

        RectTransform buttonRect =
            button.GetComponent<RectTransform>();

        if (buttonRect == null)
        {
            return;
        }

        Vector2 size = buttonRect.sizeDelta;
        size.x = Mathf.Max(size.x, navigationButtonMinimumSize.x);
        size.y = Mathf.Max(size.y, navigationButtonMinimumSize.y);
        buttonRect.sizeDelta = size;
    }

    /// <summary>
    /// 서버에서 받은 이미지 URL 배열을 설정하고 다운로드를 시작합니다.
    /// </summary>
    public void ShowImages(string[] urls)
    {
        if (urls == null || urls.Length == 0)
        {
            ShowError("표시할 Session Feedback 이미지가 없습니다.");
            return;
        }

        Clear();

        if (autoConfigureLayout)
        {
            ConfigureLayout();
        }

        foreach (string url in urls)
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                imageUrls.Add(url.Trim());
            }
        }

        if (imageUrls.Count == 0)
        {
            ShowError("유효한 이미지 URL이 없습니다.");
            return;
        }

        currentIndex = 0;

        if (panelRoot != null)
        {
            panelRoot.SetActive(true);
        }

        SetStatus(
            $"Session Feedback 이미지를 불러오는 중... 0 / {imageUrls.Count}",
            true
        );

        UpdatePageUI();

        downloadCoroutine = StartCoroutine(
            DownloadAllImagesCoroutine()
        );
    }

    public void ShowPreviousImage()
    {
        if (downloadedTextures.Count == 0)
        {
            return;
        }

        if (loopImages)
        {
            currentIndex--;

            if (currentIndex < 0)
            {
                currentIndex = downloadedTextures.Count - 1;
            }
        }
        else
        {
            currentIndex = Mathf.Max(0, currentIndex - 1);
        }

        DisplayCurrentImage();
    }

    public void ShowNextImage()
    {
        if (downloadedTextures.Count == 0)
        {
            return;
        }

        if (loopImages)
        {
            currentIndex++;

            if (currentIndex >= downloadedTextures.Count)
            {
                currentIndex = 0;
            }
        }
        else
        {
            currentIndex = Mathf.Min(
                downloadedTextures.Count - 1,
                currentIndex + 1
            );
        }

        DisplayCurrentImage();
    }

    public void Hide()
    {
        if (panelRoot != null)
        {
            panelRoot.SetActive(false);
        }
    }

    public void Show()
    {
        if (panelRoot == null)
        {
            return;
        }

        if (autoConfigureLayout)
        {
            ConfigureLayout();
        }

        panelRoot.SetActive(true);

        if (downloadedTextures.Count > 0)
        {
            DisplayCurrentImage();
        }
    }

    public void Clear()
    {
        if (downloadCoroutine != null)
        {
            StopCoroutine(downloadCoroutine);
            downloadCoroutine = null;
        }

        if (feedbackRawImage != null)
        {
            feedbackRawImage.texture = null;
            feedbackRawImage.enabled = false;
        }

        foreach (Texture2D texture in downloadedTextures)
        {
            if (texture != null)
            {
                Destroy(texture);
            }
        }

        downloadedTextures.Clear();
        imageUrls.Clear();

        currentIndex = 0;

        SetStatus(string.Empty, false);
        UpdatePageUI();
    }

    private IEnumerator DownloadAllImagesCoroutine()
    {
        SetNavigationInteractable(false);

        int successCount = 0;
        int pngVerifiedCount = 0;

        for (int i = 0; i < imageUrls.Count; i++)
        {
            string originalUrl = imageUrls[i];

            string absoluteUrl =
                ResolveAbsoluteImageUrl(originalUrl);

            if (string.IsNullOrWhiteSpace(absoluteUrl))
            {
                Debug.LogError(
                    "[SessionFeedbackImageCarousel] " +
                    "절대 이미지 URL 생성 실패" +
                    $"\nOriginal URL: {originalUrl}",
                    this
                );

                continue;
            }

            string requestUrl =
                AddCacheBuster(absoluteUrl);

            if (showDebugLog)
            {
                Debug.Log(
                    "[SESSION PNG 다운로드 시작]" +
                    $"\nIndex: {i + 1} / {imageUrls.Count}" +
                    $"\nOriginal URL: {originalUrl}" +
                    $"\nAbsolute URL: {absoluteUrl}" +
                    $"\nRequest URL: {requestUrl}",
                    this
                );
            }

            using (
                UnityWebRequest request =
                    UnityWebRequestTexture.GetTexture(
                        requestUrl,
                        true
                    )
            )
            {
                request.timeout =
                    Mathf.Max(1, timeoutSeconds);

                yield return request.SendWebRequest();

                string contentType =
                    request.GetResponseHeader(
                        "Content-Type"
                    );

                if (
                    request.result !=
                    UnityWebRequest.Result.Success
                )
                {
                    Debug.LogError(
                        "[SESSION PNG 다운로드 실패]" +
                        $"\nIndex: {i + 1} / {imageUrls.Count}" +
                        $"\nOriginal URL: {originalUrl}" +
                        $"\nRequest URL: {requestUrl}" +
                        $"\nHTTP Code: {request.responseCode}" +
                        $"\nContent-Type: {contentType}" +
                        $"\nError: {request.error}",
                        this
                    );

                    continue;
                }

                byte[] downloadedData =
                    request.downloadHandler != null
                        ? request.downloadHandler.data
                        : null;

                bool pngSignatureValid =
                    HasPngSignature(downloadedData);

                if (
                    verifyPngSignature &&
                    !pngSignatureValid
                )
                {
                    Debug.LogError(
                        "[SESSION PNG 형식 검증 실패]" +
                        $"\nIndex: {i + 1} / {imageUrls.Count}" +
                        $"\nRequest URL: {requestUrl}" +
                        $"\nHTTP Code: {request.responseCode}" +
                        $"\nContent-Type: {contentType}" +
                        $"\nDownloaded Bytes: " +
                        $"{request.downloadedBytes}" +
                        "\n서버가 PNG 대신 HTML 오류 페이지나 " +
                        "다른 파일을 반환했을 가능성이 있습니다.",
                        this
                    );

                    continue;
                }

                Texture2D texture;

                try
                {
                    texture =
                        DownloadHandlerTexture.GetContent(
                            request
                        );
                }
                catch (Exception exception)
                {
                    Debug.LogError(
                        "[SESSION PNG Texture 변환 실패]" +
                        $"\nRequest URL: {requestUrl}" +
                        $"\nError: {exception.Message}",
                        this
                    );

                    continue;
                }

                if (texture == null)
                {
                    Debug.LogError(
                        "[SESSION PNG Texture 없음]" +
                        $"\nRequest URL: {requestUrl}",
                        this
                    );

                    continue;
                }

                texture.name =
                    $"SessionFeedback_{i + 1}";

                downloadedTextures.Add(texture);

                successCount++;

                if (pngSignatureValid)
                {
                    pngVerifiedCount++;
                }

                Debug.Log(
                    "[SESSION PNG 다운로드 성공]" +
                    $"\nIndex: {i + 1} / {imageUrls.Count}" +
                    $"\nOriginal URL: {originalUrl}" +
                    $"\nAbsolute URL: {absoluteUrl}" +
                    $"\nHTTP Code: {request.responseCode}" +
                    $"\nContent-Type: {contentType}" +
                    $"\nDownloaded Bytes: " +
                    $"{request.downloadedBytes}" +
                    $"\nPNG Signature: {pngSignatureValid}" +
                    $"\nTexture Size: " +
                    $"{texture.width} x {texture.height}" +
                    $"\nAspect Ratio: " +
                    $"{(float)texture.width / texture.height:F4}",
                    this
                );

                if (successCount == 1)
                {
                    currentIndex = 0;
                    DisplayCurrentImage();
                }

                SetStatus(
                    "Session Feedback PNG 다운로드 중... " +
                    $"{i + 1} / {imageUrls.Count}",
                    true
                );
            }
        }

        downloadCoroutine = null;

        if (downloadedTextures.Count == 0)
        {
            ShowError(
                "Session Feedback PNG를 " +
                "다운로드하지 못했습니다."
            );

            yield break;
        }

        SetStatus(
            string.Empty,
            false
        );

        SetNavigationInteractable(true);
        DisplayCurrentImage();

        Debug.Log(
            "[SESSION PNG 수신 검증 완료]" +
            $"\nRequested: {imageUrls.Count}" +
            $"\nDownloaded: {successCount}" +
            $"\nPNG Verified: {pngVerifiedCount}" +
            $"\nFailed: {imageUrls.Count - successCount}",
            this
        );
    }

    private void DisplayCurrentImage()
    {
        if (
            feedbackRawImage == null ||
            downloadedTextures.Count == 0
        )
        {
            UpdatePageUI();
            return;
        }

        currentIndex = Mathf.Clamp(
            currentIndex,
            0,
            downloadedTextures.Count - 1
        );

        Texture2D texture = downloadedTextures[currentIndex];

        if (texture == null)
        {
            ShowError("현재 이미지 Texture가 없습니다.");
            return;
        }

        feedbackRawImage.texture = texture;
        feedbackRawImage.color = Color.white;
        feedbackRawImage.uvRect = new Rect(0f, 0f, 1f, 1f);
        feedbackRawImage.enabled = true;

        if (aspectRatioFitter != null)
        {
            aspectRatioFitter.aspectMode =
                AspectRatioFitter.AspectMode.FitInParent;

            aspectRatioFitter.aspectRatio =
                texture.height > 0
                    ? (float)texture.width / texture.height
                    : ReportAspectRatio;
        }

        UpdatePageUI();

        if (showDebugLog)
        {
            Debug.Log(
                $"[SessionFeedbackImageCarousel] " +
                $"이미지 표시: {currentIndex + 1} / " +
                $"{downloadedTextures.Count} " +
                $"({texture.width}x{texture.height})",
                this
            );
        }
    }

    private void UpdatePageUI()
    {
        int count = downloadedTextures.Count;

        if (pageText != null)
        {
            pageText.text =
                count > 0
                    ? $"{currentIndex + 1} / {count}"
                    : "0 / 0";
        }

        UpdateNavigationButtonState();
    }

    private void UpdateNavigationButtonState()
    {
        int count = downloadedTextures.Count;

        if (previousButton != null)
        {
            previousButton.interactable =
                count > 1 &&
                (loopImages || currentIndex > 0);
        }

        if (nextButton != null)
        {
            nextButton.interactable =
                count > 1 &&
                (loopImages || currentIndex < count - 1);
        }
    }

    private void SetNavigationInteractable(bool interactable)
    {
        if (previousButton != null)
        {
            previousButton.interactable =
                interactable && downloadedTextures.Count > 1;
        }

        if (nextButton != null)
        {
            nextButton.interactable =
                interactable && downloadedTextures.Count > 1;
        }
    }

    /// <summary>
    /// 서버가 보낸 상대 URL을 완전한 HTTP URL로 변환합니다.
    ///
    /// 예:
    /// /reports/practice-001/summary.png
    /// ↓
    /// http://127.0.0.1:8000/reports/
    /// practice-001/summary.png
    /// </summary>
    private string ResolveAbsoluteImageUrl(
        string originalUrl)
    {
        if (string.IsNullOrWhiteSpace(originalUrl))
        {
            return null;
        }

        string trimmedUrl =
            originalUrl.Trim();

        // 이미 완전한 HTTP 주소라면 그대로 사용
        if (
            Uri.TryCreate(
                trimmedUrl,
                UriKind.Absolute,
                out Uri absoluteUri
            ) &&
            (
                absoluteUri.Scheme ==
                Uri.UriSchemeHttp ||

                absoluteUri.Scheme ==
                Uri.UriSchemeHttps
            )
        )
        {
            return absoluteUri.AbsoluteUri;
        }

        if (
            string.IsNullOrWhiteSpace(
                serverHttpBaseUrl
            )
        )
        {
            Debug.LogError(
                "[SessionFeedbackImageCarousel] " +
                "Server Http Base Url이 비어 있습니다.",
                this
            );

            return null;
        }

        string normalizedBaseUrl =
            NormalizeServerBaseUrl(
                serverHttpBaseUrl
            );

        if (
            !Uri.TryCreate(
                normalizedBaseUrl,
                UriKind.Absolute,
                out Uri baseUri
            )
        )
        {
            Debug.LogError(
                "[SessionFeedbackImageCarousel] " +
                "서버 주소 형식이 잘못되었습니다." +
                $"\nServer URL: {serverHttpBaseUrl}",
                this
            );

            return null;
        }

        try
        {
            /*
             * originalUrl이 /reports/... 형태이면
             * 서버의 root 경로와 결합됩니다.
             */
            Uri resolvedUri =
                new Uri(
                    baseUri,
                    trimmedUrl
                );

            return resolvedUri.AbsoluteUri;
        }
        catch (Exception exception)
        {
            Debug.LogError(
                "[SessionFeedbackImageCarousel] " +
                "이미지 URL 결합 실패" +
                $"\nBase URL: {normalizedBaseUrl}" +
                $"\nRelative URL: {trimmedUrl}" +
                $"\nError: {exception.Message}",
                this
            );

            return null;
        }
    }

    /// <summary>
    /// http://, https://뿐 아니라 WebSocket 주소가 입력돼도
    /// 이미지 다운로드용 HTTP 서버 주소로 변환합니다.
    /// </summary>
    private static string NormalizeServerBaseUrl(
        string inputUrl)
    {
        string trimmed =
            inputUrl.Trim();

        if (
            !Uri.TryCreate(
                trimmed,
                UriKind.Absolute,
                out Uri sourceUri
            )
        )
        {
            return trimmed;
        }

        UriBuilder builder =
            new UriBuilder(sourceUri);

        if (
            sourceUri.Scheme.Equals(
                "ws",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            builder.Scheme =
                Uri.UriSchemeHttp;
        }
        else if (
            sourceUri.Scheme.Equals(
                "wss",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            builder.Scheme =
                Uri.UriSchemeHttps;
        }

        /*
         * ws://127.0.0.1:8000/ws/unity/practice-001
         * 같은 주소가 들어와도
         * http://127.0.0.1:8000/
         * 형태만 사용합니다.
         */
        builder.Path = "/";
        builder.Query = string.Empty;
        builder.Fragment = string.Empty;

        return builder.Uri.AbsoluteUri;
    }

    /// <summary>
    /// 같은 파일명이 캐시되는 것을 방지하기 위해
    /// timestamp Query Parameter를 추가합니다.
    /// </summary>
    private string AddCacheBuster(
        string absoluteUrl)
    {
        if (
            !preventCaching ||
            string.IsNullOrWhiteSpace(absoluteUrl)
        )
        {
            return absoluteUrl;
        }

        string separator =
            absoluteUrl.Contains("?")
                ? "&"
                : "?";

        return
            absoluteUrl +
            separator +
            "timestamp=" +
            DateTimeOffset.UtcNow
                .ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// PNG 파일의 8바이트 Signature를 확인합니다.
    /// </summary>
    private static bool HasPngSignature(
        byte[] data)
    {
        if (
            data == null ||
            data.Length < 8
        )
        {
            return false;
        }

        return
            data[0] == 0x89 &&
            data[1] == 0x50 &&
            data[2] == 0x4E &&
            data[3] == 0x47 &&
            data[4] == 0x0D &&
            data[5] == 0x0A &&
            data[6] == 0x1A &&
            data[7] == 0x0A;
    }

    private void ShowError(string message)
    {
        if (panelRoot != null)
        {
            panelRoot.SetActive(true);
        }

        if (feedbackRawImage != null)
        {
            feedbackRawImage.enabled = false;
        }

        SetNavigationInteractable(false);
        SetStatus(message, true);

        Debug.LogError(
            $"[SessionFeedbackImageCarousel] {message}",
            this
        );
    }

    private void SetStatus(string message, bool visible)
    {
        if (statusText == null)
        {
            return;
        }

        statusText.text = message;
        statusText.gameObject.SetActive(visible);
    }

    private void ValidateReferences()
    {
        if (panelRoot == null)
        {
            Debug.LogError(
                "[SessionFeedbackImageCarousel] " +
                "Panel Root가 지정되지 않았습니다.",
                this
            );
        }

        if (feedbackRawImage == null)
        {
            Debug.LogError(
                "[SessionFeedbackImageCarousel] " +
                "Feedback Raw Image가 지정되지 않았습니다.",
                this
            );
        }

        if (previousButton == null)
        {
            Debug.LogWarning(
                "[SessionFeedbackImageCarousel] " +
                "Previous Button이 지정되지 않았습니다.",
                this
            );
        }

        if (nextButton == null)
        {
            Debug.LogWarning(
                "[SessionFeedbackImageCarousel] " +
                "Next Button이 지정되지 않았습니다.",
                this
            );
        }
    }

    private void OnDestroy()
    {
        if (previousButton != null)
        {
            previousButton.onClick.RemoveListener(
                ShowPreviousImage
            );
        }

        if (nextButton != null)
        {
            nextButton.onClick.RemoveListener(
                ShowNextImage
            );
        }

        if (closeButton != null)
        {
            closeButton.onClick.RemoveListener(Hide);
        }

        Clear();
    }

    [ContextMenu("Test Session Report PNG 5 Pages")]
    private void TestSessionReportPngPages()
    {
        ShowImages(
            new[]
            {
            "/reports/practice-001/summary.png",
            "/reports/practice-001/sensor.png",
            "/reports/practice-001/trial-1.png",
            "/reports/practice-001/trial-2.png",
            "/reports/practice-001/trial-3.png"
            }
        );
    }
}
