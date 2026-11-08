using System;
using System.Collections.Generic;

namespace Pankin.Core.Rename;

/// <summary>
/// 纯 C# 实现的跨平台自然排序比较器（替代 Windows 专有的 shlwapi.dll!StrCmpLogicalW）。
/// 符合 Linux/Ubuntu 与 POSIX 环境无 Windows P/Invoke 规范。
/// </summary>
public sealed class NaturalStringComparer : IComparer<string?>
{
    public static readonly NaturalStringComparer OrdinalIgnoreCase = new(true);
    public static readonly NaturalStringComparer Ordinal = new(false);

    private readonly bool _ignoreCase;

    public NaturalStringComparer(bool ignoreCase = true)
    {
        _ignoreCase = ignoreCase;
    }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int ix = 0, iy = 0;
        int lenX = x.Length, lenY = y.Length;

        while (ix < lenX && iy < lenY)
        {
            char cx = x[ix];
            char cy = y[iy];

            if (char.IsDigit(cx) && char.IsDigit(cy))
            {
                int startX = ix;
                while (ix < lenX && char.IsDigit(x[ix])) ix++;
                var spanX = x.AsSpan(startX, ix - startX);

                int startY = iy;
                while (iy < lenY && char.IsDigit(y[iy])) iy++;
                var spanY = y.AsSpan(startY, iy - startY);

                var trimmedX = spanX.TrimStart('0');
                var trimmedY = spanY.TrimStart('0');

                if (trimmedX.Length != trimmedY.Length)
                {
                    return trimmedX.Length.CompareTo(trimmedY.Length);
                }

                int numCmp = trimmedX.SequenceCompareTo(trimmedY);
                if (numCmp != 0) return numCmp;

                // 若数值完全相同（例如 001 与 01），更短的排在前面或长度次序比较
                if (spanX.Length != spanY.Length)
                {
                    return spanX.Length.CompareTo(spanY.Length);
                }
            }
            else
            {
                int cmp = _ignoreCase
                    ? char.ToUpperInvariant(cx).CompareTo(char.ToUpperInvariant(cy))
                    : cx.CompareTo(cy);

                if (cmp != 0) return cmp;
                ix++;
                iy++;
            }
        }

        return (lenX - ix).CompareTo(lenY - iy);
    }
}
