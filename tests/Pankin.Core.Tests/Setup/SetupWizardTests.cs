using System.Threading.Tasks;
using Pankin.Core.Abstractions;
using Pankin.Core.Settings;
using Pankin.Core.Setup;
using Xunit;

namespace Pankin.Core.Tests.Setup;

public class SetupWizardTests
{
    private class FakeAutostartService : IAutostartService
    {
        public bool IsAutostartEnabled { get; private set; }
        public void SetAutostart(bool enabled) => IsAutostartEnabled = enabled;
    }

    [Fact]
    public void Navigation_ShouldAdvanceAndStepBackCorrectly()
    {
        var wizard = new SetupWizardService();

        Assert.Equal(SetupStepKind.Welcome, wizard.CurrentStep);
        Assert.False(wizard.CanGoPrevious);
        Assert.True(wizard.CanGoNext);

        Assert.True(wizard.NextStep());
        Assert.Equal(SetupStepKind.Language, wizard.CurrentStep);
        Assert.True(wizard.CanGoPrevious);

        wizard.GoToStep(SetupStepKind.Complete);
        Assert.Equal(SetupStepKind.Complete, wizard.CurrentStep);
        Assert.False(wizard.CanGoNext);

        Assert.True(wizard.PreviousStep());
        Assert.Equal(SetupStepKind.Autostart, wizard.CurrentStep);
    }

    [Fact]
    public async Task ApplySetup_ShouldApplyConfigToSettingsAndAutostart()
    {
        var wizard = new SetupWizardService();
        wizard.Configuration.Language = "en-US";
        wizard.Configuration.LauncherHotkey = "Alt+Space";
        wizard.Configuration.ClipboardHotkey = "Ctrl+Shift+V";
        wizard.Configuration.Autostart = true;
        wizard.Configuration.AiProvider = "OpenAI";
        wizard.Configuration.AiApiKey = "sk-test-key";
        wizard.Configuration.AiBaseUrl = "https://api.openai.com/v1";
        wizard.Configuration.AiModel = "gpt-4o";

        var fakeAutostart = new FakeAutostartService();
        var initialSettings = new PankinSettings();

        var updated = await wizard.ApplySetupAsync(initialSettings, fakeAutostart);

        Assert.Equal("en-US", updated.General.UiLanguage);
        Assert.True(updated.General.StartAtLogin);
        Assert.Equal("Alt+Space", updated.Launcher.Hotkey);
        Assert.Equal("Ctrl+Shift+V", updated.Clipboard.Hotkey);
        Assert.Equal("sk-test-key", updated.Ai.ApiKey);
        Assert.Equal("https://api.openai.com/v1", updated.Ai.BaseUrl);
        Assert.Equal("gpt-4o", updated.Ai.Model);
        Assert.True(fakeAutostart.IsAutostartEnabled);
    }
}
