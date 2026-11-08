using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Pankin.Core.FolderJump;
using Xunit;

namespace Pankin.Core.Tests.FolderJump;

public class FolderJumpStoreTests
{
    [Fact]
    public async Task RecordAccess_ShouldIncrementCountAndSortByRecency()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "folder_jump_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new FolderJumpStore(tempFile);
            var folder1 = "/home/test/projects/Pankin";
            var folder2 = "/home/test/documents/Notes";

            await store.RecordAccessAsync(folder1);
            await store.RecordAccessAsync(folder2);
            await store.RecordAccessAsync(folder1); // 访问两次

            var entries = await store.GetEntriesAsync();
            Assert.True(entries.Count >= 2);

            var entry1 = entries.First(e => e.Path == folder1);
            Assert.Equal(2, entry1.AccessCount);

            var entry2 = entries.First(e => e.Path == folder2);
            Assert.Equal(1, entry2.AccessCount);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task TogglePin_ShouldPrioritizePinnedEntries()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "folder_jump_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new FolderJumpStore(tempFile);
            var folder1 = "/home/test/downloads";
            var folder2 = "/home/test/pinned_work";

            await store.RecordAccessAsync(folder1);
            await store.RecordAccessAsync(folder1);
            await store.RecordAccessAsync(folder1); // 3次，本应作为高频排在前面

            await store.RecordAccessAsync(folder2);
            await store.TogglePinAsync(folder2); // 置顶

            var entries = await store.GetEntriesAsync();
            Assert.Equal(folder2, entries[0].Path);
            Assert.True(entries[0].IsPinned);
            Assert.Equal(FolderJumpSource.Pinned, entries[0].Source);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Search_PinyinAndEnglish_ShouldMatchAccurately()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "folder_jump_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new FolderJumpStore(tempFile);
            await store.RecordAccessAsync("/home/test/开发文档");
            await store.RecordAccessAsync("/home/test/代码仓库");
            await store.RecordAccessAsync("/home/test/PersonalProjects");

            // 1. 拼音首字母匹配 "kfwd" -> "开发文档"
            var resultsPinyin = await store.SearchAsync("kfwd");
            Assert.NotEmpty(resultsPinyin);
            Assert.Contains(resultsPinyin, r => r.Name == "开发文档");

            // 2. 全拼匹配 "daima" -> "代码仓库"
            var resultsFullPinyin = await store.SearchAsync("daima");
            Assert.NotEmpty(resultsFullPinyin);
            Assert.Contains(resultsFullPinyin, r => r.Name == "代码仓库");

            // 3. 英文子串匹配 "projects" -> "PersonalProjects"
            var resultsEnglish = await store.SearchAsync("projects");
            Assert.NotEmpty(resultsEnglish);
            Assert.Contains(resultsEnglish, r => r.Name == "PersonalProjects");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ClearHistory_ShouldEmptyStore()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "folder_jump_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new FolderJumpStore(tempFile);
            await store.RecordAccessAsync("/home/test/folder1");
            Assert.NotEmpty(await store.GetEntriesAsync());

            await store.ClearHistoryAsync();
            Assert.Empty(await store.GetEntriesAsync());
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
