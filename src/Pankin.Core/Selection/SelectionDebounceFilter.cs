using System;
using System.Collections.Generic;

namespace Pankin.Core.Selection;

public enum SelectionTriggerType
{
    None = 0,
    MouseDrag = 1,
    DoubleClick = 2,
    Hotkey = 3
}

public sealed record SelectionTriggerDecision(
    SelectionTriggerType TriggerType,
    bool ShouldTrigger,
    double X,
    double Y);

/// <summary>
/// 纯逻辑状态机：负责鼠标抬起（拖拽划词）、双击选词与快捷键触发的防抖、去重与黑名单过滤。
/// 不依赖任何平台原生 API，100% 可进行单元测试。
/// </summary>
public sealed class SelectionDebounceFilter
{
    private long _lastClickDownMs;
    private long _lastClickUpMs;
    private double _lastClickX;
    private double _lastClickY;
    private int _clickCount;

    private long _lastTriggerMs;
    private string? _lastCapturedText;
    private long _lastCapturedTimeMs;

    public long DebounceWindowMs { get; set; } = 280;
    public long DoubleClickThresholdMs { get; set; } = 380;
    public double MaxDoubleClickDistance { get; set; } = 10.0;
    public long DuplicateSuppressWindowMs { get; set; } = 1500;

    public bool TriggerOnMouseUp { get; set; } = true;
    public bool TriggerOnDoubleClick { get; set; } = true;
    public int MinLength { get; set; } = 2;
    public int MaxLength { get; set; } = 2000;

    public void RecordMouseDown(int button, double x, double y, long timestampMs)
    {
        if (button != 1)
        {
            _clickCount = 0;
            return;
        }

        long elapsed = timestampMs - _lastClickDownMs;
        double dx = Math.Abs(x - _lastClickX);
        double dy = Math.Abs(y - _lastClickY);

        if (elapsed <= DoubleClickThresholdMs && dx <= MaxDoubleClickDistance && dy <= MaxDoubleClickDistance)
        {
            _clickCount++;
        }
        else
        {
            _clickCount = 1;
        }

        _lastClickDownMs = timestampMs;
        _lastClickX = x;
        _lastClickY = y;
    }

    public SelectionTriggerDecision RecordMouseUp(int button, double x, double y, long timestampMs, bool wasDragged)
    {
        if (button != 1)
        {
            return new SelectionTriggerDecision(SelectionTriggerType.None, false, x, y);
        }

        if (wasDragged)
        {
            _clickCount = 0;
            _lastClickUpMs = timestampMs;

            if (TriggerOnMouseUp && CanTrigger(timestampMs))
            {
                _lastTriggerMs = timestampMs;
                return new SelectionTriggerDecision(SelectionTriggerType.MouseDrag, true, x, y);
            }

            return new SelectionTriggerDecision(SelectionTriggerType.MouseDrag, false, x, y);
        }

        long elapsed = timestampMs - _lastClickUpMs;
        _lastClickUpMs = timestampMs;

        if (_clickCount >= 2 && elapsed <= DoubleClickThresholdMs)
        {
            _clickCount = 0;
            if (TriggerOnDoubleClick && CanTrigger(timestampMs))
            {
                _lastTriggerMs = timestampMs;
                return new SelectionTriggerDecision(SelectionTriggerType.DoubleClick, true, x, y);
            }

            return new SelectionTriggerDecision(SelectionTriggerType.DoubleClick, false, x, y);
        }

        return new SelectionTriggerDecision(SelectionTriggerType.None, false, x, y);
    }

    public SelectionTriggerDecision RecordHotkeyTrigger(double x, double y, long timestampMs)
    {
        if (CanTrigger(timestampMs))
        {
            _lastTriggerMs = timestampMs;
            return new SelectionTriggerDecision(SelectionTriggerType.Hotkey, true, x, y);
        }

        return new SelectionTriggerDecision(SelectionTriggerType.Hotkey, false, x, y);
    }

    public bool CanTrigger(long timestampMs)
    {
        return timestampMs - _lastTriggerMs >= DebounceWindowMs;
    }

    public bool IsDuplicate(string text, long timestampMs)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();

        if (string.Equals(_lastCapturedText, trimmed, StringComparison.Ordinal) &&
            timestampMs - _lastCapturedTimeMs < DuplicateSuppressWindowMs)
        {
            return true;
        }

        return false;
    }

    public void RecordCapture(string text, long timestampMs)
    {
        _lastCapturedText = text?.Trim();
        _lastCapturedTimeMs = timestampMs;
    }

    public bool IsValidLength(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var len = text.Trim().Length;
        if (len < MinLength) return false;
        if (MaxLength > 0 && len > MaxLength) return false;
        return true;
    }

    public static bool IsBlacklisted(string? appName, string? windowTitle, IEnumerable<string>? blacklist)
    {
        if (blacklist == null) return false;

        string cleanApp = (appName ?? string.Empty).Trim();
        string cleanTitle = (windowTitle ?? string.Empty).Trim();

        foreach (var item in blacklist)
        {
            if (string.IsNullOrWhiteSpace(item)) continue;
            var pattern = item.Trim();

            if (!string.IsNullOrEmpty(cleanApp) &&
                cleanApp.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(cleanTitle) &&
                cleanTitle.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public void Reset()
    {
        _lastClickDownMs = 0;
        _lastClickUpMs = 0;
        _clickCount = 0;
        _lastTriggerMs = 0;
        _lastCapturedText = null;
        _lastCapturedTimeMs = 0;
    }
}
