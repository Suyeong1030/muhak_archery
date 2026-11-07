using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// arc_unity.py가 UDP 5005로 보내는 shot JSON을 수신합니다.
/// 네트워크 스레드에서는 데이터를 큐에 넣기만 하고,
/// Unity 이벤트는 메인 스레드 Update에서 발생시킵니다.
/// </summary>
public class ArcUnityUdpReceiver : MonoBehaviour
{
    [Serializable]
    public class TrajectoryPoint
    {
        public float x;
        public float y;
        public float z;
    }

    [Serializable]
    public class ArcUnityShotPayload
    {
        public string type;
        public int shot_id;
        public float draw_pct;
        public float draw_cm;
        public float force_n;
        public float speed_ms;
        public float elev_deg;
        public float azim_deg;
        public float dist_m;
        public float lateral_m;
        public TrajectoryPoint[] traj;
    }

    [SerializeField] private int listenPort = 5005;

    public event Action<ArcUnityShotPayload> ShotReceived;

    private readonly ConcurrentQueue<ArcUnityShotPayload> pendingShots =
        new ConcurrentQueue<ArcUnityShotPayload>();

    private UdpClient client;
    private Thread receiveThread;
    private volatile bool running;

    private void Start()
    {
        try
        {
            client = new UdpClient(listenPort);
            running = true;
            receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
            receiveThread.Start();
            Debug.Log($"[ArcUnityUdpReceiver] UDP 수신 시작: 0.0.0.0:{listenPort}", this);
        }
        catch (Exception exception)
        {
            Debug.LogError($"[ArcUnityUdpReceiver] UDP 포트 열기 실패: {exception.Message}", this);
        }
    }

    private void ReceiveLoop()
    {
        IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);

        while (running && client != null)
        {
            try
            {
                byte[] bytes = client.Receive(ref remote);
                string json = Encoding.UTF8.GetString(bytes);
                ArcUnityShotPayload payload =
                    JsonUtility.FromJson<ArcUnityShotPayload>(json);

                if (payload != null && payload.type == "shot")
                    pendingShots.Enqueue(payload);
            }
            catch (SocketException)
            {
                if (running)
                    Debug.LogWarning("[ArcUnityUdpReceiver] UDP 소켓 수신 오류");
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception exception)
            {
                if (running)
                    Debug.LogWarning($"[ArcUnityUdpReceiver] 패킷 처리 실패: {exception.Message}");
            }
        }
    }

    private void Update()
    {
        while (pendingShots.TryDequeue(out ArcUnityShotPayload payload))
            ShotReceived?.Invoke(payload);
    }

    private void OnDestroy()
    {
        running = false;
        client?.Close();
        client = null;

        if (receiveThread != null && receiveThread.IsAlive)
            receiveThread.Join(100);
    }
}
