using System.Globalization;
using Pankin.Core.Localization;
using Xunit;

namespace Pankin.Core.Tests.Localization;

public class LocalizationTests
{
    [Theory]
    [InlineData("zh-CN", "en-US", "zh-CN")]
    [InlineData("zh-Hans", "en-US", "zh-Hans")]
    [InlineData("zh-Hant", "en-US", "zh-Hant")]
    [InlineData("en-US", "zh-CN", "en-US")]
    public void UiLanguageResolver_ExplicitConfiguration_OverridesSystem(string configured, string system, string expected)
    {
        var resolved = UiLanguageResolver.Resolve(configured, system);
        Assert.Equal(expected, resolved);
    }

    [Theory]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("zh-MO", "zh-Hant")]
    [InlineData("zh-Hant", "zh-Hant")]
    [InlineData("en-US", "en-US")]
    [InlineData("en-GB", "en-US")]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("ja-JP", "zh-CN")] // 默认回退简体中文
    public void UiLanguageResolver_SystemLanguage_ResolvesCorrectly(string system, string expected)
    {
        var resolved = UiLanguageResolver.Resolve(null, system);
        Assert.Equal(expected, resolved);

        var resolvedFromSystemToken = UiLanguageResolver.Resolve("system", system);
        Assert.Equal(expected, resolvedFromSystemToken);
    }

    [Fact]
    public void UiLanguageResolver_SystemLanguage_HandlesExceptionsSafely()
    {
        var language = UiLanguageResolver.SystemLanguage(() => throw new InvalidOperationException());
        Assert.Equal(string.Empty, language);

        var resolved = UiLanguageResolver.Resolve(null, language);
        Assert.Equal("zh-CN", resolved);
    }

    [Fact]
    public void Strings_ZhCn_RetrievesCorrectTranslations()
    {
        Strings.Initialize("zh-CN");

        Assert.Equal("设置", Strings.Get("Common_Settings"));
        Assert.Equal("Pankin 启动器", Strings.Get("Launcher_Title"));
        Assert.Equal("自动翻译", Strings.Get("SuperPanel_ModeTranslate"));
        Assert.Equal("Pankin 设置中心", Strings.Get("Settings_Title"));
    }

    [Fact]
    public void Strings_EnUs_RetrievesCorrectTranslations()
    {
        Strings.Initialize("en-US");

        Assert.Equal("Settings", Strings.Get("Common_Settings"));
        Assert.Equal("Pankin Launcher", Strings.Get("Launcher_Title"));
        Assert.Equal("Auto Translate", Strings.Get("SuperPanel_ModeTranslate"));
        Assert.Equal("Pankin Settings", Strings.Get("Settings_Title"));

        // 复原
        Strings.Initialize("zh-CN");
    }

    [Fact]
    public void Strings_FallbackChain_ReturnsFallbackValueWhenMissing()
    {
        Strings.Initialize("zh-Hant");

        // 繁体有单独覆盖
        Assert.Equal("設定", Strings.Get("Common_Settings"));
        // 繁体未单独配置的键应回退至 zh-CN
        Assert.Equal("算式计算", Strings.Get("Launcher_Group_Calculation"));

        // 复原
        Strings.Initialize("zh-CN");
    }

    [Fact]
    public void Strings_UnknownKey_ReturnsKeyItself()
    {
        Strings.Initialize("zh-CN");
        var result = Strings.Get("Totally_Unknown_Key_98765");
        Assert.Equal("Totally_Unknown_Key_98765", result);
    }

    [Fact]
    public void Strings_FormattedArguments_AreInsertedCorrectly()
    {
        Strings.Initialize("zh-CN");
        var result = Strings.Get("Launcher_TranslateQuery", "hello");
        Assert.Equal("翻译“hello”", result);

        Strings.Initialize("en-US");
        var resultEn = Strings.Get("Launcher_TranslateQuery", "hello");
        Assert.Equal("Translate \"hello\"", resultEn);

        // 复原
        Strings.Initialize("zh-CN");
    }

    [Fact]
    public void Strings_PseudoLocalization_WrapsTextProperly()
    {
        var pseudo = Strings.PseudoLocalize("测试文本");
        Assert.StartsWith("［", pseudo);
        Assert.EndsWith("］", pseudo);
        Assert.Contains("测试文本", pseudo);
        Assert.Contains("～", pseudo);

        var previous = Environment.GetEnvironmentVariable("PANKIN_PSEUDO_LOC");
        try
        {
            Environment.SetEnvironmentVariable("PANKIN_PSEUDO_LOC", "1");
            Strings.Initialize("zh-CN");

            var localized = Strings.Get("Common_Settings");
            Assert.StartsWith("［", localized);
            Assert.EndsWith("］", localized);
            Assert.Contains("设置", localized);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PANKIN_PSEUDO_LOC", previous);
            Strings.Initialize("zh-CN");
        }
    }
}
