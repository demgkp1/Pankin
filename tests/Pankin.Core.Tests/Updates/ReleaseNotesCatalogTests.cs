using Pankin.Core.Updates;
using Xunit;

namespace Pankin.Core.Tests.Updates;

public class ReleaseNotesCatalogTests
{
    [Fact]
    public void ParseMarkdown_ShouldExtractCategoriesAndItems()
    {
        string md = """
            # Pankin 0.1.100 Release
            
            ## 新增
            - 支持智能划词超级面板
            - ★ 【亮点】AI 深度流式会话
            
            ## 改进
            * 优化拼音模糊匹配检索速度
            
            ## 修复
            - 修复剪贴板图片缓存未释放问题
            """;

        var published = new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);
        var parsed = ReleaseNotesCatalog.ParseMarkdown(md, "0.1.100", published);

        Assert.NotNull(parsed);
        Assert.Equal("0.1.100", parsed.Version);
        Assert.Equal(new DateOnly(2026, 11, 7), parsed.Date);
        Assert.Equal(4, parsed.Items.Count);

        var newItem = parsed.Items[0];
        Assert.Equal(ReleaseNoteKind.New, newItem.Kind);
        Assert.Equal("支持智能划词超级面板", newItem.Text);
        Assert.False(newItem.Highlight);

        var highlightItem = parsed.Items[1];
        Assert.Equal(ReleaseNoteKind.New, highlightItem.Kind);
        Assert.Equal("AI 深度流式会话", highlightItem.Text);
        Assert.True(highlightItem.Highlight);

        var improvedItem = parsed.Items[2];
        Assert.Equal(ReleaseNoteKind.Improved, improvedItem.Kind);
        Assert.Equal("优化拼音模糊匹配检索速度", improvedItem.Text);

        var fixedItem = parsed.Items[3];
        Assert.Equal(ReleaseNoteKind.Fixed, fixedItem.Kind);
        Assert.Equal("修复剪贴板图片缓存未释放问题", fixedItem.Text);
    }

    [Fact]
    public void ReleaseNotesCatalog_ShouldOrderDescendingAndFindVersion()
    {
        string json = """
            [
              {
                "version": "0.1.90",
                "date": "2026-10-01",
                "items": [
                  { "kind": "new", "text": "初期版本" }
                ]
              },
              {
                "version": "0.1.100",
                "date": "2026-11-01",
                "items": [
                  { "kind": "new", "text": "重大更新" }
                ]
              }
            ]
            """;

        var catalog = new ReleaseNotesCatalog(json);

        Assert.Equal(2, catalog.All.Count);
        Assert.Equal("0.1.100", catalog.All[0].Version);
        Assert.Equal("0.1.90", catalog.All[1].Version);

        var found = catalog.Find("0.1.0100");
        Assert.NotNull(found);
        Assert.Equal("0.1.100", found.Version);
    }
}
