using System.Linq;
using System.Threading.Tasks;
using Pankin.Core.Setup;
using Xunit;

namespace Pankin.Core.Tests.Setup;

public class DependencyCheckerTests
{
    [Fact]
    public async Task CheckDependencies_WhenToolsFound_ShouldReportInstalled()
    {
        var checker = new LinuxDependencyChecker(
            executableResolver: name => $"/usr/bin/{name}",
            libraryResolver: lib => true);

        var status = await checker.CheckDependenciesAsync();
        Assert.Equal(4, status.Count);

        var plocate = status.First(s => s.Name == "plocate");
        Assert.True(plocate.IsInstalled);
        Assert.Equal("/usr/bin/plocate", plocate.DetectedPath);
        Assert.Equal("sudo apt install plocate", plocate.InstallCommand);

        var wl = status.First(s => s.Name == "wl-clipboard");
        Assert.True(wl.IsInstalled);

        var xclip = status.First(s => s.Name == "xclip");
        Assert.True(xclip.IsInstalled);

        var ayatana = status.First(s => s.Name == "libayatana-appindicator");
        Assert.True(ayatana.IsInstalled);
    }

    [Fact]
    public async Task CheckDependencies_WhenToolsMissing_ShouldReportNotInstalled()
    {
        var checker = new LinuxDependencyChecker(
            executableResolver: name => null,
            libraryResolver: lib => false);

        var status = await checker.CheckDependenciesAsync();
        Assert.Equal(4, status.Count);
        Assert.All(status, s => Assert.False(s.IsInstalled));
    }
}
