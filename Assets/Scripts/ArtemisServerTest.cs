using System;
using System.Text;
using System.Threading;
using NativeWebSocket;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Unity와 FastAPI 서버의 WebSocket 통신을 관리합니다.
///
/// unity_trigger:
/// - MIDPOINT_GRABBED
/// - ARROW_RELEASED
/// - SCORE_UPDATED
///
/// unity_frame:
/// - DRAW
/// - ANCHOR
/// - AIM
/// - RELEASE
///
/// 서버의 자세 피드백과 LLM 샷 리포트는
/// unity_frame 메시지를 기준으로 생성됩니다.
/// </summary>
public class ArtemisServerTest : MonoBehaviour
{
    [Header("Server Settings")]
    [SerializeField]
    private string serverUrl =
        "ws://127.0.0.1:8000/ws/unity/practice-001";

    [SerializeField]
    private string sessionId =
        "practice-001";

    [Header("Session Reset")]

    [Tooltip(
        "Unity Play 시작 후 서버 연결이 완료되면 " +
        "해당 session_id의 이전 Trial을 초기화합니다."
    )]
    [SerializeField]
    private bool resetSessionOnConnect = true;

    [Header("Debug")]
    [SerializeField]
    private bool showReceivedMessages = false;

    private WebSocket websocket;

    /*
     * DRAW, ANCHOR, AIM, RELEASE 메시지가
     * 순서대로 전송되도록 제한합니다.
     */
    private readonly SemaphoreSlim sendLock =
        new SemaphoreSlim(1, 1);

    /// <summary>
    /// 서버에서 수신한 원본 JSON을
    /// 다른 Receiver 스크립트에 전달합니다.
    /// </summary>
    public event Action<string> RawMessageReceived;

    public string SessionId => sessionId;

    public bool IsConnected =>
        websocket != null &&
        websocket.State == WebSocketState.Open;

    private async void Start()
    {
        Debug.Log(
            "[Artemis][CONNECTING] 서버 연결 시도" +
            $"\nURL: {serverUrl}" +
            $"\nSession: {sessionId}",
            this
        );

        websocket =
            new WebSocket(serverUrl);

        websocket.OnOpen += () =>
        {
            Debug.Log(
                "[Artemis][CONNECTED] WebSocket 연결 성공" +
                $"\nURL: {serverUrl}" +
                $"\nSession: {sessionId}" +
                "\n다음 단계: SESSION_RESET 승인 대기",
                this
            );

            if (resetSessionOnConnect)
            {
                SendSessionReset();
            }
        };

        websocket.OnError += error =>
        {
            Debug.LogError(
                "[Artemis] WebSocket 오류: " +
                error,
                this
            );
        };

        websocket.OnClose += closeCode =>
        {
            Debug.LogWarning(
                "[Artemis] 서버 연결 종료: " +
                closeCode,
                this
            );
        };

        websocket.OnMessage += bytes =>
        {
            string message =
                Encoding.UTF8.GetString(bytes);

            bool isFrequentPoseUpdate =
                message.Contains("\"type\":\"pose_draw_update\"") &&
                message.Contains("\"action\":\"update\"");

            bool isSensorUpdate =
                message.Contains("\"type\":\"sensor_update\"");

            // Sensor streams can arrive several times per second. Logging each
            // packet causes allocations and console I/O on the Unity main
            // thread, which is especially costly on a standalone Quest build.
            if (
                showReceivedMessages &&
                !isFrequentPoseUpdate &&
                !isSensorUpdate
            )
            {
                Debug.Log(
                    "[Artemis] 서버 메시지 수신" +
                    $"\n{message}",
                    this
                );
            }

            if (
                message.Contains("\"type\":\"server_ack\"") &&
                message.Contains("\"trigger_name\":\"SESSION_RESET\"")
            )
            {
                Debug.Log(
                    "[Artemis][READY] 서버 연결 및 세션 준비 완료" +
                    $"\nSession: {sessionId}" +
                    "\nFlutter 트라이얼 시작을 기다립니다.",
                    this
                );
            }

            RawMessageReceived?.Invoke(message);
        };

        try
        {
            await websocket.Connect();
        }
        catch (Exception exception)
        {
            Debug.LogError(
                "[Artemis] 서버 연결 예외: " +
                exception.Message,
                this
            );
        }
    }

    private void Update()
    {
#if !UNITY_WEBGL || UNITY_EDITOR
        websocket?.DispatchMessageQueue();
#endif

        /*
         * 키보드 T를 누르면 연결 테스트 Trigger를 보냅니다.
         */
        if (
            Keyboard.current != null &&
            Keyboard.current.tKey.wasPressedThisFrame
        )
        {
            SendTestTrigger();
        }
    }

    /// <summary>
    /// 키보드 연결 테스트용 Trigger입니다.
    /// </summary>
    public void SendTestTrigger()
    {
        SendTriggerWithPayload(
            "TEST_TRIGGER",
            "{}"
        );
    }

    /// <summary>
    /// Unity Play가 새로 시작됐음을 서버에 알립니다.
    ///
    /// 서버는 이 Trigger를 받으면:
    /// - 진행 중 Shot
    /// - 완료된 Trial
    /// - 센서 Buffer
    /// - 대기 중 Report Task
    /// 를 초기화합니다.
    /// </summary>
    public void SendSessionReset()
    {
        SendTriggerWithPayload(
            "SESSION_RESET",
            "{}"
        );

        Debug.Log(
            "[Artemis] SESSION_RESET 전송 요청" +
            $"\nSession: {sessionId}",
            this
        );
    }

    /// <summary>
    /// 시위를 잡았다는 사건을 전송합니다.
    /// 이 메시지만으로는 LLM 샷 누적이 시작되지 않습니다.
    /// </summary>
    public void SendMidpointGrabbed()
    {
        GrabStatePayload payload =
            new GrabStatePayload
            {
                grabbed = true
            };

        SendPayloadTrigger(
            "MIDPOINT_GRABBED",
            payload
        );
    }

    /// <summary>
    /// 시위를 놓았다는 사건을 전송합니다.
    /// 이 메시지만으로는 LLM 리포트가 생성되지 않습니다.
    /// </summary>
    public void SendArrowReleased()
    {
        ReleaseStatePayload payload =
            new ReleaseStatePayload
            {
                released = true
            };

        SendPayloadTrigger(
            "ARROW_RELEASED",
            payload
        );
    }

    /// <summary>
    /// 현재 점수, 누적 점수, Shot Count를 전송합니다.
    /// </summary>
    public void SendScoreUpdated(
        int currentScore,
        int totalScore,
        int shotCount)
    {
        ScoreUpdatedPayload payload =
            new ScoreUpdatedPayload
            {
                current_score = currentScore,
                total_score = totalScore,
                shot_count = shotCount
            };

        SendPayloadTrigger(
            "SCORE_UPDATED",
            payload
        );
    }

    /// <summary>
    /// 서버의 FeedbackEngine과 ShotAccumulator가 처리하는
    /// unity_frame 메시지를 전송합니다.
    ///
    /// 사용 예:
    /// SendUnityFrame("DRAW");
    /// SendUnityFrame("ANCHOR");
    /// SendUnityFrame("AIM");
    /// SendUnityFrame("RELEASE");
    /// </summary>
    public void SendUnityFrame(string state)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            Debug.LogWarning(
                "[Artemis] Unity Frame state가 비어 있습니다.",
                this
            );

            return;
        }

        string normalizedState =
            state.Trim().ToUpperInvariant();

        if (!IsSupportedArcheryState(normalizedState))
        {
            Debug.LogWarning(
                "[Artemis] 지원하지 않는 state: " +
                normalizedState,
                this
            );

            return;
        }

        string json =
            "{"
            + "\"type\":\"unity_frame\","
            + "\"payload\":{"
            + "\"session_id\":\""
            + EscapeJson(sessionId)
            + "\","
            + "\"state\":\""
            + EscapeJson(normalizedState)
            + "\""
            + "}"
            + "}";

        SendJson(
            json,
            "UNITY_FRAME " + normalizedState
        );
    }

    private void SendPayloadTrigger<T>(
        string triggerName,
        T payload)
    {
        string payloadJson =
            JsonUtility.ToJson(payload);

        SendTriggerWithPayload(
            triggerName,
            payloadJson
        );
    }

    private void SendTriggerWithPayload(
        string triggerName,
        string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(triggerName))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            payloadJson = "{}";
        }

        string json =
            "{"
            + "\"type\":\"unity_trigger\","
            + "\"payload\":{"
            + "\"session_id\":\""
            + EscapeJson(sessionId)
            + "\","
            + "\"name\":\""
            + EscapeJson(triggerName)
            + "\","
            + "\"payload\":"
            + payloadJson
            + "}"
            + "}";

        SendJson(
            json,
            "TRIGGER " + triggerName
        );
    }

    /// <summary>
    /// WebSocket 메시지를 순서대로 전송합니다.
    /// </summary>
    private async void SendJson(
        string json,
        string logLabel)
    {
        if (!CanSend())
        {
            return;
        }

        await sendLock.WaitAsync();

        try
        {
            if (!CanSend())
            {
                return;
            }

            await websocket.SendText(json);

            Debug.Log(
                "[Artemis] 전송 완료: " +
                logLabel,
                this
            );
        }
        catch (Exception exception)
        {
            Debug.LogError(
                "[Artemis] 전송 실패 (" +
                logLabel +
                "): " +
                exception.Message,
                this
            );
        }
        finally
        {
            sendLock.Release();
        }
    }

    private bool CanSend()
    {
        if (!IsConnected)
        {
            Debug.LogWarning(
                "[Artemis] 서버가 연결된 상태가 아닙니다.",
                this
            );

            return false;
        }

        return true;
    }

    private static bool IsSupportedArcheryState(
        string state)
    {
        return
            state == "SETUP" ||
            state == "DRAW" ||
            state == "ANCHOR" ||
            state == "AIM" ||
            state == "RELEASE" ||
            state == "FOLLOW_THROUGH" ||
            state == "RESULT";
    }

    private static string EscapeJson(string value)
    {
        if (value == null)
        {
            return string.Empty;
        }

        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"");
    }

    private async void OnApplicationQuit()
    {
        if (websocket == null)
        {
            return;
        }

        if (
            websocket.State == WebSocketState.Open ||
            websocket.State == WebSocketState.Closing
        )
        {
            try
            {
                await websocket.Close();
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "[Artemis] WebSocket 종료 실패: " +
                    exception.Message,
                    this
                );
            }
        }
    }

    [Serializable]
    private class GrabStatePayload
    {
        public bool grabbed;
    }

    [Serializable]
    private class ReleaseStatePayload
    {
        public bool released;
    }

    [Serializable]
    private class ScoreUpdatedPayload
    {
        public int current_score;
        public int total_score;
        public int shot_count;
    }
}
