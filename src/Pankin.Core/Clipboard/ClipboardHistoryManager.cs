using Pankin.Core.Abstractions;

namespace Pankin.Core.Clipboard;

public sealed class ClipboardHistoryManager : IDisposable
{
    private readonly IClipboardService _clipboardService;
    private readonly ClipboardStorage _storage;
    private readonly int _maxHistoryLimit;
    private bool _isDisposed;

    public event EventHandler? HistoryChanged;

    public ClipboardStorage Storage => _storage;

    public ClipboardHistoryManager(
        IClipboardService clipboardService,
        ClipboardStorage? storage = null,
        int maxHistoryLimit = 500)
    {
        _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
        _storage = storage ?? new ClipboardStorage();
        _maxHistoryLimit = maxHistoryLimit;

        _clipboardService.ClipboardTextChanged += OnClipboardTextChanged;
    }

    private void OnClipboardTextChanged(object? sender, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            var kind = ClipboardClassifier.ClassifyText(text);
            var fingerprint = ClipboardClassifier.ComputeFingerprint(text);

            var entry = new ClipboardEntry(
                Id: Guid.NewGuid().ToString("N"),
                Kind: kind,
                Text: text,
                Files: null,
                ContentPath: null,
                ImageWidth: null,
                ImageHeight: null,
                Source: "System",
                CopiedAt: DateTimeOffset.UtcNow,
                Pinned: false,
                Fingerprint: fingerprint);

            _storage.Upsert(entry);
            _storage.TrimHistory(_maxHistoryLimit);

            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // 防止监听回调中的异常影响上层
        }
    }

    public Task<IReadOnlyList<ClipboardEntry>> SnapshotAsync(
        int limit = 100,
        string? search = null,
        ClipKind? kind = null)
    {
        return Task.Run<IReadOnlyList<ClipboardEntry>>(() =>
            _storage.QueryEntries(limit: limit, search: search, kind: kind));
    }

    public Task SetPinnedAsync(string id, bool pinned)
    {
        return Task.Run(() =>
        {
            if (_storage.SetPinned(id, pinned))
            {
                HistoryChanged?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    public Task DeleteAsync(string id)
    {
        return Task.Run(() =>
        {
            if (_storage.Delete(id))
            {
                HistoryChanged?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    public Task ClearUnpinnedAsync()
    {
        return Task.Run(() =>
        {
            int deleted = _storage.ClearUnpinned();
            if (deleted > 0)
            {
                HistoryChanged?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    public async Task PasteAsync(ClipboardEntry entry, CancellationToken ct = default)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));

        if (!string.IsNullOrEmpty(entry.Text))
        {
            await _clipboardService.SetTextAsync(entry.Text, ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _clipboardService.ClipboardTextChanged -= OnClipboardTextChanged;
        _storage.Dispose();
    }
}
