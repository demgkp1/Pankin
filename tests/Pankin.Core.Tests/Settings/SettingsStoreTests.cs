using Pankin.Core.Settings;
using Xunit;

namespace Pankin.Core.Tests.Settings;

public class SettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;

    public SettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pankin_test_settings_" + Guid.NewGuid().ToString("N"));
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
            // 忽略临时文件清理异常
        }
    }

    [Fact]
    public void Constructor_FileDoesNotExist_CreatesDefaultSettingsFile()
    {
        Assert.False(File.Exists(_configPath));

        var store = new SettingsStore(_configPath);

        Assert.True(File.Exists(_configPath));
        Assert.NotNull(store.Current);
        Assert.Equal(ThemeMode.Dark, store.Current.General.Theme);
        Assert.True(store.Current.Modules.Launcher);
        Assert.Equal("Ctrl+Space", store.Current.Launcher.Hotkey);
    }

    [Fact]
    public void Save_PersistsToFileAndUpdatesCurrent()
    {
        var store = new SettingsStore(_configPath);

        var modified = store.Current with
        {
            General = store.Current.General with
            {
                Theme = ThemeMode.Light,
                AccentColor = "#112233"
            },
            Launcher = store.Current.Launcher with
            {
                Hotkey = "Alt+Space",
                MaxAppRows = 4
            }
        };

        store.Save(modified);

        Assert.Equal(ThemeMode.Light, store.Current.General.Theme);
        Assert.Equal("#112233", store.Current.General.AccentColor);
        Assert.Equal("Alt+Space", store.Current.Launcher.Hotkey);

        string rawJson = File.ReadAllText(_configPath);
        Assert.Contains("light", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#112233", rawJson);
    }

    [Fact]
    public void Save_ThenReload_RoundtripPreservesAllValues()
    {
        var store1 = new SettingsStore(_configPath);
        var custom = store1.Current with
        {
            General = store1.Current.General with
            {
                Theme = ThemeMode.System,
                PanelRadius = 12.5
            },
            Modules = store1.Current.Modules with
            {
                Ai = false,
                PowerTimer = false
            },
            Clipboard = store1.Current.Clipboard with
            {
                HistoryLimit = 200,
                RecordImages = false
            },
            Gestures = store1.Current.Gestures with
            {
                TriggerButton = "Middle",
                MinimumDistance = 45
            }
        };

        store1.Save(custom);

        // 使用新实例加载同一文件验证序列化往返
        var store2 = new SettingsStore(_configPath);
        Assert.Equal(ThemeMode.System, store2.Current.General.Theme);
        Assert.Equal(12.5, store2.Current.General.PanelRadius);
        Assert.False(store2.Current.Modules.Ai);
        Assert.False(store2.Current.Modules.PowerTimer);
        Assert.Equal(200, store2.Current.Clipboard.HistoryLimit);
        Assert.False(store2.Current.Clipboard.RecordImages);
        Assert.Equal("Middle", store2.Current.Gestures.TriggerButton);
        Assert.Equal(45, store2.Current.Gestures.MinimumDistance);
    }

    [Fact]
    public void Update_ModifiesSettingsAtomicallyAndFiresChangedEvent()
    {
        var store = new SettingsStore(_configPath);
        int changedCount = 0;
        PankinSettings? lastSettings = null;

        store.Changed += (_, s) =>
        {
            changedCount++;
            lastSettings = s;
        };

        store.Update(s => s with
        {
            General = s.General with { AccentColor = "#00FF00" }
        });

        Assert.Equal(1, changedCount);
        Assert.NotNull(lastSettings);
        Assert.Equal("#00FF00", lastSettings.General.AccentColor);
        Assert.Equal("#00FF00", store.Current.General.AccentColor);

        // 重新从磁盘读取验证已同步写入
        var storeReload = new SettingsStore(_configPath);
        Assert.Equal("#00FF00", storeReload.Current.General.AccentColor);
    }

    [Fact]
    public void Load_CorruptedFile_RecoversWithDefaultsAndCreatesBackup()
    {
        File.WriteAllText(_configPath, "{ corrupted json: 1234 invalid");

        var store = new SettingsStore(_configPath);

        // 应回退到默认设置
        Assert.NotNull(store.Current);
        Assert.Equal(ThemeMode.Dark, store.Current.General.Theme);

        // 应生成备份文件
        var backups = Directory.GetFiles(_tempDir, "settings.json.bak-*");
        Assert.NotEmpty(backups);
        string backupContent = File.ReadAllText(backups[0]);
        Assert.Contains("corrupted json", backupContent);
    }

    [Fact]
    public void Save_NullSettings_ThrowsArgumentNullException()
    {
        var store = new SettingsStore(_configPath);
        Assert.Throws<ArgumentNullException>(() => store.Save(null!));
    }

    [Fact]
    public void Update_NullUpdater_ThrowsArgumentNullException()
    {
        var store = new SettingsStore(_configPath);
        Assert.Throws<ArgumentNullException>(() => store.Update(null!));
    }
}
