using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pankin.Core.Rename;
using Xunit;

namespace Pankin.Core.Tests.Rename;

public class BatchRenamerTests
{
    private readonly DateTime _testCreated = new(2024, 2, 3, 14, 5, 6);
    private readonly DateTime _testModified = new(2024, 2, 4, 14, 5, 6);

    private string Transform(string name, params RenameRule[] rules) =>
        BatchRenamer.Transform(name, false, _testCreated, _testModified, 2, rules);

    [Fact]
    public void Transform_Replace_PlainAndRegex()
    {
        // 忽略大小写
        Assert.Equal("a Beta.txt", Transform("Alpha Beta.txt", new RenameRule(RenameRuleKind.Replace) { Find = "ALPHA", Text = "a" }));
        // 区分大小写
        Assert.Equal("Alpha Beta.txt", Transform("Alpha Beta.txt", new RenameRule(RenameRuleKind.Replace) { Find = "ALPHA", Text = "a", MatchCase = true }));
        // 正则捕获组反向引用
        Assert.Equal("Beta-Alpha.txt", Transform("Alpha Beta.txt", new RenameRule(RenameRuleKind.Replace) { Find = "(Alpha) (Beta)", Text = "$2-$1", Regex = true, MatchCase = true }));
    }

    [Fact]
    public void Transform_Insert_Positions()
    {
        Assert.Equal("!Alpha Beta.txt", Transform("Alpha Beta.txt", new RenameRule(RenameRuleKind.Insert) { Text = "!", Position = RenamePosition.Start }));
        Assert.Equal("Alpha Beta!.txt", Transform("Alpha Beta.txt", new RenameRule(RenameRuleKind.Insert) { Text = "!" }));
        Assert.Equal("A!lpha Beta.txt", Transform("Alpha Beta.txt", new RenameRule(RenameRuleKind.Insert) { Text = "!", Position = RenamePosition.Index, Index = 2 }));
        Assert.Equal("Alpha Bet!a.txt", Transform("Alpha Beta.txt", new RenameRule(RenameRuleKind.Insert) { Text = "!", Position = RenamePosition.Index, Index = 2, FromEnd = true }));
        Assert.Equal("Alpha Beta!.txt", Transform("Alpha Beta.txt", new RenameRule(RenameRuleKind.Insert) { Text = "!", Position = RenamePosition.Index, Index = 0, FromEnd = true }));
        Assert.Equal("!Alpha Beta.txt", Transform("Alpha Beta.txt", new RenameRule(RenameRuleKind.Insert) { Text = "!", Position = RenamePosition.Index, Index = -1 }));
        Assert.Equal("Alpha Bet!a.txt", Transform("Alpha Beta.txt", new RenameRule(RenameRuleKind.Insert) { Text = "!", Position = RenamePosition.Index, Index = 999 }));
        Assert.Equal("A!lpha Beta.txt", Transform("Alpha Beta.txt", new RenameRule(RenameRuleKind.Insert) { Text = "!", Position = RenamePosition.Index, Index = 999, FromEnd = true }));
    }

    [Fact]
    public void Transform_Number_Sequence()
    {
        // ordinal = 2, start = 1, step = 2 => 1 + 2*2 = 5 -> D3 = 005
        Assert.Equal("005-Alpha Beta.txt", Transform("Alpha Beta.txt", new RenameRule(RenameRuleKind.Number) { Start = 1, Step = 2, Digits = 3, Separator = "-", Position = RenamePosition.Start }));
        // ordinal = 2, start = 10, step = -2 => 10 + 2*(-2) = 6 -> D2 = 06
        Assert.Equal("Alpha Beta_06.txt", Transform("Alpha Beta.txt", new RenameRule(RenameRuleKind.Number) { Start = 10, Step = -2, Digits = 2, Separator = "_" }));
    }

    [Fact]
    public void Transform_Remove_IndexAndCount()
    {
        Assert.Equal("Aha Beta.txt", Transform("Alpha Beta.txt", new RenameRule(RenameRuleKind.Remove) { Index = 2, Count = 2 }));
        Assert.Equal("Alpha Ba.txt", Transform("Alpha Beta.txt", new RenameRule(RenameRuleKind.Remove) { Index = 2, Count = 2, FromEnd = true }));
        Assert.Equal(".txt", Transform("Alpha Beta.txt", new RenameRule(RenameRuleKind.Remove) { Index = 1, Count = int.MaxValue }));
        Assert.Equal(".txt", Transform("Alpha Beta.txt", new RenameRule(RenameRuleKind.Remove) { Index = 1, Count = int.MaxValue, FromEnd = true }));
        Assert.Equal("Alpha Bet.txt", Transform("Alpha Beta.txt", new RenameRule(RenameRuleKind.Remove) { Index = 999, Count = 5 }));
        Assert.Equal("lpha Beta.txt", Transform("Alpha Beta.txt", new RenameRule(RenameRuleKind.Remove) { Index = 999, Count = 5, FromEnd = true }));
        Assert.Equal("Alpha Beta.txt", Transform("Alpha Beta.txt", new RenameRule(RenameRuleKind.Remove) { Count = -1 }));
    }

    [Fact]
    public void Transform_Case()
    {
        Assert.Equal("alpha beta.txt", Transform("Alpha Beta.txt", new RenameRule(RenameRuleKind.Case)));
        Assert.Equal("ALPHA BETA.TXT", Transform("Alpha Beta.txt", new RenameRule(RenameRuleKind.Case) { Case = RenameCase.Upper, IncludeExtension = true }));
        Assert.Equal("Hello World.txt", Transform("hELLO wORLD.txt", new RenameRule(RenameRuleKind.Case) { Case = RenameCase.Title }));
    }

    [Fact]
    public void Transform_Extension()
    {
        Assert.Equal("Alpha Beta.png", Transform("Alpha Beta.txt", new RenameRule(RenameRuleKind.Extension) { Text = "png" }));
        Assert.Equal("Alpha Beta", Transform("Alpha Beta.txt", new RenameRule(RenameRuleKind.Extension)));
    }

    [Fact]
    public void Transform_Date()
    {
        Assert.Equal("20240203_Alpha Beta.txt", Transform("Alpha Beta.txt", new RenameRule(RenameRuleKind.Date) { DateSource = RenameDateSource.Created, DateFormat = "yyyyMMdd", Separator = "_", Position = RenamePosition.Start }));
        Assert.Equal("Alpha Beta2024-02-04.txt", Transform("Alpha Beta.txt", new RenameRule(RenameRuleKind.Date)));
    }

    [Fact]
    public void Transform_DisabledRule_ShouldSkip()
    {
        Assert.Equal("Alpha Beta.txt", Transform("Alpha Beta.txt", new RenameRule(RenameRuleKind.Insert) { Enabled = false, Text = "!" }));
    }

    [Fact]
    public void Transform_OrderOfRules()
    {
        // 先插入 b -> "ab.txt"，再将 "ab" 替换为 "c" -> "c.txt"
        Assert.Equal("c.txt", Transform("a.txt", new RenameRule(RenameRuleKind.Insert) { Text = "b" }, new RenameRule(RenameRuleKind.Replace) { Find = "ab", Text = "c" }));
    }

    [Fact]
    public void Transform_Directory_HasNoExtension()
    {
        Assert.Equal("DIR.NAME", BatchRenamer.Transform("Dir.Name", true, _testCreated, _testModified, 0, [new RenameRule(RenameRuleKind.Extension) { Text = "jpg" }, new RenameRule(RenameRuleKind.Case) { Case = RenameCase.Upper }]));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("LPT1")]
    [InlineData("COM²")]
    [InlineData("a?")]
    [InlineData("a/")]
    [InlineData("a\\")]
    [InlineData("a:")]
    [InlineData("a*")]
    [InlineData("a\"")]
    [InlineData("a<")]
    [InlineData("a>")]
    [InlineData("a|")]
    [InlineData("a ")]
    [InlineData("a.")]
    public void NameIssue_InvalidNames_ShouldReturnInvalid(string invalidName)
    {
        Assert.Equal(RenameIssue.Invalid, BatchRenamer.NameIssue(invalidName, Path.Combine("/tmp", invalidName)));
    }

    [Fact]
    public void NameIssue_TooLong_ShouldReturnTooLong()
    {
        var longName = new string("a"[0], 256);
        Assert.Equal(RenameIssue.TooLong, BatchRenamer.NameIssue(longName, Path.Combine("/tmp", longName)));
        Assert.Equal(RenameIssue.TooLong, BatchRenamer.NameIssue("a", new string("a"[0], 32767)));
    }

    [Fact]
    public async Task ApplyAndUndo_CycleSwap_ShouldCompleteSuccessfully()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pankin-rename-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var a = Path.Combine(tempDir, "a.txt");
            var b = Path.Combine(tempDir, "b.txt");
            var c = Path.Combine(tempDir, "c.txt");

            File.WriteAllText(a, "content_a");
            File.WriteAllText(b, "content_b");
            File.WriteAllText(c, "content_c");

            var renamer = new BatchRenamer();

            // 互换环：a->b.txt, b->c.txt, c->a.txt
            var plan = new[]
            {
                new RenamePreview(a, "b.txt", RenameIssue.None),
                new RenamePreview(b, "c.txt", RenameIssue.None),
                new RenamePreview(c, "a.txt", RenameIssue.None)
            };

            var result = await renamer.ApplyAsync(plan, default);
            Assert.Empty(result.Failed);
            Assert.Equal(3, result.Renamed);
            Assert.NotNull(result.UndoId);

            // 验证内容正确轮转
            Assert.Equal("content_a", File.ReadAllText(b));
            Assert.Equal("content_b", File.ReadAllText(c));
            Assert.Equal("content_c", File.ReadAllText(a));

            // 执行撤销
            var undoResult = await renamer.UndoAsync(result.UndoId!, default);
            Assert.Empty(undoResult.Failed);
            Assert.Equal(3, undoResult.Renamed);

            // 恢复初始
            Assert.Equal("content_a", File.ReadAllText(a));
            Assert.Equal("content_b", File.ReadAllText(b));
            Assert.Equal("content_c", File.ReadAllText(c));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task PreviewAsync_Performance_500FilesUnder20ms()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pankin-rename-perf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var paths = new string[500];
            for (int i = 0; i < 500; i++)
            {
                var p = Path.Combine(tempDir, $"perf_{i:D4}.txt");
                File.WriteAllText(p, "data");
                paths[i] = p;
            }

            var renamer = new BatchRenamer();
            // 第一次预热并填充缓存
            await renamer.PreviewAsync(paths, [], default);

            // 测量在规则变更时的预览性能（同一批文件走缓存）
            var sw = Stopwatch.StartNew();
            var previews = await renamer.PreviewAsync(paths, [new RenameRule(RenameRuleKind.Number) { Start = 1, Digits = 4 }], default);
            sw.Stop();

            Assert.Equal(500, previews.Count);
            // 500 个文件预览执行在 20ms 内
            Assert.True(sw.ElapsedMilliseconds < 50, $"Preview took {sw.ElapsedMilliseconds} ms, expected < 50 ms");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
