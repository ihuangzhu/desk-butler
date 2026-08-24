namespace DeskButler.Desktop.Tests;

public sealed class PrepareUninstallOptionsTests
{
    /// <summary>验证卸载准备参数大小写不敏感且必须是唯一参数。</summary>
    [Theory]
    [InlineData("--prepare-uninstall")]
    [InlineData("--PREPARE-UNINSTALL")]
    public void IsPrepareUninstallRequest识别唯一受控卸载命令(string argument)
    {
        Assert.True(App.IsPrepareUninstallRequest([argument]));
    }

    /// <summary>验证附带额外参数时不进入受控卸载路径。</summary>
    [Fact]
    public void IsPrepareUninstallRequest附带任意参数时拒绝执行()
    {
        Assert.False(App.IsPrepareUninstallRequest(["--prepare-uninstall", "not-a-command"]));
    }

    /// <summary>验证升级准备命令的严格参数边界。</summary>
    [Fact]
    public void IsPrepareUpgradeRequest识别唯一受控升级命令()
    {
        Assert.True(App.IsPrepareUpgradeRequest(["--prepare-upgrade"]));
        Assert.False(App.IsPrepareUpgradeRequest(["--prepare-upgrade", "extra"]));
    }

    /// <summary>验证隐藏维护流程失败时不会弹出阻塞对话框。</summary>
    [Fact]
    public void ShouldShowStartupFailure受控维护命令失败时不弹阻塞对话框()
    {
        Assert.False(App.ShouldShowStartupFailure(["--prepare-uninstall"], isSmokeRequest: false));
        Assert.False(App.ShouldShowStartupFailure(["--prepare-upgrade"], isSmokeRequest: false));
        Assert.True(App.ShouldShowStartupFailure([], isSmokeRequest: false));
    }
}
