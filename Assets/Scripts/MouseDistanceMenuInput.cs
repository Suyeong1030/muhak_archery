using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Provides a reliable mouse fallback for the world-space distance menu.
/// XR UI interaction remains handled by XRUIInputModule.
/// </summary>
public class MouseDistanceMenuInput : MonoBehaviour
{
    [Tooltip("Extra clickable area around each button, in screen pixels.")]
    [Min(0f)]
    [SerializeField]
    private float hitPaddingPixels = 18f;

    private Button[] buttons = Array.Empty<Button>();
    private Button hoveredButton;
    private readonly Vector3[] worldCorners = new Vector3[4];

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void EnsureDistanceMenuHasMouseInput()
    {
        GameObject menu = GameObject.Find("StartMenu");
        if (menu != null && menu.GetComponent<MouseDistanceMenuInput>() == null)
        {
            menu.AddComponent<MouseDistanceMenuInput>();
        }
    }

    private void OnEnable()
    {
        buttons = GetComponentsInChildren<Button>(true);
    }

    private void OnDisable()
    {
        SetHoveredButton(null);
    }

    private void Update()
    {
        Mouse mouse = Mouse.current;
        if (mouse == null)
        {
            SetHoveredButton(null);
            return;
        }

        Vector2 mousePosition = mouse.position.ReadValue();
        Button nextHoveredButton = null;

        foreach (Button button in buttons)
        {
            if (button == null ||
                !button.IsActive() ||
                !button.IsInteractable())
            {
                continue;
            }

            if (ContainsScreenPoint(button, mousePosition))
            {
                nextHoveredButton = button;
                break;
            }
        }

        SetHoveredButton(nextHoveredButton);

        if (nextHoveredButton != null &&
            mouse.leftButton.wasPressedThisFrame)
        {
            nextHoveredButton.onClick.Invoke();
        }
    }

    private bool ContainsScreenPoint(Button button, Vector2 mousePosition)
    {
        RectTransform rectTransform = button.transform as RectTransform;
        if (rectTransform == null)
        {
            return false;
        }

        Canvas canvas = button.GetComponentInParent<Canvas>();
        Camera eventCamera = null;

        if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
        {
            eventCamera = canvas.worldCamera != null
                ? canvas.worldCamera
                : Camera.main;
        }

        rectTransform.GetWorldCorners(worldCorners);

        Vector2 minimum = RectTransformUtility.WorldToScreenPoint(
            eventCamera,
            worldCorners[0]
        );
        Vector2 maximum = minimum;

        for (int index = 1; index < worldCorners.Length; index++)
        {
            Vector2 point = RectTransformUtility.WorldToScreenPoint(
                eventCamera,
                worldCorners[index]
            );
            minimum = Vector2.Min(minimum, point);
            maximum = Vector2.Max(maximum, point);
        }

        Rect expandedRect = Rect.MinMaxRect(
            minimum.x - hitPaddingPixels,
            minimum.y - hitPaddingPixels,
            maximum.x + hitPaddingPixels,
            maximum.y + hitPaddingPixels
        );

        return expandedRect.Contains(mousePosition);
    }

    private void SetHoveredButton(Button nextButton)
    {
        if (hoveredButton == nextButton)
        {
            return;
        }

        EventSystem eventSystem = EventSystem.current;
        if (eventSystem != null)
        {
            PointerEventData pointerData = new PointerEventData(eventSystem);

            if (hoveredButton != null)
            {
                hoveredButton.OnPointerExit(pointerData);
            }

            if (nextButton != null)
            {
                nextButton.OnPointerEnter(pointerData);
            }
        }

        hoveredButton = nextButton;
    }
}
