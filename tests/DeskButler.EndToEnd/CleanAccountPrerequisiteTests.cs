using System.Diagnostics;
using System.Text.Json;

namespace DeskButler.EndToEnd;

public sealed class CleanAccountPrerequisiteTests
{
    /// <summary>运行只读先决条件脚本，并要求真实数据存在时明确 BLOCK 而不是继续安装。</summary>
    [WindowsFact]
    public async Task 未确认专用账户或已有真实数据时门禁明确阻止执行()
    {
        var script = Path.Combine(FindRepositoryRoot(), "tests", "manual", "verify-clean-account-prerequisites.ps1");
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动只读先决条件脚本。");
        var standardOutput = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var standardError = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.True(process.ExitCode is 0 or 2, standardError);
        using var document = JsonDocument.Parse(standardOutput);
        var root = document.RootElement;
        Assert.Equal(
            "READ_ONLY_NO_ACCOUNT_CREATE_NO_FEATURE_ENABLE_NO_SANDBOX_START_NO_DELETE",
            root.GetProperty("safety").GetString());
        Assert.Equal("BLOCK", root.GetProperty("status").GetString());
        Assert.NotEmpty(root.GetProperty("blockers").EnumerateArray());
        Assert.Equal(2, process.ExitCode);
    }

    /// <summary>从测试输出向上定位唯一 DeskButler.slnx。</summary>
    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "DeskButler.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("无法定位 DeskButler 仓库根。");
    }
}
