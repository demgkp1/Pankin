using System;
using System.Text.Json;
using System.Threading.Tasks;
using Pankin.Core.Abstractions;
using Pankin.Core.Settings;
using Pankin.Core.Setup;
using Xunit;

namespace Pankin.Core.Tests.Settings;

public class P3SettingsWiringTests
{
    private class DummyAutostart : IAutostartService
    {
        public bool IsAutostartEnabled { get; set; }
        public void SetAutostart(bool enabled) => IsAutostartEnabled = enabled;
    }

    [Fact]
    public void UpdateSettings_HasCorrectDefaults()
    {
        var settings = new UpdateSettings();
        Assert.True(settings.AutoUpdate);
        Assert.True(settings.CheckOnStartup);
        Assert.Null(settings.LastChecked);
        Assert.Null(settings.IgnoredVersion);
    }

    [Fact]
    public void GeneralSettings_SetupCompleted_DefaultIsFalse()
    {
        var settings = new GeneralSettings();
        Assert.False(settings.SetupCompleted);
    }

    [Fact]
    public void Deserialize_LegacyJsonWithoutNewFields_PreservesDefaultsWithoutThrowing()
    {
        string legacyJson = """
        {
            "version": 1,
            "general": {
                "theme": "dark",
                "accentColor": "#5874C9",
                "startAtLogin": false
            },
            "modules": {
                "launcher": true,
                "clipboard": true
            }
        }
        """;

        var deserialized = JsonSerializer.Deserialize<PankinSettings>(legacyJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        });

        Assert.NotNull(deserialized);
        Assert.False(deserialized.General.SetupCompleted);
        Assert.NotNull(deserialized.Update);
        Assert.True(deserialized.Update.AutoUpdate);
        Assert.True(deserialized.Update.CheckOnStartup);
        Assert.Null(deserialized.Update.LastChecked);
        Assert.Null(deserialized.Update.IgnoredVersion);
    }

    [Fact]
    public void Serialize_Roundtrip_WithNewSettings()
    {
        var original = new PankinSettings
        {
            General = new GeneralSettings
            {
                SetupCompleted = true,
                UiLanguage = "zh-CN"
            },
            Update = new UpdateSettings
            {
                AutoUpdate = false,
                CheckOnStartup = true,
                LastChecked = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero),
                IgnoredVersion = "0.2.0"
            }
        };

        var json = JsonSerializer.Serialize(original, new JsonSerializerOptions { WriteIndented = true });
        var loaded = JsonSerializer.Deserialize<PankinSettings>(json);

        Assert.NotNull(loaded);
        Assert.True(loaded.General.SetupCompleted);
        Assert.Equal("zh-CN", loaded.General.UiLanguage);
        Assert.False(loaded.Update.AutoUpdate);
        Assert.True(loaded.Update.CheckOnStartup);
        Assert.Equal("0.2.0", loaded.Update.IgnoredVersion);
        Assert.NotNull(loaded.Update.LastChecked);
    }

    [Fact]
    public async Task SetupWizard_ApplySetup_SetsSetupCompletedToTrue()
    {
        var wizard = new SetupWizardService();
        var initial = new PankinSettings
        {
            General = new GeneralSettings { SetupCompleted = false }
        };

        var autostart = new DummyAutostart();
        var result = await wizard.ApplySetupAsync(initial, autostart);

        Assert.True(result.General.SetupCompleted);
    }
}
