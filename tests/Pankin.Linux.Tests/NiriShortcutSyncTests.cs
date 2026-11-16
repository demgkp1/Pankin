using Pankin.Platform.Linux;
using Xunit;

namespace Pankin.Linux.Tests;

public sealed class NiriShortcutSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pankin-niri-tests-" + Guid.NewGuid().ToString("N"));

    public NiriShortcutSyncTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("Ctrl+Space", "Ctrl+Space")]
    [InlineData("Control+Alt+Delete", null)]
    [InlineData("Alt+,", "Alt+Comma")]
    [InlineData("Alt+comma", "Alt+Comma")]
    [InlineData("Ctrl+.", "Ctrl+Period")]
    [InlineData("Ctrl+period", "Ctrl+Period")]
    [InlineData("Ctrl+-", "Ctrl+Minus")]
    [InlineData("Ctrl+minus", "Ctrl+Minus")]
    [InlineData("Ctrl+=", "Ctrl+Equal")]
    [InlineData("Ctrl+equals", "Ctrl+Equal")]
    [InlineData("Ctrl+equal", "Ctrl+Equal")]
    [InlineData("Ctrl+;", "Ctrl+Semicolon")]
    [InlineData("Ctrl+semicolon", "Ctrl+Semicolon")]
    [InlineData("Ctrl+/", "Ctrl+Slash")]
    [InlineData("Ctrl+slash", "Ctrl+Slash")]
    [InlineData("Ctrl+`", "Ctrl+Grave")]
    [InlineData("Ctrl+backquote", "Ctrl+Grave")]
    [InlineData("Ctrl+grave", "Ctrl+Grave")]
    [InlineData("Ctrl+[", "Ctrl+BracketLeft")]
    [InlineData("Ctrl+openbracket", "Ctrl+BracketLeft")]
    [InlineData("Ctrl+\\", "Ctrl+Backslash")]
    [InlineData("Ctrl+backslash", "Ctrl+Backslash")]
    [InlineData("Ctrl+]", "Ctrl+BracketRight")]
    [InlineData("Ctrl+closebracket", "Ctrl+BracketRight")]
    [InlineData("Ctrl+'", "Ctrl+Apostrophe")]
    [InlineData("Ctrl+quote", "Ctrl+Apostrophe")]
    [InlineData("Alt+tab", "Alt+Tab")]
    [InlineData("Ctrl+enter", "Ctrl+Return")]
    [InlineData("Ctrl+return", "Ctrl+Return")]
    [InlineData("Ctrl+esc", "Ctrl+Escape")]
    [InlineData("Ctrl+escape", "Ctrl+Escape")]
    [InlineData("Ctrl+backspace", "Ctrl+BackSpace")]
    [InlineData("Ctrl+Back", "Ctrl+BackSpace")]
    [InlineData("Win+Shift+a", "Shift+Super+A")]
    [InlineData("Meta+Opt+s", "Alt+Super+S")]
    [InlineData("Windows+Option+F12", "Alt+Super+F12")]
    [InlineData("F1", "F1")]
    [InlineData("Ctrl+F24", "Ctrl+F24")]
    [InlineData("Alt+D5", "Alt+5")]
    [InlineData("Alt+0", "Alt+0")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("Ctrl+Alt", null)]
    [InlineData("Ctrl+F25", null)]
    [InlineData("Ctrl+UnknownKey", null)]
    public void FormatNiriHotkeyMapsModifiersAndKeys(string? input, string? expected)
    {
        Assert.Equal(expected, NiriShortcutSync.FormatNiriHotkey(input));
    }

    [Fact]
    public void ApplyShortcutsBlockInsertsIntoBindsIgnoringCommentsAndStrings()
    {
        const string initial = """
            // binds { fake comment block }
            spawn-at-startup "sh" "-c" "echo 'binds { fake }'"

            binds {
                // comment with } brace
                Mod+T { spawn "alacritty"; }
                Mod+Q hotkey-overlay-title="Close {window}" { close-window; }
            }
            """;

        const string block = """
                // BEGIN PANKIN SHORTCUTS
                Ctrl+Space hotkey-overlay-title="Pankin: Toggle Launcher" { spawn "/usr/bin/pankin" "--toggle"; }
                // END PANKIN SHORTCUTS
            """;

        var updated = NiriShortcutSync.ApplyShortcutsBlock(initial, block);

        Assert.Contains("Mod+Q hotkey-overlay-title=\"Close {window}\" { close-window; }\n    // BEGIN PANKIN SHORTCUTS", updated);
        Assert.Contains("    // END PANKIN SHORTCUTS\n}", updated);
    }

    [Fact]
    public void ApplyShortcutsBlockReplacesExistingDelimitedBlock()
    {
        const string initial = """
            binds {
                Mod+T { spawn "alacritty"; }
                // BEGIN PANKIN SHORTCUTS
                Ctrl+Space hotkey-overlay-title="Pankin: Toggle Launcher" { spawn "/old/pankin" "--toggle"; }
                // END PANKIN SHORTCUTS
            }
            """;

        const string newBlock = """
                // BEGIN PANKIN SHORTCUTS
                Alt+Space hotkey-overlay-title="Pankin: Toggle Launcher" { spawn "/new/pankin" "--toggle"; }
                // END PANKIN SHORTCUTS
            """;

        var updated = NiriShortcutSync.ApplyShortcutsBlock(initial, newBlock);

        Assert.DoesNotContain("/old/pankin", updated);
        Assert.Contains("/new/pankin", updated);
        Assert.Equal(1, updated.Split("// BEGIN PANKIN SHORTCUTS").Length - 1);
        Assert.Equal(1, updated.Split("// END PANKIN SHORTCUTS").Length - 1);
    }

    [Fact]
    public void ApplyShortcutsBlockMigratesUndelimitedLegacyPankinBinds()
    {
        const string initial = """
            binds {
                Mod+T { spawn "alacritty"; }
                // Pankin Desktop Assistant shortcuts
                Ctrl+Space { spawn "/usr/bin/pankin" "--toggle"; }
                Alt+V { spawn "/opt/Pankin.AppImage" "--clipboard"; }
                Alt+S { spawn "pankin" "--superpanel"; }
                Alt+Comma { spawn "pankin" "--settings"; }
            }
            """;

        const string newBlock = """
                // BEGIN PANKIN SHORTCUTS
                Ctrl+Space hotkey-overlay-title="Pankin: Toggle Launcher" { spawn "/usr/bin/pankin" "--toggle"; }
                // END PANKIN SHORTCUTS
            """;

        var updated = NiriShortcutSync.ApplyShortcutsBlock(initial, newBlock);

        Assert.Contains("Mod+T { spawn \"alacritty\"; }", updated);
        Assert.DoesNotContain("// Pankin Desktop Assistant", updated);
        Assert.DoesNotContain("Alt+V { spawn", updated);
        Assert.DoesNotContain("Alt+S { spawn", updated);
        Assert.DoesNotContain("Alt+Comma { spawn", updated);
        Assert.Contains("// BEGIN PANKIN SHORTCUTS", updated);
        Assert.Contains("// END PANKIN SHORTCUTS", updated);
    }

    [Fact]
    public void ApplyShortcutsBlockAppendsBindsWhenMissing()
    {
        const string initial = """
            input {
                keyboard {
                    xkb { }
                }
            }
            """;

        const string block = """
                // BEGIN PANKIN SHORTCUTS
                Ctrl+Space hotkey-overlay-title="Pankin: Toggle Launcher" { spawn "/usr/bin/pankin" "--toggle"; }
                // END PANKIN SHORTCUTS
            """;

        var updated = NiriShortcutSync.ApplyShortcutsBlock(initial, block);

        Assert.Contains("binds {\n    // BEGIN PANKIN SHORTCUTS", updated);
        Assert.EndsWith("}\n", updated);
    }

    [Fact]
    public void SyncWritesValidBlockDeduplicatesAndIsIdempotent()
    {
        var configPath = Path.Combine(_root, "config.kdl");
        File.WriteAllText(configPath, """
            binds {
                Mod+T { spawn "alacritty"; }
            }
            """);

        int validateCalls = 0;
        int reloadCalls = 0;
        string? validatedTempDir = null;

        var sync = new NiriShortcutSync(
            configPath: configPath,
            executablePathProvider: () => "/opt/Pankin.AppImage",
            isNiriSessionProvider: () => true,
            configValidator: tempPath =>
            {
                validateCalls++;
                validatedTempDir = Path.GetDirectoryName(tempPath);
                Assert.True(File.Exists(tempPath));
                return true;
            },
            configReloader: () => reloadCalls++);

        // SuperPanel shares Alt+V with Clipboard to verify deduplication.
        bool first = sync.Sync("Ctrl+Space", "Alt+V", "Alt+v", "Alt+,");
        Assert.True(first);
        Assert.Equal(1, validateCalls);
        Assert.Equal(1, reloadCalls);
        Assert.Equal(_root, validatedTempDir);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));

        var content = File.ReadAllText(configPath);
        Assert.Contains("Ctrl+Space hotkey-overlay-title=\"Pankin: Toggle Launcher\" { spawn \"/opt/Pankin.AppImage\" \"--toggle\"; }", content);
        Assert.Contains("Alt+V hotkey-overlay-title=\"Pankin: Toggle Clipboard\" { spawn \"/opt/Pankin.AppImage\" \"--clipboard\"; }", content);
        Assert.DoesNotContain("--superpanel", content);
        Assert.Contains("Alt+Comma hotkey-overlay-title=\"Pankin: Open Settings\" { spawn \"/opt/Pankin.AppImage\" \"--settings\"; }", content);

        // Second sync with identical hotkeys must be a no-op.
        bool second = sync.Sync("Ctrl+Space", "Alt+V", "Alt+v", "Alt+,");
        Assert.True(second);
        Assert.Equal(1, validateCalls);
        Assert.Equal(1, reloadCalls);
    }

    [Fact]
    public void SyncAbortsAndCleansTempFileWhenValidationFails()
    {
        var configPath = Path.Combine(_root, "config.kdl");
        const string original = "binds {\n    Mod+T { spawn \"alacritty\"; }\n}\n";
        File.WriteAllText(configPath, original);

        int reloadCalls = 0;
        var sync = new NiriShortcutSync(
            configPath: configPath,
            executablePathProvider: () => "/usr/bin/pankin",
            isNiriSessionProvider: () => true,
            configValidator: _ => false,
            configReloader: () => reloadCalls++);

        bool result = sync.Sync("Ctrl+Space", "Alt+V", "Alt+S");

        Assert.False(result);
        Assert.Equal(0, reloadCalls);
        Assert.Equal(original, File.ReadAllText(configPath));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void SyncSkipsWhenNotNiriOrExecutableMissingOrConfigMissing()
    {
        var configPath = Path.Combine(_root, "config.kdl");
        File.WriteAllText(configPath, "binds { }\n");

        var notNiri = new NiriShortcutSync(
            configPath: configPath,
            executablePathProvider: () => "/usr/bin/pankin",
            isNiriSessionProvider: () => false,
            configValidator: _ => true,
            configReloader: () => { });
        Assert.False(notNiri.Sync("Ctrl+Space", "Alt+V", "Alt+S"));

        var noExe = new NiriShortcutSync(
            configPath: configPath,
            executablePathProvider: () => null,
            isNiriSessionProvider: () => true,
            configValidator: _ => true,
            configReloader: () => { });
        Assert.False(noExe.Sync("Ctrl+Space", "Alt+V", "Alt+S"));

        var missingConfig = new NiriShortcutSync(
            configPath: Path.Combine(_root, "missing.kdl"),
            executablePathProvider: () => "/usr/bin/pankin",
            isNiriSessionProvider: () => true,
            configValidator: _ => true,
            configReloader: () => { });
        Assert.False(missingConfig.Sync("Ctrl+Space", "Alt+V", "Alt+S"));
    }
}
