using Pankin.Core.Settings;

namespace Pankin.Core.Modules;

/// <summary>
/// 模块标识与 PankinSettings / ModulesSettings 的属性双向映射与归一化
/// </summary>
public static class ModuleSettingsMapping
{
    public static bool GetModuleEnabled(PankinSettings settings, string moduleId, bool defaultEnabled = true)
    {
        var modules = settings.Modules;
        return moduleId switch
        {
            ModuleIds.Launcher => modules.Launcher,
            ModuleIds.Clipboard => modules.Clipboard,
            ModuleIds.Gestures => modules.Gestures,
            ModuleIds.SuperPanel => modules.SuperPanel,
            ModuleIds.Ai => modules.Ai,
            ModuleIds.FolderJump => modules.FolderJump,
            ModuleIds.Rename => modules.BatchRename,
            ModuleIds.PowerTimer => modules.PowerTimer,
            _ => defaultEnabled
        };
    }

    public static PankinSettings SetModuleEnabled(PankinSettings settings, string moduleId, bool enabled)
    {
        var current = settings.Modules;
        var updated = moduleId switch
        {
            ModuleIds.Launcher => current with { Launcher = enabled },
            ModuleIds.Clipboard => current with { Clipboard = enabled },
            ModuleIds.Gestures => current with { Gestures = enabled },
            ModuleIds.SuperPanel => current with { SuperPanel = enabled },
            ModuleIds.Ai => current with { Ai = enabled },
            ModuleIds.FolderJump => current with { FolderJump = enabled },
            ModuleIds.Rename => current with { BatchRename = enabled },
            ModuleIds.PowerTimer => current with { PowerTimer = enabled },
            _ => current
        };

        // 联动旧版/细项设置中的 Enabled 字段保持双向一致
        var res = settings with { Modules = updated };
        if (moduleId == ModuleIds.Clipboard)
        {
            res = res with { Clipboard = res.Clipboard with { Enabled = enabled } };
        }
        else if (moduleId == ModuleIds.SuperPanel)
        {
            res = res with { SuperPanel = res.SuperPanel with { Enabled = enabled } };
        }
        else if (moduleId == ModuleIds.Gestures)
        {
            res = res with { Gestures = res.Gestures with { Enabled = enabled } };
        }
        else if (moduleId == ModuleIds.Ai)
        {
            res = res with { Ai = res.Ai with { Enabled = enabled } };
        }

        return res;
    }
}
