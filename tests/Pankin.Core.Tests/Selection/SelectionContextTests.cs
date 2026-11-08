using System;
using System.Collections.Generic;
using Pankin.Core.Selection;
using Xunit;

namespace Pankin.Core.Tests.Selection;

public sealed class SelectionContextTests
{
    [Fact]
    public void CreateText_ValidText_InitializesProperly()
    {
        var ctx = SelectionContext.CreateText("Hello Pankin", 100, 200, "chrome", "Google - Welcome");

        Assert.True(ctx.HasText);
        Assert.False(ctx.HasFiles);
        Assert.True(ctx.IsSuccess);
        Assert.Equal("Hello Pankin", ctx.Text);
        Assert.Equal(100, ctx.PointerX);
        Assert.Equal(200, ctx.PointerY);
        Assert.Equal("chrome", ctx.SourceAppName);
        Assert.Equal("Google - Welcome", ctx.WindowTitle);
        Assert.Equal(SelectionFailure.None, ctx.Failure);
        Assert.Equal(SelectionKind.Text, ctx.Kind);
    }

    [Fact]
    public void CreateText_EmptyText_SetsFailureToEmpty()
    {
        var ctx = SelectionContext.CreateText("   ", 50, 50);

        Assert.False(ctx.HasText);
        Assert.False(ctx.IsSuccess);
        Assert.Equal(SelectionFailure.Empty, ctx.Failure);
    }

    [Fact]
    public void CreateFiles_ValidFiles_SetsKindToFiles()
    {
        var files = new List<string> { "/home/user/doc.txt", "/home/user/pic.png" };
        var ctx = SelectionContext.CreateFiles(files, 10, 20, "nautilus", "Files");

        Assert.True(ctx.HasFiles);
        Assert.False(ctx.HasText);
        Assert.True(ctx.IsSuccess);
        Assert.Equal(SelectionKind.Files, ctx.Kind);
        Assert.Equal(2, ctx.Files!.Count);
        Assert.Equal("2 个文件", ctx.PreviewText);
    }

    [Fact]
    public void CreateFiles_SingleFile_PreviewShowsFileName()
    {
        var files = new List<string> { "/home/user/document.pdf" };
        var ctx = SelectionContext.CreateFiles(files);

        Assert.Equal("document.pdf", ctx.PreviewText);
    }

    [Fact]
    public void CreateFailure_SetsFailureStatus()
    {
        var ctx = SelectionContext.CreateFailure(SelectionFailure.Blacklisted, 10, 10, "game.exe");

        Assert.False(ctx.IsSuccess);
        Assert.Equal(SelectionFailure.Blacklisted, ctx.Failure);
        Assert.Equal("game.exe", ctx.SourceAppName);
    }

    [Theory]
    [InlineData("https://github.com/avaloniaui", true)]
    [InlineData("http://localhost:5000/api", true)]
    [InlineData("ftp://files.example.com", true)]
    [InlineData("www.bing.com", true)]
    [InlineData("not a url", false)]
    [InlineData("https://invalid url with spaces", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void DetectIsUrl_VariousInputs_ValidatedCorrectly(string? input, bool expected)
    {
        bool actual = SelectionContext.DetectIsUrl(input);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("/home/dkp/file.txt", true)]
    [InlineData("/etc/hosts", true)]
    [InlineData("file:///home/dkp/pic.png", true)]
    [InlineData("just text", false)]
    [InlineData("/path/with\nnewline", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void DetectIsFilePath_VariousInputs_ValidatedCorrectly(string? input, bool expected)
    {
        bool actual = SelectionContext.DetectIsFilePath(input);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Kind_MultiLineText_ReturnsMultiLine()
    {
        var ctx = SelectionContext.CreateText("Line 1\nLine 2\nLine 3");
        Assert.Equal(SelectionKind.MultiLineText, ctx.Kind);
    }

    [Fact]
    public void WordCount_CalculatesCorrectly()
    {
        var ctx = SelectionContext.CreateText("The quick brown fox jumps over the lazy dog");
        Assert.Equal(9, ctx.WordCount);

        var emptyCtx = SelectionContext.CreateText("   ");
        Assert.Equal(0, emptyCtx.WordCount);
    }

    [Fact]
    public void PreviewText_LongSingleLine_TruncatesWithEllipsis()
    {
        string longText = new string('a', 120);
        var ctx = SelectionContext.CreateText(longText);

        Assert.Equal(83, ctx.PreviewText.Length);
        Assert.EndsWith("...", ctx.PreviewText);
    }
}
