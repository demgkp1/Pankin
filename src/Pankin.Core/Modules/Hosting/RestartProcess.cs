using System.Diagnostics;
using System.Globalization;

namespace Pankin.Core.Modules.Hosting;

/// <summary>
/// 跨平台应用安全重启协调器（通过环境变量传递旧进程 PID 与启动时间戳，无任何 Windows 原生 API）
/// </summary>
public static class RestartProcess
{
    private const string ParentVariable = "PANKIN_RESTART_PARENT";
    public static bool Requested { get; set; }

    public static void WaitForParent()
    {
        var value = Environment.GetEnvironmentVariable(ParentVariable);
        Environment.SetEnvironmentVariable(ParentVariable, null);
        if (string.IsNullOrEmpty(value)) return;

        var parts = value.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int pid) || !long.TryParse(parts[1], out long ticks))
        {
            throw new InvalidDataException("重启进程标识格式无效");
        }

        Process parent;
        try
        {
            parent = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            // 旧进程已经完全退出
            return;
        }

        using (parent)
        {
            try
            {
                if (parent.StartTime.ToUniversalTime().Ticks != ticks) return;
                if (!parent.WaitForExit(30000))
                {
                    throw new TimeoutException("等待旧实例进程退出超时");
                }
            }
            catch (InvalidOperationException) when (parent.HasExited)
            {
                // 查询启动时间前旧进程已退出
            }
        }
    }

    public static void Start()
    {
        using var current = Process.GetCurrentProcess();
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("无法获取当前进程可执行文件路径");
        var start = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment[ParentVariable] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ":" +
                                            current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);

        using var child = Process.Start(start) ?? throw new IOException("启动新实例进程失败");
        Requested = true;
    }
}
