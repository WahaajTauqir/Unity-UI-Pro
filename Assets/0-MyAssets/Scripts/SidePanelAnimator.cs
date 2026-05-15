using PrimeTween;
using UnityEngine;

/// <summary>
/// Slides a left-aligned side panel open/closed by tweening RectTransform anchors with PrimeTween.
/// Keep anchorMin.x at 0 so the panel stays pinned to the left edge while anchorMax.x drives width.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(RectTransform))]
public sealed class SidePanelAnimator : MonoBehaviour
{
    [Header("Shown (open)")]
    [SerializeField] Vector2 shownAnchorMin = new(0f, 0f);
    [SerializeField] Vector2 shownAnchorMax = new(0.33333334f, 1f);

    [Header("Hidden (closed)")]
    [SerializeField] Vector2 hiddenAnchorMin = new(0f, 0f);
    [SerializeField] Vector2 hiddenAnchorMax = new(0f, 1f);

    [Header("Animation")]
    [SerializeField] float duration = 0.35f;
    [SerializeField] Ease showEase = Ease.OutCubic;
    [SerializeField] Ease hideEase = Ease.InCubic;
    [SerializeField] bool startOpen = true;
    [SerializeField] bool disableInteractionWhenHidden = true;

    RectTransform panel;
    CanvasGroup canvasGroup;
    Sequence sequence;

    public bool IsOpen { get; private set; }

    void Awake()
    {
        panel = (RectTransform)transform;
        canvasGroup = GetComponent<CanvasGroup>();
    }

    void Start()
    {
        ApplyAnchors(startOpen);
    }

    void OnDisable()
    {
        if (sequence.isAlive)
            sequence.Stop();
    }

    public void Show() => SetOpen(true);

    public void Hide() => SetOpen(false);

    public void Toggle() => SetOpen(!IsOpen);

    public void SetOpen(bool open)
    {
        if (IsOpen == open && !sequence.isAlive)
            return;

        if (sequence.isAlive)
            sequence.Stop();

        IsOpen = open;

        if (!open && disableInteractionWhenHidden && canvasGroup != null)
        {
            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;
        }

        var endMin = open ? shownAnchorMin : hiddenAnchorMin;
        var endMax = open ? shownAnchorMax : hiddenAnchorMax;
        var ease = open ? showEase : hideEase;

        sequence = Sequence.Create()
            .Group(Tween.UIAnchorMin(panel, endMin, duration, ease))
            .Group(Tween.UIAnchorMax(panel, endMax, duration, ease))
            .OnComplete(OnTweenComplete);
    }

    void OnTweenComplete()
    {
        if (disableInteractionWhenHidden && canvasGroup != null)
        {
            canvasGroup.interactable = IsOpen;
            canvasGroup.blocksRaycasts = IsOpen;
        }
    }

    void ApplyAnchors(bool open)
    {
        IsOpen = open;
        panel.anchorMin = open ? shownAnchorMin : hiddenAnchorMin;
        panel.anchorMax = open ? shownAnchorMax : hiddenAnchorMax;
        panel.anchoredPosition = Vector2.zero;
        panel.sizeDelta = Vector2.zero;

        if (disableInteractionWhenHidden && canvasGroup != null)
        {
            canvasGroup.interactable = open;
            canvasGroup.blocksRaycasts = open;
        }
    }
}
