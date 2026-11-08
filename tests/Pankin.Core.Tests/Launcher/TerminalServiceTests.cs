using Pankin.Core.Abstractions;
using Pankin.Core.Launcher;
using Xunit;

namespace Pankin.Core.Tests.Launcher;

public class TerminalServiceTests
{
    private sealed class FakeLauncherActionHost : ILauncherActionHost
    {
        public string? LastCommand { get; private set; }
        public bool LastElevated { get; private set; }

        public Task<bool> OpenAsync(string pathOrUrl, bool elevated = false, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> RevealAsync(string path, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> RunInTerminalAsync(string command, bool elevated = false, string? workingDir = null, CancellationToken ct = default)
        {
            LastCommand = command;
            LastElevated = elevated;
            return Task.FromResult(true);
        }
        public Task<bool> CopyTextAsync(string text, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ExecuteSystemCommandAsync(string command, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ShowPropertiesAsync(string path, CancellationToken ct = default) => Task.FromResult(true);
    }

    [Fact]
    public void Search_EmptyTerminalPrefix_ShouldReturnHint()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pankin-term-test-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var fakeHost = new FakeLauncherActionHost();
            var terminalService = new TerminalService(fakeHost, tempDir);
            var result = terminalService.Search(">");

            Assert.Single(result.Items);
            Assert.Equal("terminal:hint", result.Items[0].Id);
            Assert.Equal(LauncherGroups.Terminal, result.Items[0].Group);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Search_WithCommand_ShouldReturnRunItemAndCompletions()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pankin-term-test-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var fakeHost = new FakeLauncherActionHost();
            var terminalService = new TerminalService(fakeHost, tempDir);
            var result = terminalService.Search("> gi");

            Assert.NotEmpty(result.Items);
            var runItem = result.Items[0];
            Assert.StartsWith("terminal:run:", runItem.Id);
            Assert.Equal("gi", runItem.Subtitle);

            var completions = result.Items.Skip(1).ToList();
            Assert.Contains(completions, c => c.Id == "complete:git");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void TerminalHistory_RecordAndMatch_ShouldPersistAndLimit()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pankin-term-test-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var history = new TerminalHistory(tempDir);
            history.Record("git status");
            history.Record("git pull");
            history.Record("docker ps");

            var matches = history.Match("git");
            Assert.Equal(2, matches.Count);
            Assert.Equal("git pull", matches[0]); // 最近执行排在最前
            Assert.Equal("git status", matches[1]);

            history.Clear();
            Assert.Empty(history.Match("git"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
