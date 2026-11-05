using UnityEngine;
using System;
using System.IO.Ports;
using System.Globalization;

public class EMGManager : MonoBehaviour
{
    [Header("Serial Settings")]
    public string portName = "COM8";
    public int baudRate = 115200;

    [Header("Raw EMG")]
    public string currentEMGString = "";
    public float currentEMG = 0f;

    [Header("EMA Filter")]
    [Range(0.01f, 1f)]
    public float emaAlpha = 0.2f;

    public float filteredEMG = 0f;

    [Header("Debug")]
    public bool showDebugLog = true;

    private bool filterInitialized = false;

    private SerialPort serialPort;
    private string serialBuffer = "";

    void Start()
    {
        try
        {
            serialPort = new SerialPort(portName, baudRate);

            serialPort.ReadTimeout = 50;
            serialPort.DtrEnable = true;
            serialPort.RtsEnable = false;
            serialPort.Handshake = Handshake.None;

            serialPort.Open();
            serialPort.DiscardInBuffer();

            Debug.Log("Arduino 연결 성공: " + portName);
        }
        catch (Exception e)
        {
            Debug.LogError("Arduino 연결 실패: " + e.Message);
        }
    }

    void Update()
    {
        if (serialPort == null || !serialPort.IsOpen)
            return;

        try
        {
            string incoming = serialPort.ReadExisting();

            if (string.IsNullOrEmpty(incoming))
                return;

            serialBuffer += incoming;

            int newlineIndex;

            while ((newlineIndex = serialBuffer.IndexOf('\n')) >= 0)
            {
                string line =
                    serialBuffer.Substring(0, newlineIndex).Trim();

                serialBuffer =
                    serialBuffer.Substring(newlineIndex + 1);

                if (string.IsNullOrEmpty(line))
                    continue;

                // Arduino에서 받은 원본 문자열
                currentEMGString = line;

                // 문자열 -> float
                if (float.TryParse(
                    line,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out float value))
                {
                    // Raw EMG
                    currentEMG = value;

                    // EMA Filter
                    if (!filterInitialized)
                    {
                        filteredEMG = currentEMG;
                        filterInitialized = true;
                    }
                    else
                    {
                        filteredEMG =
                            emaAlpha * currentEMG +
                            (1f - emaAlpha) * filteredEMG;
                    }

                    // 실시간 Console 출력
                    if (showDebugLog)
                    {
                        Debug.Log(
                            "Raw: " + currentEMG.ToString("F3") +
                            " | Filtered: " + filteredEMG.ToString("F3")
                        );
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError("Serial Read Error: " + e.Message);
        }
    }

    // 필터 초기화
    public void ResetFilter()
    {
        filterInitialized = false;
        filteredEMG = 0f;

        Debug.Log("EMA Filter Reset");
    }

    void OnDestroy()
    {
        CloseSerial();
    }

    void OnApplicationQuit()
    {
        CloseSerial();
    }

    void CloseSerial()
    {
        if (serialPort != null && serialPort.IsOpen)
        {
            serialPort.Close();
            Debug.Log("Arduino Serial 연결 종료");
        }
    }
}