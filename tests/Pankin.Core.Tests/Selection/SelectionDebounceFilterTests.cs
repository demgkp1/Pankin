using System.Collections.Generic;
using Pankin.Core.Selection;
using Xunit;

namespace Pankin.Core.Tests.Selection;

public sealed class SelectionDebounceFilterTests
{
    [Fact]
    public void MouseDrag_WhenEnabled_TriggersDecision()
    {
        var filter = new SelectionDebounceFilter { TriggerOnMouseUp = true };

        filter.RecordMouseDown(1, 100, 100, 1000);
        var decision = filter.RecordMouseUp(1, 150, 120, 1200, wasDragged: true);

        Assert.True(decision.ShouldTrigger);
        Assert.Equal(SelectionTriggerType.MouseDrag, decision.TriggerType);
        Assert.Equal(150, decision.X);
        Assert.Equal(120, decision.Y);
    }

    [Fact]
    public void MouseDrag_WhenDisabled_DoesNotTrigger()
    {
        var filter = new SelectionDebounceFilter { TriggerOnMouseUp = false };

        filter.RecordMouseDown(1, 100, 100, 1000);
        var decision = filter.RecordMouseUp(1, 150, 120, 1200, wasDragged: true);

        Assert.False(decision.ShouldTrigger);
    }

    [Fact]
    public void SingleClick_DoesNotTrigger()
    {
        var filter = new SelectionDebounceFilter();

        filter.RecordMouseDown(1, 100, 100, 1000);
        var decision = filter.RecordMouseUp(1, 100, 100, 1050, wasDragged: false);

        Assert.False(decision.ShouldTrigger);
        Assert.Equal(SelectionTriggerType.None, decision.TriggerType);
    }

    [Fact]
    public void DoubleClick_WithinThresholdAndDistance_TriggersDecision()
    {
        var filter = new SelectionDebounceFilter
        {
            TriggerOnDoubleClick = true,
            DoubleClickThresholdMs = 380,
            MaxDoubleClickDistance = 10.0
        };

        // First click
        filter.RecordMouseDown(1, 100, 100, 1000);
        var d1 = filter.RecordMouseUp(1, 101, 100, 1050, wasDragged: false);
        Assert.False(d1.ShouldTrigger);

        // Second click within 200ms at (102, 101)
        filter.RecordMouseDown(1, 102, 101, 1200);
        var d2 = filter.RecordMouseUp(1, 102, 101, 1250, wasDragged: false);

        Assert.True(d2.ShouldTrigger);
        Assert.Equal(SelectionTriggerType.DoubleClick, d2.TriggerType);
        Assert.Equal(102, d2.X);
        Assert.Equal(101, d2.Y);
    }

    [Fact]
    public void DoubleClick_ExceedingThreshold_DoesNotTrigger()
    {
        var filter = new SelectionDebounceFilter
        {
            TriggerOnDoubleClick = true,
            DoubleClickThresholdMs = 300
        };

        // First click at t=1000
        filter.RecordMouseDown(1, 100, 100, 1000);
        filter.RecordMouseUp(1, 100, 100, 1050, wasDragged: false);

        // Second click at t=1600 (elapsed 550ms > 300ms)
        filter.RecordMouseDown(1, 100, 100, 1600);
        var d2 = filter.RecordMouseUp(1, 100, 100, 1650, wasDragged: false);

        Assert.False(d2.ShouldTrigger);
    }

    [Fact]
    public void DoubleClick_ExceedingDistance_DoesNotTrigger()
    {
        var filter = new SelectionDebounceFilter
        {
            TriggerOnDoubleClick = true,
            MaxDoubleClickDistance = 10.0
        };

        // First click at (100, 100)
        filter.RecordMouseDown(1, 100, 100, 1000);
        filter.RecordMouseUp(1, 100, 100, 1050, wasDragged: false);

        // Second click at (200, 200)
        filter.RecordMouseDown(1, 200, 200, 1150);
        var d2 = filter.RecordMouseUp(1, 200, 200, 1200, wasDragged: false);

        Assert.False(d2.ShouldTrigger);
    }

    [Fact]
    public void DebounceWindow_SuppressesRapidTriggers()
    {
        var filter = new SelectionDebounceFilter
        {
            DebounceWindowMs = 300
        };

        filter.RecordMouseDown(1, 100, 100, 1000);
        var d1 = filter.RecordMouseUp(1, 150, 150, 1100, wasDragged: true);
        Assert.True(d1.ShouldTrigger);

        // Immediate next trigger at t=1200 (only 100ms later)
        filter.RecordMouseDown(1, 150, 150, 1150);
        var d2 = filter.RecordMouseUp(1, 180, 180, 1200, wasDragged: true);
        Assert.False(d2.ShouldTrigger);

        // Later trigger at t=1500 (400ms > 300ms)
        filter.RecordMouseDown(1, 180, 180, 1450);
        var d3 = filter.RecordMouseUp(1, 200, 200, 1500, wasDragged: true);
        Assert.True(d3.ShouldTrigger);
    }

    [Fact]
    public void DuplicateSuppression_SuppressesIdenticalTextWithinWindow()
    {
        var filter = new SelectionDebounceFilter
        {
            DuplicateSuppressWindowMs = 1000
        };

        filter.RecordCapture("Selected Text", 1000);

        Assert.True(filter.IsDuplicate("Selected Text", 1500));
        Assert.False(filter.IsDuplicate("Different Text", 1500));
        Assert.False(filter.IsDuplicate("Selected Text", 2500));
    }

    [Fact]
    public void IsValidLength_ValidatesBoundaries()
    {
        var filter = new SelectionDebounceFilter
        {
            MinLength = 3,
            MaxLength = 50
        };

        Assert.False(filter.IsValidLength(null));
        Assert.False(filter.IsValidLength(""));
        Assert.False(filter.IsValidLength("ab"));
        Assert.True(filter.IsValidLength("abc"));
        Assert.True(filter.IsValidLength(new string('x', 50)));
        Assert.False(filter.IsValidLength(new string('x', 51)));
    }

    [Theory]
    [InlineData("google-chrome", null, new[] { "chrome" }, true)]
    [InlineData("Code", "Pankin.sln", new[] { "code" }, true)]
    [InlineData("gnome-terminal", "Terminal", new[] { "TERMINAL" }, true)]
    [InlineData("nautilus", "Home", new[] { "chrome", "firefox" }, false)]
    [InlineData(null, null, new[] { "chrome" }, false)]
    [InlineData("firefox", "Mozilla", null, false)]
    [InlineData("firefox", "Mozilla", new string[0], false)]
    public void IsBlacklisted_MatchesCorrectly(string? app, string? title, string[]? blacklist, bool expected)
    {
        bool actual = SelectionDebounceFilter.IsBlacklisted(app, title, blacklist);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Reset_ClearsAllInternalState()
    {
        var filter = new SelectionDebounceFilter();
        filter.RecordMouseDown(1, 100, 100, 1000);
        filter.RecordCapture("Captured", 1000);

        filter.Reset();

        Assert.False(filter.IsDuplicate("Captured", 1100));
        Assert.True(filter.CanTrigger(1100));
    }
}
