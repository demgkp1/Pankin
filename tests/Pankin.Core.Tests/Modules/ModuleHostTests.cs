using Pankin.Core.Modules;
using Xunit;

namespace Pankin.Core.Tests.Modules;

public class ModuleHostTests
{
    private sealed class FakeModule : ICapabilityModule
    {
        public ModuleInfo Info { get; }
        public bool FailStart { get; set; }
        public bool FailStop { get; set; }
        public int Starts { get; private set; }
        public int Stops { get; private set; }

        public FakeModule(string id, bool defaultEnabled = false, params ModuleDependency[] dependencies)
        {
            Info = new ModuleInfo(id, ModuleCategory.Files, "test-glyph", defaultEnabled, dependencies);
        }

        public Task StartAsync(CancellationToken token)
        {
            Starts++;
            if (FailStart) throw new InvalidOperationException("fixture start failure");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken token)
        {
            Stops++;
            if (FailStop) throw new InvalidOperationException("fixture stop failure");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Constructor_CyclicRequiredDependencies_ShouldThrowArgumentException()
    {
        var modA = new FakeModule("a", false, new ModuleDependency("b", DependencyKind.Required));
        var modB = new FakeModule("b", false, new ModuleDependency("a", DependencyKind.Required));

        var ex = Assert.Throws<ArgumentException>(() =>
            new ModuleHost(new[] { modA, modB }, _ => false, (_, _) => { }));

        Assert.Contains("成环", ex.Message);
    }

    [Fact]
    public void Preview_TopologicalOrderForHardDependencies()
    {
        var parent = new FakeModule("parent");
        var child = new FakeModule("child", false, new ModuleDependency("parent", DependencyKind.Required));
        var leaf = new FakeModule("leaf", false, new ModuleDependency("child", DependencyKind.Required));
        var optional = new FakeModule("optional", false, new ModuleDependency("parent", DependencyKind.Optional));

        var store = new Dictionary<string, bool>();
        var host = new ModuleHost(
            new[] { parent, child, leaf, optional },
            id => store.GetValueOrDefault(id, false),
            (id, val) => store[id] = val);

        var preview = host.Preview("leaf", true);

        Assert.Equal("leaf", preview.Id);
        Assert.True(preview.Enable);
        Assert.Equal(new[] { "child", "parent" }, preview.AlsoEnable.OrderBy(x => x).ToArray());
        Assert.Empty(preview.AlsoDisable);
    }

    [Fact]
    public async Task ApplyAsync_ShouldEnableDependenciesAndReportState()
    {
        var parent = new FakeModule("parent");
        var child = new FakeModule("child", false, new ModuleDependency("parent", DependencyKind.Required));
        var leaf = new FakeModule("leaf", false, new ModuleDependency("child", DependencyKind.Required));

        var store = new Dictionary<string, bool>();
        var host = new ModuleHost(
            new[] { parent, child, leaf },
            id => store.GetValueOrDefault(id, false),
            (id, val) => store[id] = val);

        int changeEvents = 0;
        host.Changed += (_, _) => changeEvents++;

        await host.InitializeAsync();

        Assert.False(host.IsActive("leaf"));
        Assert.False(host.IsActive("parent"));

        var preview = host.Preview("leaf", true);
        await host.ApplyAsync(preview);

        Assert.True(host.IsActive("parent"));
        Assert.True(host.IsActive("child"));
        Assert.True(host.IsActive("leaf"));
        Assert.Equal(ModuleStatus.Running, host.StateOf("leaf").Status);
        Assert.True(changeEvents >= 3);
    }

    [Fact]
    public async Task ApplyAsync_FailureDuringStart_ShouldRollbackAndMarkFailed()
    {
        var failing = new FakeModule("failing") { FailStart = true };
        var store = new Dictionary<string, bool>();
        var host = new ModuleHost(
            new[] { failing },
            id => store.GetValueOrDefault(id, false),
            (id, val) => store[id] = val);

        await host.InitializeAsync();
        var preview = host.Preview("failing", true);
        await host.ApplyAsync(preview);

        var state = host.StateOf("failing");
        Assert.Equal(ModuleStatus.Failed, state.Status);
        Assert.Contains("fixture start failure", state.Problem);
        Assert.False(host.IsActive("failing"));
        Assert.Equal(2, failing.Stops);

        // 重试成功
        failing.FailStart = false;
        await host.ApplyAsync(host.Preview("failing", true));
        Assert.True(host.IsActive("failing"));
        Assert.Equal(ModuleStatus.Running, host.StateOf("failing").Status);
    }

    [Fact]
    public async Task Preview_DisablingParent_ShouldCascadeToDisableDependents()
    {
        var parent = new FakeModule("parent", true);
        var child = new FakeModule("child", true, new ModuleDependency("parent", DependencyKind.Required));
        var leaf = new FakeModule("leaf", true, new ModuleDependency("child", DependencyKind.Required));

        var store = new Dictionary<string, bool>
        {
            ["parent"] = true,
            ["child"] = true,
            ["leaf"] = true
        };

        var host = new ModuleHost(
            new[] { parent, child, leaf },
            id => store.GetValueOrDefault(id, false),
            (id, val) => store[id] = val);

        await host.InitializeAsync();

        var preview = host.Preview("parent", false);
        Assert.Contains("child", preview.AlsoDisable);
        Assert.Contains("leaf", preview.AlsoDisable);

        await host.ApplyAsync(preview);

        Assert.False(host.IsActive("parent"));
        Assert.False(host.IsActive("child"));
        Assert.False(host.IsActive("leaf"));
    }
}
