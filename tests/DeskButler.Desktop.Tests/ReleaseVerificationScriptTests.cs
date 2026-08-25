using System.Diagnostics;

namespace DeskButler.Desktop.Tests;

public sealed class ReleaseVerificationScriptTests
{
    private static readonly string RepositoryRoot = TestRepository.Root;

    /// <summary>发布验证必须按既定次序调用工具，并对最终安装器计算 SHA-256。</summary>
    [Fact]
    public async Task 验证脚本按顺序执行完整发布链()
    {
        using var fixture = ReleaseScriptFixture.Create(RepositoryRoot);

        var result = await fixture.RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            [
                "dotnet restore DeskButler.slnx",
                "dotnet build DeskButler.slnx -c Release --no-restore",
                "dotnet test DeskButler.slnx -c Release --no-build",
                "dotnet publish src\\DeskButler.Desktop -c Release -r win-x64 --self-contained true -o artifacts\\publish\\win-x64.staging",
                "installer",
                "certutil -hashfile \"" + Path.Combine(fixture.Root, "artifacts", "installer", "DeskButler-Setup-0.1.0.exe") + "\" SHA256"
            ],
            fixture.ReadTrace());
        Assert.True(File.Exists(Path.Combine(fixture.Root, "artifacts", "publish", "win-x64", "DeskButler.Desktop.exe")));
    }

    /// <summary>任一构建步骤失败后，发布验证必须立即停止且不得继续生成安装器。</summary>
    [Fact]
    public async Task 验证脚本在首个失败处停止()
    {
        using var fixture = ReleaseScriptFixture.Create(RepositoryRoot, failDotnetCall: 2);

        var result = await fixture.RunAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(
            [
                "dotnet restore DeskButler.slnx",
                "dotnet build DeskButler.slnx -c Release --no-restore"
            ],
            fixture.ReadTrace());
        Assert.False(File.Exists(Path.Combine(fixture.Root, "artifacts", "installer", "DeskButler-Setup-0.1.0.exe")));
    }

    private sealed class ReleaseScriptFixture : IDisposable
    {
        private readonly string tracePath;

        private ReleaseScriptFixture(string root)
        {
            Root = root;
            tracePath = Path.Combine(root, "trace.txt");
        }

        public string Root { get; }

        /// <summary>复制被测脚本，并创建只记录调用和最小产物的本地假工具。</summary>
        public static ReleaseScriptFixture Create(string repositoryRoot, int? failDotnetCall = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "DeskButler.ReleaseScript.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "scripts"));
            Directory.CreateDirectory(Path.Combine(root, "installer"));
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            File.Copy(Path.Combine(repositoryRoot, "scripts", "verify-release.cmd"),
                Path.Combine(root, "scripts", "verify-release.cmd"));

            File.WriteAllText(Path.Combine(root, "fake-dotnet.cmd"), $$"""
                @echo off
                setlocal EnableExtensions DisableDelayedExpansion
                >>"{{Path.Combine(root, "trace.txt")}}" echo dotnet %*
                set /a COUNT=0
                if exist "{{Path.Combine(root, "count.txt")}}" set /p COUNT=<"{{Path.Combine(root, "count.txt")}}"
                set /a COUNT+=1
                >"{{Path.Combine(root, "count.txt")}}" echo %COUNT%
                if "{{failDotnetCall?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0"}}"=="%COUNT%" exit /b 23
                if /i "%1"=="publish" (
                  mkdir "{{Path.Combine(root, "artifacts", "publish", "win-x64.staging")}}"
                  >"{{Path.Combine(root, "artifacts", "publish", "win-x64.staging", "DeskButler.Desktop.exe")}}" echo fixture
                )
                exit /b 0
                """);
            File.WriteAllText(Path.Combine(root, "installer", "build-installer.cmd"), $$"""
                @echo off
                >>"{{Path.Combine(root, "trace.txt")}}" echo installer
                mkdir "{{Path.Combine(root, "artifacts", "installer")}}"
                >"{{Path.Combine(root, "artifacts", "installer", "DeskButler-Setup-0.1.0.exe")}}" echo installer
                exit /b 0
                """);
            File.WriteAllText(Path.Combine(root, "fake-certutil.cmd"), $$"""
                @echo off
                >>"{{Path.Combine(root, "trace.txt")}}" echo certutil %*
                echo ABCDEF
                exit /b 0
                """);
            return new ReleaseScriptFixture(root);
        }

        /// <summary>以注入的假工具执行真实发布验证脚本并返回退出结果。</summary>
        public async Task<ProcessResult> RunAsync()
        {
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Root
            };
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(Path.Combine(Root, "scripts", "verify-release.cmd"));
            startInfo.Environment["DOTNET_CMD"] = Path.Combine(Root, "fake-dotnet.cmd");
            startInfo.Environment["CERTUTIL_CMD"] = Path.Combine(Root, "fake-certutil.cmd");
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动发布验证脚本。");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
        }

        /// <summary>读取假工具按调用顺序落下的可观察轨迹。</summary>
        public string[] ReadTrace() => File.Exists(tracePath)
            ? File.ReadAllLines(tracePath)
            : [];

        /// <summary>仅删除本测试创建的唯一临时目录。</summary>
        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
