using Pankin.Core.Launcher;
using Xunit;

namespace Pankin.Core.Tests.Launcher;

public class PowerTimerTests
{
    private readonly DateTimeOffset _baseTime = new(2026, 11, 7, 12, 0, 0, TimeSpan.FromHours(8));

    [Theory]
    [InlineData("30分钟后关机", PowerAction.Shutdown, 30)]
    [InlineData("10分后重启", PowerAction.Restart, 10)]
    [InlineData("1小时后睡眠", PowerAction.Sleep, 60)]
    [InlineData("半小时后关机", PowerAction.Shutdown, 30)]
    [InlineData("关机 15", PowerAction.Shutdown, 15)]
    [InlineData("restart 20m", PowerAction.Restart, 20)]
    [InlineData("sleep 2h", PowerAction.Sleep, 120)]
    public void Parse_RelativeExpressions_ShouldReturnExpected(string input, PowerAction expectedAction, double expectedMinutes)
    {
        var parsed = PowerTimerParser.Parse(input, _baseTime);

        Assert.NotNull(parsed);
        Assert.Equal(expectedAction, parsed.Action);
        Assert.NotNull(parsed.Minutes);
        Assert.Equal(expectedMinutes, parsed.Minutes.Value);
        Assert.Equal(_baseTime.AddMinutes(expectedMinutes), parsed.At);
    }

    [Theory]
    [InlineData("18:30 关机", PowerAction.Shutdown, 18, 30)]
    [InlineData("下午5点关机", PowerAction.Shutdown, 17, 0)]
    [InlineData("晚上10点半重启", PowerAction.Restart, 22, 30)]
    [InlineData("关机 23:00", PowerAction.Shutdown, 23, 0)]
    public void Parse_AbsoluteExpressions_ShouldReturnExpected(string input, PowerAction expectedAction, int hour, int minute)
    {
        var parsed = PowerTimerParser.Parse(input, _baseTime);

        Assert.NotNull(parsed);
        Assert.Equal(expectedAction, parsed.Action);
        Assert.Equal(hour, parsed.At.Hour);
        Assert.Equal(minute, parsed.At.Minute);
    }

    [Fact]
    public void Parse_AbsoluteTimeEarlierThanNow_ShouldScheduleForTomorrow()
    {
        // _baseTime 是 12:00，指定上午 9 点关机应被定在明天
        var parsed = PowerTimerParser.Parse("上午9点关机", _baseTime);

        Assert.NotNull(parsed);
        Assert.Equal(PowerAction.Shutdown, parsed.Action);
        Assert.Equal(9, parsed.At.Hour);
        Assert.Equal(_baseTime.Day + 1, parsed.At.Day);
    }

    [Fact]
    public void Parse_InvalidOrZero_ShouldReturnNull()
    {
        Assert.Null(PowerTimerParser.Parse("0分钟后关机", _baseTime));
        Assert.Null(PowerTimerParser.Parse("hello world", _baseTime));
        Assert.Null(PowerTimerParser.Parse("", _baseTime));
    }

    [Fact]
    public void PowerTimerService_ScheduleAndCancel_ShouldWorkCorrectly()
    {
        using var service = new PowerTimerService();
        bool changedFired = false;
        service.Changed += (s, e) => changedFired = true;

        var targetTime = DateTimeOffset.Now.AddHours(2);
        service.Schedule(PowerAction.Shutdown, targetTime);

        Assert.NotNull(service.Current);
        Assert.Equal(PowerAction.Shutdown, service.Current.Action);
        Assert.True(changedFired);

        changedFired = false;
        service.Cancel();

        Assert.Null(service.Current);
        Assert.True(changedFired);
    }
}
