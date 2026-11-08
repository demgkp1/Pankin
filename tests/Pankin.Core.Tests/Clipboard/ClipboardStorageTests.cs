using Microsoft.Data.Sqlite;
using Pankin.Core.Clipboard;
using Xunit;

namespace Pankin.Core.Tests.Clipboard;

public class ClipboardStorageTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly ClipboardStorage _storage;

    public ClipboardStorageTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pankin_test_clip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "clipboard.db");
        _storage = new ClipboardStorage(_dbPath);
    }

    public void Dispose()
    {
        _storage.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // 忽略清理临时目录失败
        }
    }

    private static ClipboardEntry CreateEntry(
        string id,
        string text,
        ClipKind kind = ClipKind.Text,
        bool pinned = false,
        string source = "test_app",
        DateTimeOffset? copiedAt = null)
    {
        return new ClipboardEntry(
            Id: id,
            Kind: kind,
            Text: text,
            Files: null,
            ContentPath: null,
            ImageWidth: null,
            ImageHeight: null,
            Source: source,
            CopiedAt: copiedAt ?? DateTimeOffset.UtcNow,
            Pinned: pinned,
            Fingerprint: ClipboardClassifier.ComputeFingerprint(text));
    }

    [Fact]
    public void Upsert_NewEntry_SavesAndRetrievesEntry()
    {
        var entry = CreateEntry("entry-1", "Hello World");

        var inserted = _storage.Upsert(entry);
        Assert.NotNull(inserted);
        Assert.Equal("entry-1", inserted.Id);
        Assert.Equal("Hello World", inserted.Text);
        Assert.Equal(1, _storage.GetTotalCount());

        var list = _storage.QueryEntries();
        Assert.Single(list);
        Assert.Equal("entry-1", list[0].Id);
        Assert.Equal("Hello World", list[0].Text);
    }

    [Fact]
    public void Upsert_DuplicateFingerprint_DeduplicatesAndUpdatesTimestamp()
    {
        var t1 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var t2 = DateTimeOffset.UtcNow;

        var entry1 = CreateEntry("entry-1", "Duplicate Text", copiedAt: t1, source: "App1");
        _storage.Upsert(entry1);
        Assert.Equal(1, _storage.GetTotalCount());

        // 相同指纹（相同文本），不同 ID，最新时间
        var entry2 = CreateEntry("entry-2", "Duplicate Text", copiedAt: t2, source: "App2");
        var updated = _storage.Upsert(entry2);

        // 去重后总数仍然为 1
        Assert.Equal(1, _storage.GetTotalCount());
        Assert.Equal("entry-1", updated.Id); // 保留原 ID
        Assert.Equal("App2", updated.Source);
        Assert.Equal(t2.ToUnixTimeMilliseconds(), updated.CopiedAt.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void QueryEntries_SearchTextAndSource_ReturnsMatchingEntries()
    {
        _storage.Upsert(CreateEntry("1", "Special keyword inside text", source: "gedit"));
        _storage.Upsert(CreateEntry("2", "Another normal text", source: "gnome-terminal"));
        _storage.Upsert(CreateEntry("3", "Completely unrelated", source: "firefox"));

        var searchByText = _storage.QueryEntries(search: "keyword");
        Assert.Single(searchByText);
        Assert.Equal("1", searchByText[0].Id);

        var searchBySource = _storage.QueryEntries(search: "terminal");
        Assert.Single(searchBySource);
        Assert.Equal("2", searchBySource[0].Id);
    }

    [Fact]
    public void QueryEntries_FilterByKind_ReturnsMatchingKindOnly()
    {
        _storage.Upsert(CreateEntry("1", "plain text", ClipKind.Text));
        _storage.Upsert(CreateEntry("2", "https://example.com", ClipKind.Link));
        _storage.Upsert(CreateEntry("3", "public class Foo {}", ClipKind.Code));

        var links = _storage.QueryEntries(kind: ClipKind.Link);
        Assert.Single(links);
        Assert.Equal("2", links[0].Id);

        var codes = _storage.QueryEntries(kind: ClipKind.Code);
        Assert.Single(codes);
        Assert.Equal("3", codes[0].Id);
    }

    [Fact]
    public void QueryEntries_FilterByPinned_ReturnsOnlyPinnedOrUnpinned()
    {
        _storage.Upsert(CreateEntry("1", "Unpinned 1", pinned: false));
        _storage.Upsert(CreateEntry("2", "Pinned 1", pinned: true));
        _storage.Upsert(CreateEntry("3", "Unpinned 2", pinned: false));

        var pinned = _storage.QueryEntries(pinned: true);
        Assert.Single(pinned);
        Assert.Equal("2", pinned[0].Id);

        var unpinned = _storage.QueryEntries(pinned: false);
        Assert.Equal(2, unpinned.Count);
    }

    [Fact]
    public void QueryEntries_Pagination_RespectsLimitAndOffset()
    {
        var baseTime = DateTimeOffset.UtcNow;
        for (int i = 0; i < 10; i++)
        {
            _storage.Upsert(CreateEntry($"id-{i}", $"Text {i}", copiedAt: baseTime.AddSeconds(i)));
        }

        var page1 = _storage.QueryEntries(limit: 3, offset: 0);
        Assert.Equal(3, page1.Count);
        Assert.Equal("id-9", page1[0].Id); // 最新优先

        var page2 = _storage.QueryEntries(limit: 3, offset: 3);
        Assert.Equal(3, page2.Count);
        Assert.Equal("id-6", page2[0].Id);
    }

    [Fact]
    public void SetPinned_TogglesPinnedStatus()
    {
        _storage.Upsert(CreateEntry("entry-pin", "Text to pin", pinned: false));

        bool pinSuccess = _storage.SetPinned("entry-pin", true);
        Assert.True(pinSuccess);

        var entries = _storage.QueryEntries();
        Assert.True(entries[0].Pinned);

        bool unpinSuccess = _storage.SetPinned("entry-pin", false);
        Assert.True(unpinSuccess);

        entries = _storage.QueryEntries();
        Assert.False(entries[0].Pinned);

        // 不存在的 ID
        Assert.False(_storage.SetPinned("non-existent", true));
    }

    [Fact]
    public void Delete_RemovesTargetEntry()
    {
        _storage.Upsert(CreateEntry("entry-del", "Delete me"));
        Assert.Equal(1, _storage.GetTotalCount());

        bool deleted = _storage.Delete("entry-del");
        Assert.True(deleted);
        Assert.Equal(0, _storage.GetTotalCount());

        Assert.False(_storage.Delete("non-existent"));
    }

    [Fact]
    public void ClearUnpinned_RemovesOnlyUnpinnedEntries()
    {
        _storage.Upsert(CreateEntry("unpinned-1", "Unpinned 1", pinned: false));
        _storage.Upsert(CreateEntry("pinned-1", "Pinned 1", pinned: true));
        _storage.Upsert(CreateEntry("unpinned-2", "Unpinned 2", pinned: false));

        int cleared = _storage.ClearUnpinned();
        Assert.Equal(2, cleared);
        Assert.Equal(1, _storage.GetTotalCount());

        var remaining = _storage.QueryEntries();
        Assert.Single(remaining);
        Assert.Equal("pinned-1", remaining[0].Id);
    }

    [Fact]
    public void TrimHistory_PreservesPinnedAndTrimsOldestUnpinned()
    {
        var now = DateTimeOffset.UtcNow;
        _storage.Upsert(CreateEntry("pinned-1", "Pinned entry", pinned: true, copiedAt: now.AddHours(-10)));

        // 插入 10 条未固定条目
        for (int i = 0; i < 10; i++)
        {
            _storage.Upsert(CreateEntry($"unpinned-{i}", $"Unpinned {i}", pinned: false, copiedAt: now.AddMinutes(i)));
        }

        // 限制未固定保留 4 条
        int deleted = _storage.TrimHistory(maxUnpinnedCount: 4);
        Assert.Equal(6, deleted);

        // 剩余条目：1 条固定 + 4 条未固定 = 5 条
        Assert.Equal(5, _storage.GetTotalCount());

        var unpinnedRemaining = _storage.QueryEntries(pinned: false);
        Assert.Equal(4, unpinnedRemaining.Count);
        // 保留的是最新的 4 条 (9, 8, 7, 6)
        Assert.Contains(unpinnedRemaining, e => e.Id == "unpinned-9");
        Assert.Contains(unpinnedRemaining, e => e.Id == "unpinned-8");
        Assert.Contains(unpinnedRemaining, e => e.Id == "unpinned-7");
        Assert.Contains(unpinnedRemaining, e => e.Id == "unpinned-6");
    }

    [Theory]
    [InlineData("https://google.com", ClipKind.Link)]
    [InlineData("http://localhost:8080/api", ClipKind.Link)]
    [InlineData("#FF5733", ClipKind.Color)]
    [InlineData("#fff", ClipKind.Color)]
    [InlineData("rgb(255, 0, 128)", ClipKind.Color)]
    [InlineData("Just normal text", ClipKind.Text)]
    public void Classifier_ClassifiesCorrectly(string input, ClipKind expectedKind)
    {
        var kind = ClipboardClassifier.ClassifyText(input);
        Assert.Equal(expectedKind, kind);
    }

    [Fact]
    public void Classifier_CodeSnippet_ClassifiedAsCode()
    {
        string code = "public class Person\n{\n    public string Name { get; set; }\n}";
        var kind = ClipboardClassifier.ClassifyText(code);
        Assert.Equal(ClipKind.Code, kind);
    }
}
