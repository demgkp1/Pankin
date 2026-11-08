using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pankin.Core.Selection;

public sealed record ClipboardSnapshot(string? Text, DateTime CapturedAt);

/// <summary>
/// 跨平台选区读取抽象接口
/// </summary>
public interface ISelectionReader
{
    Task<SelectionContext> ReadSelectionAsync(double mouseX, double mouseY, CancellationToken ct = default);
}

/// <summary>
/// 超级面板非 AI 动作抽象接口
/// </summary>
public interface ISelectionActions
{
    Task CopyAsync(string text, CancellationToken ct = default);
    Task SearchWebAsync(string query, CancellationToken ct = default);
    Task OpenUrlAsync(string url, CancellationToken ct = default);
    Task OpenFileOrFolderAsync(string path, CancellationToken ct = default);
    Task<bool> ReplaceSelectionAsync(string text, CancellationToken ct = default);
}
