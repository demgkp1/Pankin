using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Pankin.Core.Modules.Hosting;

/// <summary>
/// 跨平台单实例通信协调器：基于 NamedPipe (在 Linux 下底层自动映射为 Unix Domain Socket)，严禁 Windows P/Invoke
/// </summary>
public static class SingleInstanceCoordinator
{
    private static Mutex? _mutex;
    private static readonly string PipeName = "Pankin-" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(
            Environment.UserName + "|" +
            (RuntimeMode.Current.IsProduction ? "prod" : "dev") + "|" +
            (Environment.GetEnvironmentVariable("PANKIN_INSTANCE") ?? "default"))))[..24];

    public static bool Enter(LaunchCommand command)
    {
        try
        {
            _mutex = new Mutex(true, "Local\\" + PipeName, out bool isFirstInstance);
            if (isFirstInstance)
            {
                if (command == LaunchCommand.Exit)
                {
                    Release();
                    return false;
                }
                return true;
            }

            if (command == LaunchCommand.Autostart)
            {
                Release();
                return false;
            }

            // 存在已有实例，向主实例管道发送控制命令
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
                pipe.Connect(3000);

                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                writer.WriteLine(command == LaunchCommand.Exit ? "exit" : "show-launcher");

                if (command == LaunchCommand.Exit)
                {
                    using var reader = new StreamReader(pipe, leaveOpen: true);
                    var reply = reader.ReadLine();
                    if (int.TryParse(reply, out int targetPid))
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
                }
            }
            catch
            {
                // 通信超时或主实例异常
            }
            finally
            {
                Release();
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    public static async Task ListenAsync(Action<string> commandHandler, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(pipe, leaveOpen: true);
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

                if (line == "exit")
                {
                    using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                    await writer.WriteLineAsync(Environment.ProcessId.ToString()).ConfigureAwait(false);
                }

                if (!string.IsNullOrEmpty(line))
                {
                    commandHandler(line);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // 管道异常时短暂等待后重试
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static void Release()
    {
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch { }
        finally
        {
            _mutex?.Dispose();
            _mutex = null;
        }
    }
}
