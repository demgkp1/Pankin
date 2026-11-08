using System.Text.Json;
using Pankin.Core.Settings;
using Xunit;

namespace Pankin.Core.Tests.Settings;

public class SettingsSerializationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;

    public SettingsSerializationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pankin_serial_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void PankinSettings_RoundTrip_WithAllNewAndPortableFields()
    {
        var original = new PankinSettings
        {
            Version = 2,
            General = new GeneralSettings
            {
                Theme = ThemeMode.Light,
                AccentColor = "#112233",
                PanelRadius = 14.5,
                StartAtLogin = true,
                ShowTrayIcon = false,
                UiLanguage = "en-US"
            },
            Launcher = new LauncherSettings
            {
                Hotkey = "Alt+Space",
                DoubleTapCtrl = false,
                MaxAppRows = 3,
                MaxFileRows = 7,
                DefaultSearchEngine = "google",
                RememberCommands = true,
                WebSuggestions = false,
                PinnedAppIds = ["app1", "app2"],
                FavoriteFolders = ["/tmp", "/var"]
            },
            SuperPanel = new SuperPanelSettings
            {
                Enabled = true,
                TriggerButton = "Middle",
                HoldDurationMs = 350,
                FollowCursor = false,
                TriggerOnSelection = true,
                TriggerOnDoubleClick = false,
                TriggerOnMouseUp = true,
                SelectionMinLength = 5,
                SelectionMaxLength = 1200,
                RestoreClipboardAfterBorrow = false,
                PauseInFullscreen = false,
                ClipboardFallback = false,
                BlacklistedApps = ["code", "steam", "blender"],
                EnabledActions = ["translate", "copy"]
            }
        };

        var json = JsonSerializer.Serialize(original, new JsonSerializerOptions { WriteIndented = true });
        var deserialized = JsonSerializer.Deserialize<PankinSettings>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.Version);

        // General
        Assert.Equal(ThemeMode.Light, deserialized.General.Theme);
        Assert.Equal("#112233", deserialized.General.AccentColor);
        Assert.Equal(14.5, deserialized.General.PanelRadius);
        Assert.True(deserialized.General.StartAtLogin);
        Assert.False(deserialized.General.ShowTrayIcon);
        Assert.Equal("en-US", deserialized.General.UiLanguage);

        // Launcher
        Assert.Equal("Alt+Space", deserialized.Launcher.Hotkey);
        Assert.False(deserialized.Launcher.DoubleTapCtrl);
        Assert.Equal(3, deserialized.Launcher.MaxAppRows);
        Assert.Equal(7, deserialized.Launcher.MaxFileRows);
        Assert.Equal("google", deserialized.Launcher.DefaultSearchEngine);
        Assert.True(deserialized.Launcher.RememberCommands);
        Assert.False(deserialized.Launcher.WebSuggestions);
        Assert.Equal(["app1", "app2"], deserialized.Launcher.PinnedAppIds);
        Assert.Equal(["/tmp", "/var"], deserialized.Launcher.FavoriteFolders);

        // SuperPanel
        Assert.True(deserialized.SuperPanel.Enabled);
        Assert.Equal("Middle", deserialized.SuperPanel.TriggerButton);
        Assert.Equal(350, deserialized.SuperPanel.HoldDurationMs);
        Assert.False(deserialized.SuperPanel.FollowCursor);
        Assert.Equal(5, deserialized.SuperPanel.SelectionMinLength);
        Assert.Equal(1200, deserialized.SuperPanel.SelectionMaxLength);
        Assert.False(deserialized.SuperPanel.RestoreClipboardAfterBorrow);
        Assert.False(deserialized.SuperPanel.PauseInFullscreen);
        Assert.False(deserialized.SuperPanel.ClipboardFallback);
        Assert.Equal(["code", "steam", "blender"], deserialized.SuperPanel.BlacklistedApps);
        Assert.Equal(["translate", "copy"], deserialized.SuperPanel.EnabledActions);
    }

    [Fact]
    public void SettingsStore_PersistsAndReloadsNewFieldsCorrectly()
    {
        var store = new SettingsStore(_configPath);

        store.Update(s => s with
        {
            General = s.General with
            {
                UiLanguage = "zh-CN",
                Theme = ThemeMode.Light
            },
            SuperPanel = s.SuperPanel with
            {
                SelectionMinLength = 4,
                SelectionMaxLength = 3000,
                PauseInFullscreen = false,
                BlacklistedApps = ["gnome-terminal"]
            }
        });

        // 重新从磁盘创建新的 store 实例
        var reloadedStore = new SettingsStore(_configPath);

        Assert.Equal("zh-CN", reloadedStore.Current.General.UiLanguage);
        Assert.Equal(ThemeMode.Light, reloadedStore.Current.General.Theme);
        Assert.Equal(4, reloadedStore.Current.SuperPanel.SelectionMinLength);
        Assert.Equal(3000, reloadedStore.Current.SuperPanel.SelectionMaxLength);
        Assert.False(reloadedStore.Current.SuperPanel.PauseInFullscreen);
        Assert.Contains("gnome-terminal", reloadedStore.Current.SuperPanel.BlacklistedApps);
    }
}
