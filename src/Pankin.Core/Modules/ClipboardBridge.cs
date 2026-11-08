using Pankin.Core.Clipboard;

namespace Pankin.Core.Modules;

/// <summary>
/// 剪贴板历史稳定引用代理：当剪贴板模块动态重启或重构时，UI 与关联模块维持此桥接对象，避免悬挂对旧实例的引用
/// </summary>
public sealed class ClipboardBridge : IDisposable
{
    private ClipboardHistoryManager? _target;
    private readonly object _lock = new();

    public event EventHandler? Changed;

    public void Attach(ClipboardHistoryManager? value)
    {
        lock (_lock)
        {
            if (_target != null)
            {
                _target.HistoryChanged -= OnTargetHistoryChanged;
            }

            _target = value;

            if (_target != null)
            {
                _target.HistoryChanged += OnTargetHistoryChanged;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnTargetHistoryChanged(object? sender, EventArgs e)
    {
        Changed?.Invoke(this, e);
    }

    public Task<IReadOnlyList<ClipboardEntry>> SnapshotAsync(
        int limit = 100,
        string? search = null,
        ClipKind? kind = null)
    {
        lock (_lock)
        {
            if (_target != null)
            {
                return _target.SnapshotAsync(limit, search, kind);
            }
        }

        return Task.FromResult<IReadOnlyList<ClipboardEntry>>(Array.Empty<ClipboardEntry>());
    }

    public Task SetPinnedAsync(string id, bool pinned)
    {
        lock (_lock)
        {
            if (_target != null)
            {
                return _target.SetPinnedAsync(id, pinned);
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id)
    {
        lock (_lock)
        {
            if (_target != null)
            {
                return _target.DeleteAsync(id);
            }
        }

        return Task.CompletedTask;
    }

    public Task ClearUnpinnedAsync()
    {
        lock (_lock)
        {
            if (_target != null)
            {
                return _target.ClearUnpinnedAsync();
            }
        }

        return Task.CompletedTask;
    }

    public Task PasteAsync(ClipboardEntry entry, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_target != null)
            {
                return _target.PasteAsync(entry, ct);
            }
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Attach(null);
    }
}
