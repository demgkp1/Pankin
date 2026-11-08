using Pankin.Core.Abstractions;

namespace Pankin.Core.Launcher;

/// <summary>
/// 系统关机/重启/睡眠倒计时及到期执行服务
/// </summary>
public sealed class PowerTimerService : IPowerTimer, IDisposable
{
    private readonly ILauncherActionHost? _actionHost;
    private readonly Func<PowerAction, Task<bool>>? _customExecutor;
    private readonly TimeProvider _clock;
    private readonly object _lock = new();
    private readonly Timer _timer;
    private PowerTimerState? _current;
    private bool _disposed;

    public PowerTimerState? Current
    {
        get
        {
            lock (_lock) return _current;
        }
    }

    public event EventHandler? Changed;
    public event EventHandler<PowerTimerResult>? Finished;

    public PowerTimerService(ILauncherActionHost? actionHost = null, TimeProvider? clock = null, Func<PowerAction, Task<bool>>? customExecutor = null)
    {
        _actionHost = actionHost;
        _customExecutor = customExecutor;
        _clock = clock ?? TimeProvider.System;
        _timer = new Timer(OnTimerTick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void Schedule(PowerAction action, DateTimeOffset at)
    {
        var now = _clock.GetLocalNow();
        var diff = at - now;
        if (diff < TimeSpan.FromSeconds(30) || diff > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(at), "定时时间必须在当前时间 30 秒至 24 小时之间。");
        }

        lock (_lock)
        {
            _current = new PowerTimerState(action, at, diff <= TimeSpan.FromSeconds(60));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Cancel()
    {
        bool hadTimer;
        lock (_lock)
        {
            hadTimer = _current != null;
            _current = null;
        }

        if (hadTimer)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Postpone(TimeSpan by)
    {
        lock (_lock)
        {
            if (_current == null) return;
            var newAt = _clock.GetLocalNow() + by;
            _current = new PowerTimerState(_current.Action, newAt, false);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnTimerTick(object? state)
    {
        PowerTimerState? target = null;
        DateTimeOffset now;
        bool shouldNotifyReminding = false;
        bool shouldExecute = false;
        bool isMissed = false;

        lock (_lock)
        {
            if (_disposed || _current == null) return;
            now = _clock.GetLocalNow();
            target = _current;

            if (now >= target.At)
            {
                // 到期执行
                if (now - target.At > TimeSpan.FromMinutes(1))
                {
                    isMissed = true;
                }
                else
                {
                    shouldExecute = true;
                }
                _current = null;
            }
            else if (!target.Reminding && (target.At - now) <= TimeSpan.FromSeconds(60))
            {
                // 进入 60 秒提醒阶段
                _current = target with { Reminding = true };
                shouldNotifyReminding = true;
            }
        }

        if (shouldNotifyReminding)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        if (isMissed && target != null)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            Finished?.Invoke(this, new PowerTimerResult(target.Action, PowerTimerOutcome.Missed, "系统唤醒或时间跳变，已错过执行窗口。"));
        }
        else if (shouldExecute && target != null)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            _ = ExecutePowerActionAsync(target.Action);
        }
    }

    private async Task ExecutePowerActionAsync(PowerAction action)
    {
        try
        {
            bool success;
            if (_customExecutor != null)
            {
                success = await _customExecutor(action).ConfigureAwait(false);
            }
            else if (_actionHost != null)
            {
                var cmd = action switch
                {
                    PowerAction.Shutdown => "shutdown",
                    PowerAction.Restart => "restart",
                    PowerAction.Sleep => "sleep",
                    _ => "shutdown"
                };
                success = await _actionHost.ExecuteSystemCommandAsync(cmd).ConfigureAwait(false);
            }
            else
            {
                success = false;
            }

            Finished?.Invoke(this, new PowerTimerResult(
                action,
                success ? PowerTimerOutcome.Executed : PowerTimerOutcome.Failed,
                success ? null : "调用底层电源命令失败。"));
        }
        catch (Exception ex)
        {
            Finished?.Invoke(this, new PowerTimerResult(action, PowerTimerOutcome.Failed, ex.Message));
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _timer.Dispose();
    }
}
