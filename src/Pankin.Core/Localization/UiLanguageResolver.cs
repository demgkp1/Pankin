using System.Globalization;

namespace Pankin.Core.Localization;

/// <summary>
/// 界面语言解析器：根据用户设置、系统区域文化或环境变量解析最适宜的界面显示语言。
/// </summary>
public static class UiLanguageResolver
{
    public const string DefaultLanguage = "zh-CN";

    /// <summary>
    /// 解析最终使用的语言代码（"zh-CN", "zh-Hant", "en-US"）。
    /// </summary>
    public static string Resolve(string? configured, string systemLanguage)
    {
        if (!string.IsNullOrWhiteSpace(configured) && !configured.Equals("system", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeCode(configured);
        }

        var sys = (systemLanguage ?? string.Empty).Trim().ToLowerInvariant();
        if (sys is "zh-tw" or "zh-hk" or "zh-mo" or "zh-hant")
        {
            return "zh-Hant";
        }

        if (sys.StartsWith("en"))
        {
            return "en-US";
        }

        if (sys == "zh-hans")
        {
            return "zh-Hans";
        }

        if (sys.StartsWith("zh"))
        {
            return "zh-CN";
        }

        return DefaultLanguage;
    }

    /// <summary>
    /// 读取系统 UI 语言代码。
    /// </summary>
    public static string SystemLanguage() => SystemLanguage(() => CultureInfo.CurrentUICulture.Name);

    /// <summary>
    /// 系统语言读取接缝，捕获任何未知文化异常并安全回退为空字符串。
    /// </summary>
    public static string SystemLanguage(Func<string> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeCode(string code) => code.Trim() switch
    {
        "zh" or "zh-cn" or "zh-Hans" or "zh-hans" => code, // 保留调用方的 zh-Hans 或 zh-CN
        "zh-tw" or "zh-hk" or "zh-mo" or "zh-Hant" or "zh-hant" => "zh-Hant",
        "en" or "en-us" or "en-US" => "en-US",
        _ => code
    };
}
