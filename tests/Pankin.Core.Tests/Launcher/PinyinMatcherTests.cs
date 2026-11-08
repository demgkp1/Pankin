using Pankin.Core.Launcher;
using Xunit;

namespace Pankin.Core.Tests.Launcher;

public class PinyinMatcherTests
{
    private readonly PinyinMatcher _matcher = PinyinMatcher.Instance;

    [Theory]
    [InlineData("百度网盘", "bdwp")]
    [InlineData("开发工具", "kfgj")]
    [InlineData("计算器", "jsq")]
    [InlineData("Edge 浏览器", "edgellq")]
    public void GetInitials_ShouldExtractExpectedInitials(string text, string expected)
    {
        var initials = _matcher.GetInitials(text);
        Assert.Equal(expected, initials);
    }

    [Fact]
    public void GetInitials_PrimaryReading_ForPolyphones()
    {
        // 字典中 "信" 的首选读音为 "shen"
        var initials = _matcher.GetInitials("微信");
        Assert.Equal("ws", initials);
    }

    [Fact]
    public void GetInitials_EmptyOrNull_ShouldReturnEmpty()
    {
        Assert.Equal(string.Empty, _matcher.GetInitials(""));
        Assert.Equal(string.Empty, _matcher.GetInitials(null!));
    }

    [Fact]
    public void GetPinyinTokens_ChineseText_ShouldReturnPinyinTokens()
    {
        var tokens = _matcher.GetPinyinTokens("百度网盘");
        Assert.Equal(4, tokens.Count);
        Assert.Equal("bai", tokens[0]);
        Assert.Equal("du", tokens[1]);
        Assert.Equal("wang", tokens[2]);
        Assert.Equal("pan", tokens[3]);
    }

    [Fact]
    public void GetPinyinTokens_Empty_ShouldReturnEmpty()
    {
        var tokens = _matcher.GetPinyinTokens("");
        Assert.Empty(tokens);
    }

    [Fact]
    public void Match_ExactMatch_ShouldReturnHighestScore()
    {
        var match = _matcher.Match("微信", "微信");
        Assert.NotNull(match);
        Assert.Equal(100, match.Score);
        Assert.Single(match.Spans);
        Assert.Equal(0, match.Spans[0].Start);
        Assert.Equal(2, match.Spans[0].Length);
    }

    [Theory]
    [InlineData("微信", "wx")]             // 多音字拼音首字母匹配
    [InlineData("微信", "ws")]             // 第一读音首字母
    [InlineData("微信", "weixin")]         // 全拼匹配
    [InlineData("百度网盘", "bdwp")]       // 4字首字母
    [InlineData("百度网盘", "baidu")]      // 前缀全拼
    [InlineData("百度网盘", "wangpan")]    // 子串全拼
    [InlineData("Visual Studio Code", "code")] // 英文子串
    [InlineData("Google Chrome 浏览器", "chrome")] // 中英混合 英文部分
    [InlineData("Google Chrome 浏览器", "llq")]    // 中英混合 中文简拼
    public void Match_ValidQueries_ShouldMatch(string target, string query)
    {
        var match = _matcher.Match(target, query);
        Assert.NotNull(match);
        Assert.True(match.Score > 0);
    }

    [Theory]
    [InlineData("微信", "qq")]
    [InlineData("百度", "taobao")]
    [InlineData("Notepad", "xyz123")]
    public void Match_NoMatch_ShouldReturnNull(string target, string query)
    {
        var match = _matcher.Match(target, query);
        Assert.Null(match);
    }

    [Theory]
    [InlineData("", "test")]
    [InlineData("test", "")]
    [InlineData("   ", "test")]
    [InlineData("test", "   ")]
    public void Match_EmptyOrWhitespace_ShouldReturnNull(string target, string query)
    {
        var match = _matcher.Match(target, query);
        Assert.Null(match);
    }

    [Fact]
    public void CreateLocalized_ShouldRetainDisplayName()
    {
        var searchName = _matcher.CreateLocalized("文本编辑器", "gedit");
        Assert.Equal("文本编辑器", searchName.Name);
        var match = searchName.Match("gedit");
        Assert.NotNull(match);
    }
}
