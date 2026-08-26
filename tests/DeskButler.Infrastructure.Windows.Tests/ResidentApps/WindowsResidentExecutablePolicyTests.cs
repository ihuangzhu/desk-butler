using DeskButler.Core.ResidentApps;
using DeskButler.Infrastructure.Windows.ResidentApps;
using System.Reflection;
using System.Text;

namespace DeskButler.Infrastructure.Windows.Tests.ResidentApps;

public sealed class WindowsResidentExecutablePolicyTests
{
    /// <summary>本地固定卷上的普通 asInvoker fixture 必须返回正规化后的最终路径。</summary>
    [WindowsFact]
    public void ValidateAllowsOrdinaryAsInvokerExeOnFixedDrive()
    {
        using var fixture = ResidentFixtureCopy.Create();
        var policy = new WindowsResidentExecutablePolicy();

        var result = policy.Validate(fixture.ExecutablePath);

        Assert.True(result.IsAllowed);
        Assert.Equal(Path.GetFullPath(fixture.ExecutablePath), result.NormalizedPath);
        Assert.Equal(ResidentExecutableRejection.None, result.Reason);
    }

    /// <summary>相对路径不得进入文件系统解析边界。</summary>
    [WindowsFact]
    public void ValidateRejectsRelativePath()
    {
        var policy = new WindowsResidentExecutablePolicy();

        var result = policy.Validate(@"apps\fixture.exe");

        Assert.Equal(ResidentExecutableRejection.NotAbsolutePath, result.Reason);
    }

    /// <summary>含非法字符的路径必须稳定分类为格式无效。</summary>
    [WindowsFact]
    public void ValidateRejectsInvalidPath()
    {
        var policy = CreatePolicy();

        var result = policy.Validate(@"C:\invalid" + '\0' + "path.exe");

        Assert.Equal(ResidentExecutableRejection.InvalidPath, result.Reason);
    }

    /// <summary>源路径卷根之后的冒号表示 ADS，不得伪装成普通 exe。</summary>
    [WindowsTheory]
    [InlineData(":payload.exe")]
    [InlineData(":payload")]
    public void ValidateRejectsAlternateDataStreamInSourcePath(string streamSuffix)
    {
        using var fixture = ResidentFixtureCopy.Create();
        var policy = CreatePolicy();

        var result = policy.Validate(fixture.ExecutablePath + streamSuffix);

        Assert.Equal(ResidentExecutableRejection.InvalidPath, result.Reason);
        Assert.Null(result.NormalizedPath);
    }

    /// <summary>最终解析路径卷根之后出现 ADS 时也必须拒绝，不能只检查用户输入。</summary>
    [WindowsTheory]
    [InlineData(":payload.exe")]
    [InlineData(":payload")]
    public void ValidateRejectsAlternateDataStreamInFinalPath(string streamSuffix)
    {
        using var fixture = ResidentFixtureCopy.Create();
        var policy = CreatePolicy(finalPath: fixture.ExecutablePath + streamSuffix);

        var result = policy.Validate(fixture.ExecutablePath);

        Assert.Equal(ResidentExecutableRejection.InvalidPath, result.Reason);
        Assert.Null(result.NormalizedPath);
    }

    /// <summary>目录即使以 exe 结尾也不是普通可执行文件。</summary>
    [WindowsFact]
    public void ValidateRejectsDirectory()
    {
        using var fixture = ResidentFixtureCopy.Create();
        var directory = Path.Combine(fixture.DirectoryPath, "directory.exe");
        Directory.CreateDirectory(directory);
        var policy = CreatePolicy();

        var result = policy.Validate(directory);

        Assert.Equal(ResidentExecutableRejection.NotExecutableFile, result.Reason);
    }

    /// <summary>非 exe 扩展名不得作为启动入口。</summary>
    [WindowsFact]
    public void ValidateRejectsNonExeFile()
    {
        using var fixture = ResidentFixtureCopy.Create();
        var textFile = Path.Combine(fixture.DirectoryPath, "fixture.txt");
        File.WriteAllText(textFile, "not executable");
        var policy = CreatePolicy();

        var result = policy.Validate(textFile);

        Assert.Equal(ResidentExecutableRejection.NotExecutableFile, result.Reason);
    }

    /// <summary>UNC 输入必须在任何网络访问发生前被拒绝。</summary>
    [WindowsFact]
    public void ValidateRejectsUncPath()
    {
        var policy = CreatePolicy();

        var result = policy.Validate(@"\\server\share\fixture.exe");

        Assert.Equal(ResidentExecutableRejection.NetworkPath, result.Reason);
    }

    /// <summary>不存在的 exe 必须与权限失败区分。</summary>
    [WindowsFact]
    public void ValidateRejectsMissingFile()
    {
        using var fixture = ResidentFixtureCopy.Create();
        var missing = Path.Combine(fixture.DirectoryPath, "missing.exe");
        var policy = new WindowsResidentExecutablePolicy();

        var result = policy.Validate(missing);

        Assert.Equal(ResidentExecutableRejection.FileNotFound, result.Reason);
    }

    /// <summary>网络卷和可移动卷都不得成为自动启动来源。</summary>
    [WindowsTheory]
    [InlineData(DriveType.Network, ResidentExecutableRejection.NonFixedDrive)]
    [InlineData(DriveType.Removable, ResidentExecutableRejection.NonFixedDrive)]
    public void ValidateRejectsUnsafeDriveTypes(DriveType driveType, ResidentExecutableRejection expected)
    {
        using var fixture = ResidentFixtureCopy.Create();
        var policy = CreatePolicy(driveType: driveType);

        var result = policy.Validate(fixture.ExecutablePath);

        Assert.Equal(expected, result.Reason);
    }

    /// <summary>Windows、临时目录和安装器缓存的最终路径必须按目录边界拒绝。</summary>
    [WindowsFact]
    public void ValidateRejectsProhibitedFinalDirectories()
    {
        using var fixture = ResidentFixtureCopy.Create();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var prohibitedPaths = new[]
        {
            Path.Combine(windows, "System32", "fixture.exe"),
            Path.Combine(Path.GetTempPath(), "fixture.exe"),
            Path.Combine(localAppData, "Temp", "fixture.exe"),
            Path.Combine(windows, "Installer", "fixture.exe"),
            Path.Combine(programData, "Package Cache", "fixture.exe")
        };

        foreach (var finalPath in prohibitedPaths)
        {
            var policy = CreatePolicy(finalPath: finalPath);

            var result = policy.Validate(fixture.ExecutablePath);

            Assert.Equal(ResidentExecutableRejection.ProhibitedDirectory, result.Reason);
        }
    }

    /// <summary>禁止目录判断必须使用分隔符边界，不能把 WindowsOld 当成 Windows 子目录。</summary>
    [WindowsFact]
    public void ValidateDoesNotRejectWindowsOldPrefix()
    {
        using var fixture = ResidentFixtureCopy.Create();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var finalPath = Path.Combine(Path.GetDirectoryName(windows)!, "WindowsOld", "fixture.exe");
        var policy = CreatePolicy(finalPath: finalPath);

        var result = policy.Validate(fixture.ExecutablePath);

        Assert.True(result.IsAllowed);
        Assert.Equal(finalPath, result.NormalizedPath);
    }

    /// <summary>路径解析的访问拒绝必须返回稳定分类而不是异常文本。</summary>
    [WindowsFact]
    public void ValidateClassifiesPathAccessFailure()
    {
        using var fixture = ResidentFixtureCopy.Create();
        var resolver = new FakeFinalPathResolver(
            _ => throw new UnauthorizedAccessException("sensitive path"));
        var policy = CreatePolicy(resolver: resolver);

        var result = policy.Validate(fixture.ExecutablePath);

        Assert.Equal(ResidentExecutableRejection.AccessDenied, result.Reason);
        Assert.Null(result.NormalizedPath);
    }

    /// <summary>未分类的策略依赖故障必须 fail-closed。</summary>
    [WindowsFact]
    public void ValidateFailsClosedWhenPolicyDependencyThrows()
    {
        using var fixture = ResidentFixtureCopy.Create();
        var resolver = new FakeFinalPathResolver(
            _ => throw new InvalidOperationException("internal detail"));
        var policy = CreatePolicy(resolver: resolver);

        var result = policy.Validate(fixture.ExecutablePath);

        Assert.Equal(ResidentExecutableRejection.ValidationFailed, result.Reason);
        Assert.Null(result.NormalizedPath);
    }

    /// <summary>reparse 入口跳向非固定卷时必须优先报告最终卷风险。</summary>
    [WindowsFact]
    public void ValidatePrioritizesNonFixedFinalDriveOverReparsePoint()
    {
        using var fixture = ResidentFixtureCopy.Create();
        var policy = CreatePolicy(hasReparsePoint: true, driveType: DriveType.Removable);

        var result = policy.Validate(fixture.ExecutablePath);

        Assert.Equal(ResidentExecutableRejection.NonFixedDrive, result.Reason);
    }

    /// <summary>reparse 入口跳向禁止目录时必须优先报告最终目录风险。</summary>
    [WindowsFact]
    public void ValidatePrioritizesProhibitedFinalDirectoryOverReparsePoint()
    {
        using var fixture = ResidentFixtureCopy.Create();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var policy = CreatePolicy(
            finalPath: Path.Combine(windows, "System32", "fixture.exe"),
            hasReparsePoint: true);

        var result = policy.Validate(fixture.ExecutablePath);

        Assert.Equal(ResidentExecutableRejection.ProhibitedDirectory, result.Reason);
    }

    /// <summary>即使最终目标安全，路径链中的 reparse point 也不属于普通文件入口。</summary>
    [WindowsFact]
    public void ValidateRejectsReparsePointWithOtherwiseSafeTarget()
    {
        using var fixture = ResidentFixtureCopy.Create();
        var policy = CreatePolicy(hasReparsePoint: true);

        var result = policy.Validate(fixture.ExecutablePath);

        Assert.Equal(ResidentExecutableRejection.ReparsePoint, result.Reason);
    }

    /// <summary>两种可能提升的 manifest level 都必须拒绝自动启动。</summary>
    [WindowsTheory]
    [InlineData((int)ExecutableElevationLevel.RequireAdministrator)]
    [InlineData((int)ExecutableElevationLevel.HighestAvailable)]
    public void ValidateRejectsElevationManifest(int levelValue)
    {
        using var fixture = ResidentFixtureCopy.Create();
        var policy = CreatePolicy(elevationLevel: (ExecutableElevationLevel)levelValue);

        var result = policy.Validate(fixture.ExecutablePath);

        Assert.Equal(ResidentExecutableRejection.RequiresElevation, result.Reason);
    }

    /// <summary>manifest 解析或访问不可靠时必须拒绝而不能假定 asInvoker。</summary>
    [WindowsFact]
    public void ValidateRejectsUnreliableManifestInspection()
    {
        using var fixture = ResidentFixtureCopy.Create();
        var policy = CreatePolicy(elevationReliable: false);

        var result = policy.Validate(fixture.ExecutablePath);

        Assert.Equal(ResidentExecutableRejection.ValidationFailed, result.Reason);
    }

    /// <summary>生产 inspector 必须从专属 fixture 的 RT_MANIFEST 读到 asInvoker。</summary>
    [WindowsFact]
    public void ElevationInspectorReadsFixtureAsInvokerManifest()
    {
        using var fixture = ResidentFixtureCopy.Create();
        var inspector = new WindowsExecutableElevationInspector();

        var result = inspector.Inspect(fixture.ExecutablePath);

        Assert.True(result.IsReliable);
        Assert.Equal(ExecutableElevationLevel.AsInvoker, result.Level);
    }

    /// <summary>没有 RT_MANIFEST 的普通 PE 必须按 Windows 默认语义视为 asInvoker。</summary>
    [WindowsFact]
    public void ElevationInspectorTreatsMissingManifestAsInvoker()
    {
        var inspector = new WindowsExecutableElevationInspector();
        var libraryWithoutApplicationManifest = typeof(WindowsResidentExecutablePolicy).Assembly.Location;

        var result = inspector.Inspect(libraryWithoutApplicationManifest);

        Assert.True(result.IsReliable);
        Assert.Equal(ExecutableElevationLevel.AsInvoker, result.Level);
    }

    /// <summary>完整的 asm.v2、v2/v3 混合和 asm.v3 UAC 层级都必须可靠解析。</summary>
    [Fact]
    public void ElevationInspectorAcceptsSupportedManifestNamespaceStructures()
    {
        string[] manifests =
        [
            """
            <assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
              <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
                <security><requestedPrivileges><requestedExecutionLevel level="asInvoker" /></requestedPrivileges></security>
              </trustInfo>
            </assembly>
            """,
            """
            <assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
              <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
                <security><requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3"><requestedExecutionLevel level="asInvoker" /></requestedPrivileges></security>
              </trustInfo>
            </assembly>
            """,
            """
            <assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
              <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
                <security><requestedPrivileges><requestedExecutionLevel level="asInvoker" /></requestedPrivileges></security>
              </trustInfo>
            </assembly>
            """
        ];

        foreach (var manifest in manifests)
        {
            var result = ParseManifest(manifest);

            Assert.True(result.IsReliable);
            Assert.Equal(ExecutableElevationLevel.AsInvoker, result.Level);
        }
    }

    /// <summary>同名节点位于未知 namespace 时不得绕过可靠性检查。</summary>
    [Fact]
    public void ElevationInspectorRejectsRequestedLevelInWrongNamespace()
    {
        const string manifest = """
            <assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
              <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
                <security><requestedPrivileges><requestedExecutionLevel xmlns="urn:example:wrong" level="asInvoker" /></requestedPrivileges></security>
              </trustInfo>
            </assembly>
            """;

        var result = ParseManifest(manifest);

        Assert.False(result.IsReliable);
    }

    /// <summary>requestedExecutionLevel 位于规定层级之外时 manifest 必须不可靠。</summary>
    [Fact]
    public void ElevationInspectorRejectsRequestedLevelAtWrongHierarchy()
    {
        const string manifest = """
            <assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
              <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
                <requestedExecutionLevel level="asInvoker" />
              </trustInfo>
            </assembly>
            """;

        var result = ParseManifest(manifest);

        Assert.False(result.IsReliable);
    }

    /// <summary>RT_MANIFEST 存在但没有 requestedExecutionLevel 时不得套用资源缺失默认值。</summary>
    [Fact]
    public void ElevationInspectorRejectsManifestWithoutRequestedLevelNode()
    {
        const string manifest = """
            <assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
              <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
                <security><requestedPrivileges /></security>
              </trustInfo>
            </assembly>
            """;

        var result = ParseManifest(manifest);

        Assert.False(result.IsReliable);
    }

    /// <summary>重复 requestedExecutionLevel 无法唯一确定 UAC 行为，必须不可靠。</summary>
    [Fact]
    public void ElevationInspectorRejectsDuplicateRequestedLevelNodes()
    {
        const string manifest = """
            <assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
              <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
                <security><requestedPrivileges>
                  <requestedExecutionLevel level="asInvoker" />
                  <requestedExecutionLevel level="requireAdministrator" />
                </requestedPrivileges></security>
              </trustInfo>
            </assembly>
            """;

        var result = ParseManifest(manifest);

        Assert.False(result.IsReliable);
    }

    /// <summary>节点存在但缺少必需 level 属性时不得默认为 asInvoker。</summary>
    [Fact]
    public void ElevationInspectorRejectsRequestedLevelWithoutLevelAttribute()
    {
        const string manifest = """
            <assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
              <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
                <security><requestedPrivileges><requestedExecutionLevel uiAccess="false" /></requestedPrivileges></security>
              </trustInfo>
            </assembly>
            """;

        var result = ParseManifest(manifest);

        Assert.False(result.IsReliable);
    }

    /// <summary>非 schema 枚举值或空 level 属性都必须不可靠。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData(" asInvoker ")]
    public void ElevationInspectorRejectsInvalidRequestedLevelValue(string level)
    {
        var manifest = $"""
            <assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
              <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
                <security><requestedPrivileges><requestedExecutionLevel level="{level}" /></requestedPrivileges></security>
              </trustInfo>
            </assembly>
            """;

        var result = ParseManifest(manifest);

        Assert.False(result.IsReliable);
    }

    /// <summary>通过生产 parser 解析测试提供的 manifest 字节，不绕过 XML 安全设置。</summary>
    private static ExecutableElevationInspection ParseManifest(string manifest)
    {
        var parser = typeof(WindowsExecutableElevationInspector).GetMethod(
            "Parse",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(parser);
        return Assert.IsType<ExecutableElevationInspection>(
            parser.Invoke(null, [Encoding.UTF8.GetBytes(manifest)]));
    }

    /// <summary>构造可独立控制最终路径、卷类型和 manifest 的策略。</summary>
    private static WindowsResidentExecutablePolicy CreatePolicy(
        string? finalPath = null,
        bool hasReparsePoint = false,
        DriveType driveType = DriveType.Fixed,
        ExecutableElevationLevel elevationLevel = ExecutableElevationLevel.AsInvoker,
        bool elevationReliable = true,
        IExecutableFinalPathResolver? resolver = null)
    {
        resolver ??= new FakeFinalPathResolver(
            source => new ExecutableFinalPathResolution(finalPath ?? Path.GetFullPath(source), hasReparsePoint));
        return new WindowsResidentExecutablePolicy(
            resolver,
            new FakeDriveTypeProvider(driveType),
            new FakeElevationInspector(new ExecutableElevationInspection(elevationReliable, elevationLevel)));
    }

    private sealed class FakeFinalPathResolver(
        Func<string, ExecutableFinalPathResolution> resolve) : IExecutableFinalPathResolver
    {
        /// <summary>返回测试指定的最终路径解析结果。</summary>
        public ExecutableFinalPathResolution Resolve(string path) => resolve(path);
    }

    private sealed class FakeDriveTypeProvider(DriveType driveType) : IExecutableDriveTypeProvider
    {
        /// <summary>返回测试指定的卷类型。</summary>
        public DriveType GetDriveType(string path) => driveType;
    }

    private sealed class FakeElevationInspector(ExecutableElevationInspection inspection)
        : IExecutableElevationInspector
    {
        /// <summary>返回测试指定的 manifest 检查结果。</summary>
        public ExecutableElevationInspection Inspect(string path) => inspection;
    }

    private sealed class ResidentFixtureCopy : IDisposable
    {
        /// <summary>记录隔离测试目录及其中的 fixture 路径。</summary>
        private ResidentFixtureCopy(string directoryPath, string executablePath)
        {
            DirectoryPath = directoryPath;
            ExecutablePath = executablePath;
        }

        internal string DirectoryPath { get; }

        internal string ExecutablePath { get; }

        /// <summary>把专属 fixture exe 复制到测试输出下的唯一目录，避开系统临时目录策略。</summary>
        internal static ResidentFixtureCopy Create()
        {
            var source = FindFixtureExecutablePath();
            var directory = Path.Combine(AppContext.BaseDirectory, "ResidentFixtureCopies", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, Path.GetFileName(source));
            File.Copy(source, destination);
            return new ResidentFixtureCopy(directory, destination);
        }

        /// <summary>删除本测试拥有的唯一 fixture 目录。</summary>
        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }

        /// <summary>从仓库根定位与当前测试配置一致的 fixture exe。</summary>
        private static string FindFixtureExecutablePath()
        {
            var output = new DirectoryInfo(AppContext.BaseDirectory);
            var configuration = output.Parent?.Name ?? "Debug";
            var repository = output;
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "DeskButler.slnx")))
            {
                repository = repository.Parent;
            }

            if (repository is null)
            {
                throw new DirectoryNotFoundException("无法从测试输出目录定位 DeskButler 仓库根目录。");
            }

            return Path.Combine(
                repository.FullName,
                "tests",
                "DeskButler.Infrastructure.Windows.Tests",
                "TestApps",
                "DeskButler.ResidentFixture",
                "bin",
                configuration,
                "net10.0-windows10.0.17763.0",
                "DeskButler.ResidentFixture.exe");
        }
    }
}
