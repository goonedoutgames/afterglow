using Afterglow.Core;
using Xunit;

namespace Afterglow.Core.Tests;

public class AppUpdateAssetsTests
{
    [Fact]
    public void PickWindowsInstallerUrl_prefers_setup_x64()
    {
        var url = AppUpdateAssets.PickWindowsInstallerUrl(
        [
            ("Afterglow-windows-x64.zip", "https://example/zip"),
            ("avn-hub-windows-x64.exe", "https://example/hub"),
            ("Afterglow-Setup-x64.exe", "https://example/setup")
        ]);
        Assert.Equal("https://example/setup", url);
    }

    [Fact]
    public void PickNewestNewerStable_ignores_older_and_equal()
    {
        var tag = AppUpdateAssets.PickNewestNewerStable(
            ["v0.1.20", "v0.1.22", "v0.1.21", "v0.2.0-beta.1"],
            "0.1.21");
        Assert.Equal("v0.1.22", tag);
    }

    [Fact]
    public void PickNewestNewerStable_returns_null_when_current_is_newest()
    {
        Assert.Null(AppUpdateAssets.PickNewestNewerStable(["v0.1.22", "v0.1.21"], "0.1.22"));
    }

    [Fact]
    public void DirectInstallerUrl_prefixes_v()
    {
        Assert.Equal(
            "https://github.com/goonedoutgames/afterglow/releases/download/v0.1.22/Afterglow-Setup-x64.exe",
            AppUpdateAssets.DirectInstallerUrl("0.1.22"));
    }
}
