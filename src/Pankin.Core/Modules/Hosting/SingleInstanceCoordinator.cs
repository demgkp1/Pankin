using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Pankin.Core.Modules.Hosting;

/// <summary>
/// 跨平台单实例通信协调器：基于 XDG_RUNTIME_DIR 独占文件锁与 Unix Domain Socket，
/// 确保在 Niri / Wayland / AppImage / 多终端会话间稳定唯一实例与快速唤醒，严禁 Windows P/Invoke。
/// </summary>
public static class SingleInstanceCoordinator
{
    private static readonly object SyncRoot = new();
    private static FileStream? _lockStream;

    private static readonly string InstanceId = "Pankin-" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(
            Environment.UserName + "|" +
            (RuntimeMode.Current.IsProduction ? "prod" : "dev") + "|" +
            (Environment.GetEnvironmentVariable("PANKIN_INSTANCE") ?? "default"))))[..24];

    private static string RuntimeDirectory
    {
        get
        {
            var xdgRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrWhiteSpace(xdgRuntime) && Directory.Exists(xdgRuntime))
            {
                return xdgRuntime;
            }
            return Path.GetTempPath();
        }
    }

    private static string LockFilePath => Path.Combine(RuntimeDirectory, InstanceId + ".lock");
    private static string SocketFilePath => Path.Combine(RuntimeDirectory, InstanceId + ".sock");

    public static string ToWireCommand(LaunchCommand command) => command switch
    {
        LaunchCommand.Exit => "exit",
        LaunchCommand.Toggle => "toggle-launcher",
        LaunchCommand.Clipboard => "toggle-clipboard",
        LaunchCommand.SuperPanel => "toggle-superpanel",
        LaunchCommand.Settings => "open-settings",
        LaunchCommand.Autostart => "autostart",
        _ => "show-launcher"
    };

    public static bool Enter(LaunchCommand command)
    {
        if (command == LaunchCommand.Invalid)
        {
            return false;
        }

        lock (SyncRoot)
        {
            if (_lockStream != null)
            {
                return command != LaunchCommand.Exit;
            }

            try
            {
                var stream = new FileStream(LockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                try
                {
                    stream.SetLength(0);
                    var pidBytes = Encoding.UTF8.GetBytes(Environment.ProcessId.ToString());
                    stream.Write(pidBytes, 0, pidBytes.Length);
                    stream.Flush();
                }
                catch
                {
                    // PID 写入失败不影响文件锁持有
                }

                if (command == LaunchCommand.Exit)
                {
                    stream.Dispose();
                    return false;
                }

                _lockStream = stream;
                TryDeleteSocketFile();
                return true;
            }
            catch (IOException)
            {
                // 文件锁已被存活的主实例持有
            }
            catch
            {
                return true;
            }
        }

        if (command == LaunchCommand.Autostart)
        {
            return false;
        }

        ForwardCommandToPrimary(command);
        return false;
    }

    private static void ForwardCommandToPrimary(LaunchCommand command)
    {
        var wireCommand = ToWireCommand(command);
        for (int attempt = 0; attempt < 15; attempt++)
        {
            try
            {
                if (!File.Exists(SocketFilePath))
                {
                    Thread.Sleep(50);
                    continue;
                }

                using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                client.ReceiveTimeout = 3000;
                client.SendTimeout = 3000;
                client.Connect(new UnixDomainSocketEndPoint(SocketFilePath));

                using var networkStream = new NetworkStream(client, ownsSocket: false);
                using var writer = new StreamWriter(networkStream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                writer.WriteLine(wireCommand);

                using var reader = new StreamReader(networkStream, Encoding.UTF8, leaveOpen: true);
                var reply = reader.ReadLine();
                if (command == LaunchCommand.Exit && int.TryParse(reply, out int targetPid))
                {
                    try
                    {
                        using var targetProcess = Process.GetProcessById(targetPid);
                        targetProcess.WaitForExit(5000);
                    }
                    catch
                    {
                        // 进程已经退出
                    }
                }
                return;
            }
            catch
            {
                Thread.Sleep(60);
            }
        }
    }

    public static async Task ListenAsync(Action<string> commandHandler, CancellationToken cancellationToken)
    {
        TryDeleteSocketFile();
        Socket? server = null;
        try
        {
            server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            server.Bind(new UnixDomainSocketEndPoint(SocketFilePath));
            server.Listen(16);

            while (!cancellationToken.IsCancellationRequested)
            {
                Socket? client = null;
                try
                {
                    client = await server.AcceptAsync(cancellationToken).ConfigureAwait(false);
                    using var stream = new NetworkStream(client, ownsSocket: true);
                    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

                    using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                    if (line == "exit")
                    {
                        await writer.WriteLineAsync(Environment.ProcessId.ToString()).ConfigureAwait(false);
                    }
                    else
                    {
                        await writer.WriteLineAsync("ok").ConfigureAwait(false);
                    }

                    if (!string.IsNullOrEmpty(line))
                    {
                        commandHandler(line);
                    }
                }
                catch (OperationCanceledException)
                {
                    client?.Dispose();
                    break;
                }
                catch
                {
                    client?.Dispose();
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch
        {
            // 容错处理
        }
        finally
        {
            server?.Dispose();
            TryDeleteSocketFile();
        }
    }

    public static void Release()
    {
        lock (SyncRoot)
        {
            if (_lockStream != null)
            {
                try
                {
                    _lockStream.Dispose();
                }
                catch { }
                finally
                {
                    _lockStream = null;
                }
            }
            TryDeleteSocketFile();
        }
    }

    private static void TryDeleteSocketFile()
    {
        try
        {
            if (File.Exists(SocketFilePath))
            {
                File.Delete(SocketFilePath);
            }
        }
        catch
        {
            // 忽略删除失败
        }
    }
}
