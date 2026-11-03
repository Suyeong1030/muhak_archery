using System;
using UnityEngine;

/// <summary>
/// ArtemisServerTest가 받은 서버 JSON 중
/// 실시간 feedback, 한 발 shot_report,
/// 여러 발 종합 session_report,
/// Session Feedback 이미지 목록을 구분하여 처리합니다.
///
/// 지원하는 이미지 구조:
/// 1. payload.image_urls[]
/// 2. payload.pages[].url
/// </summary>
public class ServerFeedbackReceiver : MonoBehaviour
{
    [Header("Server")]

    [SerializeField]
    private ArtemisServerTest artemisServer;

    [Header("Session Feedback Image")]

    [Tooltip(
        "서버에서 받은 Session Feedback 이미지 URL 배열을 " +
        "표시할 Carousel입니다."
    )]
    [SerializeField]
    private SessionFeedbackImageCarousel sessionImageCarousel;

    /// <summary>
    /// 실시간 feedback 또는 한 발 shot_report 수신 이벤트입니다.
    /// 매개변수 순서: message, feedbackType, code
    /// </summary>
    public event Action<string, string, string>
        FeedbackReceived;

    /// <summary>
    /// 여러 발 종합 session_report 수신 이벤트입니다.
    /// </summary>
    public event Action<SessionReportData>
        SessionReportReceived;

    /// <summary>
    /// 서버에서 Session Feedback 이미지 URL 배열을 받았을 때 발생합니다.
    /// </summary>
    public event Action<string[]>
        SessionImagesReceived;

    // =========================================================
    // 외부 공개 데이터
    // =========================================================

    [Serializable]
    public class SessionReportData
    {
        public string sessionId;
        public string code;
        public string message;

        public int reportVersion;
        public int shotCount;
        public int totalScore;
        public float averageScore;

        public string[] strengths;
        public string[] weaknesses;
        public string[] recommendations;

        public string[] imageUrls;
    }

    // =========================================================
    // 서버 JSON 구조
    // =========================================================

    [Serializable]
    private class ServerEnvelope
    {
        public string type;
        public FeedbackResponsePayload payload;
    }

    /// <summary>
    /// JsonUtility는 메시지 종류에 따라 Payload 클래스를
    /// 자동으로 선택하지 못하므로 필요한 필드를
    /// 한 클래스 안에 함께 선언합니다.
    /// </summary>
    [Serializable]
    private class FeedbackResponsePayload
    {
        public string session_id;
        public string state;

        // 일반 feedback 메시지
        public FeedbackPayload feedback;

        // shot_report, session_report 공통
        public string message;
        public string feedback_type;
        public string code;
        public string[] phases;

        // session_report 필드
        public int report_version;
        public int shot_count;
        public int total_score;
        public float average_score;

        public string[] strengths;
        public string[] weaknesses;
        public string[] recommendations;

        // 예전 서버 또는 별도 이미지 메시지 호환
        public string[] image_urls;

        // 현재 session_report.v1 구조
        public ReportPagePayload[] pages;
    }

    [Serializable]
    private class FeedbackPayload
    {
        public string type;
        public string message;
        public string code;
        public int cooldown_ms;
    }

    /// <summary>
    /// 서버의 session_report.payload.pages[] 요소입니다.
    /// </summary>
    [Serializable]
    private class ReportPagePayload
    {
        public int page_index;
        public string kind;
        public int trial_number;
        public string url;
        public string mime_type;
        public int width;
        public int height;
    }

    // =========================================================
    // Unity 생명주기
    // =========================================================

    private void Awake()
    {
        if (artemisServer == null)
        {
            artemisServer =
                FindFirstObjectByType<ArtemisServerTest>();
        }

        if (sessionImageCarousel == null)
        {
            sessionImageCarousel =
                FindFirstObjectByType<
                    SessionFeedbackImageCarousel
                >();
        }
    }

    private void OnEnable()
    {
        if (artemisServer != null)
        {
            artemisServer.RawMessageReceived +=
                HandleRawMessage;
        }
        else
        {
            Debug.LogWarning(
                "[ServerFeedbackReceiver] " +
                "ArtemisServerTest를 찾지 못했습니다.",
                this
            );
        }
    }

    private void OnDisable()
    {
        if (artemisServer != null)
        {
            artemisServer.RawMessageReceived -=
                HandleRawMessage;
        }
    }

    // =========================================================
    // 서버 JSON 처리
    // =========================================================

    /// <summary>
    /// ArtemisServerTest가 받은 원본 JSON을 처리합니다.
    /// </summary>
    public void HandleRawMessage(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            ServerEnvelope envelope =
                JsonUtility.FromJson<ServerEnvelope>(
                    json
                );

            if (
                envelope == null ||
                string.IsNullOrWhiteSpace(envelope.type)
            )
            {
                return;
            }

            string normalizedType =
                envelope.type
                    .Trim()
                    .ToLowerInvariant();

            switch (normalizedType)
            {
                case "feedback":
                    HandleFeedbackEnvelope(envelope);
                    break;

                case "shot_report":
                    HandleShotReportEnvelope(envelope);
                    break;

                case "session_report":
                case "final_report":
                case "complete_report":
                    HandleSessionReportEnvelope(envelope);
                    break;

                case "session_report_images":
                case "session_images":
                case "final_report_images":
                    HandleSessionImagesEnvelope(envelope);
                    break;

                // server_ack, sensor_update, bow_input 등
                default:
                    break;
            }
        }
        catch (Exception exception)
        {
            Debug.LogError(
                "[ServerFeedbackReceiver] " +
                "서버 JSON 처리 실패: " +
                exception.Message +
                "\nJSON: " +
                json,
                this
            );
        }
    }

    private void HandleFeedbackEnvelope(
        ServerEnvelope envelope)
    {
        if (envelope.payload == null)
        {
            return;
        }

        if (envelope.payload.feedback != null)
        {
            PublishFeedback(
                envelope.payload.feedback.message,
                envelope.payload.feedback.type,
                envelope.payload.feedback.code
            );

            return;
        }

        PublishFeedback(
            envelope.payload.message,
            envelope.payload.feedback_type,
            envelope.payload.code
        );
    }

    private void HandleShotReportEnvelope(
        ServerEnvelope envelope)
    {
        if (envelope.payload == null)
        {
            return;
        }

        string reportCode =
            string.IsNullOrWhiteSpace(
                envelope.payload.code
            )
                ? "SHOT_REPORT"
                : envelope.payload.code.Trim();

        PublishFeedback(
            envelope.payload.message,
            "report",
            reportCode
        );
    }
    

    private void HandleSessionReportEnvelope(
        ServerEnvelope envelope)
    {
        if (envelope.payload == null)
        {
            Debug.LogWarning(
                "[ServerFeedbackReceiver] " +
                "session_report에 payload가 없습니다.",
                this
            );

            return;
        }

        FeedbackResponsePayload payload =
            envelope.payload;

        string[] resolvedImageUrls =
            ExtractImageUrls(payload);

        bool hasMessage =
            !string.IsNullOrWhiteSpace(
                payload.message
            );

        bool hasImages =
            resolvedImageUrls != null &&
            resolvedImageUrls.Length > 0;

        if (!hasMessage && !hasImages)
        {
            Debug.LogWarning(
                "[ServerFeedbackReceiver] " +
                "session_report를 받았지만 " +
                "message, image_urls, pages가 모두 없습니다.",
                this
            );

            return;
        }

        SessionReportData report =
            new SessionReportData
            {
                sessionId =
                    payload.session_id,

                code =
                    string.IsNullOrWhiteSpace(
                        payload.code
                    )
                        ? "SESSION_REPORT"
                        : payload.code.Trim(),

                message =
                    hasMessage
                        ? payload.message.Trim()
                        : string.Empty,

                reportVersion =
                    payload.report_version,

                shotCount =
                    payload.shot_count,

                totalScore =
                    payload.total_score,

                averageScore =
                    payload.average_score,

                strengths =
                    payload.strengths,

                weaknesses =
                    payload.weaknesses,

                recommendations =
                    payload.recommendations,

                imageUrls =
                    resolvedImageUrls
            };

        Debug.Log(
            "[SESSION_REPORT]" +
            "\nSession ID: " +
            report.sessionId +
            "\nCode: " +
            report.code +
            "\nReport Version: " +
            report.reportVersion +
            "\nShot Count: " +
            report.shotCount +
            "\nTotal Score: " +
            report.totalScore +
            "\nAverage Score: " +
            report.averageScore.ToString("F1") +
            "\nImage Count: " +
            GetValidImageCount(report.imageUrls) +
            "\nMessage: " +
            report.message,
            this
        );

        SessionReportReceived?.Invoke(report);

        if (hasImages)
        {
            PublishSessionImages(
                resolvedImageUrls
            );
        }
    }

    private void HandleSessionImagesEnvelope(
        ServerEnvelope envelope)
    {
        if (envelope.payload == null)
        {
            Debug.LogWarning(
                "[ServerFeedbackReceiver] " +
                "이미지 메시지에 payload가 없습니다.",
                this
            );

            return;
        }

        string[] resolvedImageUrls =
            ExtractImageUrls(envelope.payload);

        PublishSessionImages(
            resolvedImageUrls
        );
    }

    // =========================================================
    // pages / image_urls 변환
    // =========================================================

    /// <summary>
    /// 기존 image_urls[] 또는 현재 pages[].url을
    /// 하나의 URL 배열로 변환합니다.
    /// </summary>
    private static string[] ExtractImageUrls(
        FeedbackResponsePayload payload)
    {
        if (payload == null)
        {
            return Array.Empty<string>();
        }

        // 기존 형식 우선 지원
        if (
            payload.image_urls != null &&
            payload.image_urls.Length > 0
        )
        {
            return FilterValidImageUrls(
                payload.image_urls
            );
        }

        // session_report.v1의 pages 형식
        if (
            payload.pages == null ||
            payload.pages.Length == 0
        )
        {
            return Array.Empty<string>();
        }

        ReportPagePayload[] sortedPages =
            new ReportPagePayload[
                payload.pages.Length
            ];

        Array.Copy(
            payload.pages,
            sortedPages,
            payload.pages.Length
        );

        Array.Sort(
            sortedPages,
            CompareReportPages
        );

        string[] pageUrls =
            new string[sortedPages.Length];

        for (
            int i = 0;
            i < sortedPages.Length;
            i++
        )
        {
            ReportPagePayload page =
                sortedPages[i];

            pageUrls[i] =
                page != null
                    ? page.url
                    : null;
        }

        return FilterValidImageUrls(
            pageUrls
        );
    }

    private static int CompareReportPages(
        ReportPagePayload first,
        ReportPagePayload second)
    {
        if (
            ReferenceEquals(first, second)
        )
        {
            return 0;
        }

        if (first == null)
        {
            return 1;
        }

        if (second == null)
        {
            return -1;
        }

        return first.page_index.CompareTo(
            second.page_index
        );
    }

    // =========================================================
    // 이미지 처리
    // =========================================================

    private void PublishSessionImages(
        string[] imageUrls)
    {
        string[] validUrls =
            FilterValidImageUrls(imageUrls);

        if (validUrls.Length == 0)
        {
            Debug.LogWarning(
                "[ServerFeedbackReceiver] " +
                "유효한 Session Feedback 이미지 URL이 없습니다.",
                this
            );

            return;
        }

        Debug.Log(
            "[SESSION_REPORT_IMAGES]" +
            "\nImage Count: " +
            validUrls.Length +
            "\nFirst URL: " +
            validUrls[0],
            this
        );

        SessionImagesReceived?.Invoke(
            validUrls
        );

        if (sessionImageCarousel == null)
        {
            sessionImageCarousel =
                FindFirstObjectByType<
                    SessionFeedbackImageCarousel
                >();
        }

        if (sessionImageCarousel == null)
        {
            Debug.LogError(
                "[ServerFeedbackReceiver] " +
                "SessionFeedbackImageCarousel을 찾지 못했습니다.",
                this
            );

            return;
        }

        sessionImageCarousel.ShowImages(
            validUrls
        );
    }

    private static string[] FilterValidImageUrls(
        string[] imageUrls)
    {
        if (
            imageUrls == null ||
            imageUrls.Length == 0
        )
        {
            return Array.Empty<string>();
        }

        int validCount = 0;

        for (
            int i = 0;
            i < imageUrls.Length;
            i++
        )
        {
            if (
                !string.IsNullOrWhiteSpace(
                    imageUrls[i]
                )
            )
            {
                validCount++;
            }
        }

        if (validCount == 0)
        {
            return Array.Empty<string>();
        }

        string[] validUrls =
            new string[validCount];

        int resultIndex = 0;

        for (
            int i = 0;
            i < imageUrls.Length;
            i++
        )
        {
            if (
                string.IsNullOrWhiteSpace(
                    imageUrls[i]
                )
            )
            {
                continue;
            }

            validUrls[resultIndex] =
                imageUrls[i].Trim();

            resultIndex++;
        }

        return validUrls;
    }

    private static int GetValidImageCount(
        string[] imageUrls)
    {
        if (imageUrls == null)
        {
            return 0;
        }

        int count = 0;

        foreach (
            string imageUrl in imageUrls
        )
        {
            if (
                !string.IsNullOrWhiteSpace(
                    imageUrl
                )
            )
            {
                count++;
            }
        }

        return count;
    }

    // =========================================================
    // 이벤트 발행
    // =========================================================

    public void InjectTestFeedback(
        string message,
        string feedbackType = "alert",
        string code = "UNITY_TEST")
    {
        PublishFeedback(
            message,
            feedbackType,
            code
        );
    }

    private void PublishFeedback(
        string message,
        string feedbackType,
        string code)
    {
        string safeType =
            string.IsNullOrWhiteSpace(
                feedbackType
            )
                ? "alert"
                : feedbackType
                    .Trim()
                    .ToLowerInvariant();

        if (safeType == "none")
        {
            return;
        }

        if (
            string.IsNullOrWhiteSpace(
                message
            )
        )
        {
            return;
        }

        string safeMessage =
            message.Trim();

        string safeCode =
            string.IsNullOrWhiteSpace(code)
                ? string.Empty
                : code.Trim();

        Debug.Log(
            "[SERVER_FEEDBACK]" +
            $"\nType: {safeType}" +
            $"\nCode: {safeCode}" +
            $"\nMessage: {safeMessage}",
            this
        );

        FeedbackReceived?.Invoke(
            safeMessage,
            safeType,
            safeCode
        );
    }

    // =========================================================
    // Inspector 테스트
    // =========================================================

    [ContextMenu("Test Feedback JSON")]
    private void TestFeedbackJson()
    {
        const string testJson =
            "{\"type\":\"feedback\"," +
            "\"payload\":{" +
            "\"session_id\":\"practice-001\"," +
            "\"state\":\"AIM\"," +
            "\"feedback\":{" +
            "\"type\":\"alert\"," +
            "\"message\":\"움직임을 줄이고 자세를 유지하세요.\"," +
            "\"code\":\"LOW_STABILITY\"," +
            "\"cooldown_ms\":2500" +
            "}}}";

        HandleRawMessage(testJson);
    }

    [ContextMenu("Test Shot Report JSON")]
    private void TestShotReportJson()
    {
        const string testJson =
            "{\"type\":\"shot_report\"," +
            "\"payload\":{" +
            "\"session_id\":\"practice-001\"," +
            "\"code\":\"SHOT_REPORT\"," +
            "\"message\":\"한 발 샷 리포트 테스트입니다.\"," +
            "\"phases\":[" +
            "\"DRAW\"," +
            "\"ANCHOR\"," +
            "\"AIM\"," +
            "\"RELEASE\"" +
            "]" +
            "}}";

        HandleRawMessage(testJson);
    }

    /// <summary>
    /// 현재 서버의 session_report.v1 pages[] 구조 테스트입니다.
    /// </summary>
    [ContextMenu("Test Session Report Pages JSON")]
    private void TestSessionReportPagesJson()
    {
        const string testJson =
            "{\"type\":\"session_report\"," +
            "\"payload\":{" +
            "\"report_version\":1," +
            "\"session_id\":\"practice-001\"," +
            "\"code\":\"SESSION_REPORT\"," +
            "\"message\":\"3발 종합 분석이 완료되었습니다.\"," +
            "\"shot_count\":3," +
            "\"total_score\":26," +
            "\"average_score\":8.67," +
            "\"strengths\":[" +
            "\"후반 trial의 수행 결과를 유지했습니다.\"" +
            "]," +
            "\"weaknesses\":[" +
            "\"Trial별 릴리즈 자세 편차를 확인하세요.\"" +
            "]," +
            "\"recommendations\":[" +
            "\"기준 자세와 관절 차이를 줄여보세요.\"" +
            "]," +
            "\"pages\":[" +
            "{" +
            "\"page_index\":0," +
            "\"kind\":\"summary\"," +
            "\"trial_number\":null," +
            "\"url\":\"/reports/practice-001/summary.png\"," +
            "\"mime_type\":\"image/png\"," +
            "\"width\":1600," +
            "\"height\":900" +
            "}," +
            "{" +
            "\"page_index\":1," +
            "\"kind\":\"sensor\"," +
            "\"trial_number\":null," +
            "\"url\":\"/reports/practice-001/sensor.png\"," +
            "\"mime_type\":\"image/png\"," +
            "\"width\":1600," +
            "\"height\":900" +
            "}," +
            "{" +
            "\"page_index\":2," +
            "\"kind\":\"trial\"," +
            "\"trial_number\":1," +
            "\"url\":\"/reports/practice-001/trial-1.png\"," +
            "\"mime_type\":\"image/png\"," +
            "\"width\":1600," +
            "\"height\":900" +
            "}," +
            "{" +
            "\"page_index\":3," +
            "\"kind\":\"trial\"," +
            "\"trial_number\":2," +
            "\"url\":\"/reports/practice-001/trial-2.png\"," +
            "\"mime_type\":\"image/png\"," +
            "\"width\":1600," +
            "\"height\":900" +
            "}," +
            "{" +
            "\"page_index\":4," +
            "\"kind\":\"trial\"," +
            "\"trial_number\":3," +
            "\"url\":\"/reports/practice-001/trial-3.png\"," +
            "\"mime_type\":\"image/png\"," +
            "\"width\":1600," +
            "\"height\":900" +
            "}" +
            "]" +
            "}}";

        HandleRawMessage(testJson);
    }

    /// <summary>
    /// 예전 image_urls[] 구조 호환 테스트입니다.
    /// </summary>
    [ContextMenu("Test Session Image URLs JSON")]
    private void TestSessionImageUrlsJson()
    {
        const string testJson =
            "{\"type\":\"session_report\"," +
            "\"payload\":{" +
            "\"session_id\":\"practice-001\"," +
            "\"code\":\"SESSION_REPORT\"," +
            "\"message\":\"기존 이미지 URL 방식 테스트입니다.\"," +
            "\"shot_count\":3," +
            "\"total_score\":24," +
            "\"average_score\":8.0," +
            "\"image_urls\":[" +
            "\"http://127.0.0.1:8000/reports/" +
            "practice-001/summary.png\"," +
            "\"http://127.0.0.1:8000/reports/" +
            "practice-001/sensor.png\"" +
            "]" +
            "}}";

        HandleRawMessage(testJson);
    }
}