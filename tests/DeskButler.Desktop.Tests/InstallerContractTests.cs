using System.Xml.Linq;

namespace DeskButler.Desktop.Tests;

public sealed class InstallerContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    /// <summary>验证 Release 发布是完整的 win-x64 自包含非单文件布局。</summary>
    [Fact]
    public void Release发布配置生成非单文件自包含WinX64产物()
    {
        var project = XDocument.Load(Path.Combine(
            RepositoryRoot, "src", "DeskButler.Desktop", "DeskButler.Desktop.csproj"));
        var release = project.Root!.Elements("PropertyGroup")
            .Single(group => string.Equals((string?)group.Attribute("Condition"),
                "'$(Configuration)' == 'Release'", StringComparison.Ordinal));

        Assert.Equal("win-x64", release.Element("RuntimeIdentifier")?.Value);
        Assert.Equal("true", release.Element("SelfContained")?.Value);
        Assert.Equal("false", release.Element("PublishSingleFile")?.Value);
        Assert.Equal("true", release.Element("PublishReadyToRun")?.Value);
        Assert.Equal("embedded", release.Element("DebugType")?.Value);
    }

    /// <summary>验证 Release 符号嵌入程序集并映射源码根，避免泄露开发机绝对路径。</summary>
    [Fact]
    public void Release构建统一嵌入符号并映射本机源码路径()
    {
        var props = XDocument.Load(Path.Combine(RepositoryRoot, "Directory.Build.props"));
        var release = props.Root!.Elements("PropertyGroup")
            .Single(group => string.Equals((string?)group.Attribute("Condition"),
                "'$(Configuration)' == 'Release'", StringComparison.Ordinal));

        Assert.Equal("embedded", release.Element("DebugType")?.Value);
        Assert.Equal("true", release.Element("DebugSymbols")?.Value);
        Assert.Equal("$(MSBuildProjectDirectory)=/_/$(MSBuildProjectName)", release.Element("PathMap")?.Value);
    }

    /// <summary>验证安装声明限制为当前用户固定目录和稳定应用身份。</summary>
    [Fact]
    public void 安装脚本限定当前用户固定目录和稳定应用身份()
    {
        var script = ReadInstallerScript();

        Assert.Contains("AppId=DeskButler", script, StringComparison.Ordinal);
        Assert.Contains("DefaultDirName={localappdata}\\Programs\\DeskButler", script, StringComparison.Ordinal);
        Assert.Contains("PrivilegesRequired=lowest", script, StringComparison.Ordinal);
        Assert.Contains("SetupArchitecture=x64", script, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivilegesRequired=admin", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[Services]", script, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>验证卸载只使用固定受控命令并守卫精确用户数据目录。</summary>
    [Fact]
    public void 卸载脚本只调用固定受控命令并以精确数据路径守卫删除()
    {
        var script = ReadInstallerScript();

        Assert.Contains("--prepare-uninstall", script, StringComparison.Ordinal);
        Assert.Contains("RunOnceId: \"PrepareDeskButlerUninstall\"", script, StringComparison.Ordinal);
        Assert.Contains("IsExactUserDataPath", script, StringComparison.Ordinal);
        Assert.Contains("FILE_ATTRIBUTE_REPARSE_POINT", script, StringComparison.Ordinal);
        Assert.Contains("DelTree(UserDataPath, True, True, True)", script, StringComparison.Ordinal);
        Assert.Contains("PrepareApplicationForUninstall", script, StringComparison.Ordinal);
        Assert.Contains("Abort;", script, StringComparison.Ordinal);
        Assert.Contains("RegDeleteValue(HKCU, 'Software\\Microsoft\\Windows\\CurrentVersion\\Run', 'DeskButler')",
            script, StringComparison.Ordinal);
        Assert.DoesNotContain("taskkill", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HKLM", script, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>验证静默卸载准备失败只记录并中止，不创建任何阻塞消息框。</summary>
    [Fact]
    public void 静默卸载准备失败不会显示消息框()
    {
        var script = ReadInstallerScript();

        Assert.Contains("if not IsSilentUninstall then", script, StringComparison.Ordinal);
        Assert.Contains("SuppressibleMsgBox('DeskButler 未能安全退出", script, StringComparison.Ordinal);
        Assert.DoesNotContain("    MsgBox('DeskButler 未能安全退出", script, StringComparison.Ordinal);
    }

    /// <summary>验证发布目录中的调试符号不会被安装到用户机器。</summary>
    [Fact]
    public void 安装文件明确递归排除Pdb()
    {
        var script = ReadInstallerScript();

        Assert.Contains("Excludes: \"*.pdb\"", script, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>验证卸载初始化重入时复用首次数据保留决定。</summary>
    [Fact]
    public void 卸载数据选择只决定一次()
    {
        var script = ReadInstallerScript();

        Assert.Contains("DeleteUserDataDecisionMade: Boolean", script, StringComparison.Ordinal);
        Assert.Contains("if DeleteUserDataDecisionMade then", script, StringComparison.Ordinal);
        Assert.Contains("DeleteUserDataDecisionMade := True", script, StringComparison.Ordinal);
    }

    /// <summary>验证安装后的独立版本标记可用于证明覆盖升级确实替换了内容。</summary>
    [Fact]
    public void 安装完成写入并卸载版本标记()
    {
        var script = ReadInstallerScript();

        Assert.Contains("installed-version.txt", script, StringComparison.Ordinal);
        Assert.Contains("SaveStringToFile", script, StringComparison.Ordinal);
        Assert.Contains("{#AppVersion}", script, StringComparison.Ordinal);
        Assert.Contains("[UninstallDelete]", script, StringComparison.Ordinal);
    }

    /// <summary>验证安装与卸载脚本始终显式使用当前用户注册表 64 位视图。</summary>
    [Fact]
    public void 验证脚本显式使用Registry64且禁止默认视图()
    {
        foreach (var fileName in new[] { "verify-install.ps1", "verify-uninstall.ps1" })
        {
            var script = File.ReadAllText(Path.Combine(RepositoryRoot, "tests", "installer", fileName));

            Assert.Contains("[Microsoft.Win32.RegistryView]::Registry64", script, StringComparison.Ordinal);
            Assert.Contains("[Microsoft.Win32.RegistryHive]::CurrentUser", script, StringComparison.Ordinal);
            Assert.DoesNotContain("[Microsoft.Win32.Registry]::CurrentUser", script, StringComparison.Ordinal);
        }
    }

    /// <summary>读取安装脚本供声明式安全契约测试使用。</summary>
    private static string ReadInstallerScript() =>
        File.ReadAllText(Path.Combine(RepositoryRoot, "installer", "DeskButler.iss"));

    /// <summary>从测试输出目录向上定位包含独立 Git 的 DeskButler 根目录。</summary>
    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, ".git")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new DirectoryNotFoundException("无法定位 DeskButler 独立仓库根目录。");
    }
}
