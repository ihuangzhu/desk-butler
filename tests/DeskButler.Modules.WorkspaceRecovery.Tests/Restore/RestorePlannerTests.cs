using DeskButler.Core.Capture;
using DeskButler.Core.Diagnostics;
using DeskButler.Core.Restore;
using DeskButler.Core.Scenes;
using DeskButler.Modules.WorkspaceRecovery.Restore;

namespace DeskButler.Modules.WorkspaceRecovery.Tests.Restore;

public sealed class RestorePlannerTests
{
    /// <summary>验证同一程序存在多个无法区分的当前窗口时不会重复启动。</summary>
    [Fact]
    public void BuildAmbiguousExistingWindowsDoesNotLaunchDuplicate()
    {
        var scene = SceneWith(App("tool", @"C:\Apps\tool.exe"));
        var current = new[]
        {
            Window(101, @"C:\Apps\tool.exe", title: "one"),
            Window(102, @"C:\Apps\tool.exe", title: "two")
        };

        var plan = PlannerWithExistingPaths(@"C:\Apps\tool.exe")
            .Build(scene, current, FailureHistory.Empty, safeMode: false);

        Assert.Equal(RestoreDisposition.SkipAmbiguous, Assert.Single(plan.Items).Disposition);
    }

    /// <summary>验证资源管理器目录在大小写、冗余段和尾分隔符不同的情况下仍按最高优先级复用。</summary>
    [Fact]
    public void BuildReusesExactNormalizedExplorerPathBeforeAppIdentity()
    {
        var scene = SceneWith(App("folder", @"C:\Windows\explorer.exe", "CabinetWClass", "Folder", @"C:\Work\Project\"));
        var explorerMatch = Window(201, @"C:\Other\host.exe", "OtherClass", "Other", @"c:\work\.\PROJECT");
        var appIdentityMatch = Window(202, @"c:\windows\EXPLORER.exe", "CabinetWClass", " folder ", @"C:\Elsewhere");

        var item = Assert.Single(PlannerWithExistingPaths(@"C:\Work\Project")
            .Build(scene, [explorerMatch, appIdentityMatch], FailureHistory.Empty, safeMode: false).Items);

        Assert.Equal(RestoreDisposition.Reuse, item.Disposition);
        Assert.Equal((nint)201, item.TargetWindowHandle);
    }

    /// <summary>验证可执行文件路径、类名和受限标题均匹配时复用当前窗口。</summary>
    [Fact]
    public void BuildReusesNormalizedExecutableClassAndExactConstrainedTitle()
    {
        var scene = SceneWith(App("notes", @"C:\Apps\Editor\editor.exe", "EditorClass", " Project Notes "));
        var current = Window(301, @"c:\apps\editor\.\EDITOR.EXE", "EditorClass", "project notes");

        var item = Assert.Single(PlannerWithExistingPaths(@"C:\Apps\Editor\editor.exe")
            .Build(scene, [current], FailureHistory.Empty, safeMode: false).Items);

        Assert.Equal(RestoreDisposition.Reuse, item.Disposition);
        Assert.Equal((nint)301, item.TargetWindowHandle);
    }

    /// <summary>验证标题提示不会以子串方式宽松匹配多个当前窗口。</summary>
    [Fact]
    public void BuildDoesNotUseSubstringTitleMatching()
    {
        var scene = SceneWith(App("notes", @"C:\Apps\editor.exe", "EditorClass", "Notes"));
        var current = new[]
        {
            Window(311, @"C:\Apps\editor.exe", "EditorClass", "Notes - customer A"),
            Window(312, @"C:\Apps\editor.exe", "EditorClass", "Notes - customer B")
        };

        var item = Assert.Single(PlannerWithExistingPaths(@"C:\Apps\editor.exe")
            .Build(scene, current, FailureHistory.Empty, safeMode: false).Items);

        Assert.Equal(RestoreDisposition.SkipAmbiguous, item.Disposition);
        Assert.Null(item.TargetWindowHandle);
    }

    /// <summary>验证空标题提示不会被当成能够匹配任意标题的条件。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildDoesNotUseMissingTitleAsWildcard(string? titleHint)
    {
        var scene = SceneWith(App("notes", @"C:\Apps\editor.exe", "EditorClass", titleHint));
        var current = new[]
        {
            Window(321, @"C:\Apps\editor.exe", "EditorClass", "one"),
            Window(322, @"C:\Apps\editor.exe", "EditorClass", "two")
        };

        var item = Assert.Single(PlannerWithExistingPaths(@"C:\Apps\editor.exe")
            .Build(scene, current, FailureHistory.Empty, safeMode: false).Items);

        Assert.Equal(RestoreDisposition.SkipAmbiguous, item.Disposition);
    }

    /// <summary>验证标题变化时仅在同一 exe 的场景项和当前窗口均唯一时安全回退。</summary>
    [Fact]
    public void BuildFallsBackToExecutableWhenSceneAndCurrentAreBothUnique()
    {
        var scene = SceneWith(App("notes", @"C:\Apps\editor.exe", "OldClass", "Old title"));
        var current = Window(331, @"c:\apps\.\EDITOR.EXE", "NewClass", "New title");

        var item = Assert.Single(PlannerWithExistingPaths(@"C:\Apps\editor.exe")
            .Build(scene, [current], FailureHistory.Empty, safeMode: false).Items);

        Assert.Equal(RestoreDisposition.Reuse, item.Disposition);
        Assert.Equal((nint)331, item.TargetWindowHandle);
    }

    /// <summary>验证同 exe 有多个场景项时不会把唯一当前窗口复用给多个计划项。</summary>
    [Fact]
    public void BuildConsumesCurrentCandidateAtMostOnceForDuplicateSceneItems()
    {
        var scene = SceneWith(
            App("one", @"C:\Apps\editor.exe", "EditorClass", null),
            App("two", @"c:\apps\.\EDITOR.EXE", "EditorClass", null));
        var current = Window(341, @"C:\Apps\editor.exe", "EditorClass", "Current");

        var items = PlannerWithExistingPaths(@"C:\Apps\editor.exe")
            .Build(scene, [current], FailureHistory.Empty, safeMode: false).Items;

        Assert.Equal(2, items.Length);
        Assert.All(items, item => Assert.Equal(RestoreDisposition.SkipAmbiguous, item.Disposition));
        Assert.DoesNotContain(items, item => item.TargetWindowHandle is not null);
    }

    /// <summary>验证输入中重复的同一窗口句柄仍只表示一个当前窗口。</summary>
    [Fact]
    public void BuildTreatsDuplicateCandidateHandleAsOneCurrentWindow()
    {
        var scene = SceneWith(App("notes", @"C:\Apps\editor.exe", "EditorClass", null));
        var candidate = Window(351, @"C:\Apps\editor.exe", "EditorClass", "Current");

        var item = Assert.Single(PlannerWithExistingPaths(@"C:\Apps\editor.exe")
            .Build(scene, [candidate, candidate with { ProcessId = 999 }], FailureHistory.Empty, safeMode: false).Items);

        Assert.Equal(RestoreDisposition.Reuse, item.Disposition);
        Assert.Equal((nint)351, item.TargetWindowHandle);
    }

    /// <summary>验证同一 HWND 的重复观测会保守合并权限风险，且不依赖输入顺序。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildMergesUnsafeFlagForSameHandleIdentityRegardlessOfOrder(bool unsafeFirst)
    {
        var scene = SceneWith(App("notes", @"C:\Apps\editor.exe", "EditorClass", "Notes"));
        var normal = Window(352, @"C:\Apps\editor.exe", "EditorClass", "Notes");
        var inaccessible = normal with
        {
            ProcessId = 999,
            WasElevatedOrInaccessible = true
        };
        WindowCandidate[] current = unsafeFirst ? [inaccessible, normal] : [normal, inaccessible];

        var item = Assert.Single(PlannerWithExistingPaths(@"C:\Apps\editor.exe")
            .Build(scene, current, FailureHistory.Empty, safeMode: false).Items);

        Assert.Equal(RestoreDisposition.SkipUnsafe, item.Disposition);
        Assert.Null(item.TargetWindowHandle);
    }

    /// <summary>验证同一 HWND 的冲突身份别名均保留为不安全相关性，且不会复用或重复启动。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildTreatsConflictingAliasesForSameHandleAsUnsafeRegardlessOfOrder(bool secondAliasFirst)
    {
        var scene = SceneWith(
            App("alpha", @"C:\Apps\alpha.exe", "AlphaClass", "Alpha", @"C:\Work\Alpha"),
            App("beta", @"C:\Apps\beta.exe", "BetaClass", "Beta", @"C:\Work\Beta"));
        var alpha = Window(353, @"C:\Apps\alpha.exe", "AlphaClass", "Alpha", @"C:\Work\Alpha");
        var beta = Window(353, @"C:\Apps\beta.exe", "BetaClass", "Beta", @"C:\Work\Beta") with
        {
            ProcessId = 999
        };
        WindowCandidate[] current = secondAliasFirst ? [beta, alpha] : [alpha, beta];

        var items = PlannerWithExistingPaths(
                @"C:\Apps\alpha.exe", @"C:\Apps\beta.exe", @"C:\Work\Alpha", @"C:\Work\Beta")
            .Build(scene, current, FailureHistory.Empty, safeMode: false).Items;

        Assert.Equal(2, items.Length);
        Assert.All(items, item => Assert.Equal(RestoreDisposition.SkipUnsafe, item.Disposition));
        Assert.DoesNotContain(items, item => item.TargetWindowHandle is not null);
    }

    /// <summary>验证 PID 变化不参与已存在窗口的持久身份匹配。</summary>
    [Fact]
    public void BuildDoesNotUseProcessIdAsPersistedIdentity()
    {
        var scene = SceneWith(App("notes", @"C:\Apps\editor.exe", "EditorClass", "Notes"));
        var current = Window(361, @"C:\Apps\editor.exe", "EditorClass", "Notes") with { ProcessId = int.MaxValue };

        var item = Assert.Single(PlannerWithExistingPaths(@"C:\Apps\editor.exe")
            .Build(scene, [current], FailureHistory.Empty, safeMode: false).Items);

        Assert.Equal(RestoreDisposition.Reuse, item.Disposition);
        Assert.Equal((nint)361, item.TargetWindowHandle);
    }

    /// <summary>验证没有当前窗口且启动路径存在时生成启动项。</summary>
    [Fact]
    public void BuildLaunchesMissingWindowWhenExecutableExists()
    {
        var scene = SceneWith(App("tool", @"C:\Apps\tool.exe"));

        var item = Assert.Single(PlannerWithExistingPaths(@"C:\Apps\tool.exe")
            .Build(scene, [], FailureHistory.Empty, safeMode: false).Items);

        Assert.Equal(RestoreDisposition.Launch, item.Disposition);
        Assert.Null(item.TargetWindowHandle);
    }

    /// <summary>验证没有当前窗口且可执行文件不存在时标记缺失路径。</summary>
    [Fact]
    public void BuildMarksMissingExecutablePathWithoutUsingRealFileSystem()
    {
        var scene = SceneWith(App("gone", @"C:\Removed\gone.exe"));

        var item = Assert.Single(PlannerWithExistingPaths()
            .Build(scene, [], FailureHistory.Empty, safeMode: false).Items);

        Assert.Equal(RestoreDisposition.MissingPath, item.Disposition);
    }

    /// <summary>验证资源管理器目录存在时可以生成启动项。</summary>
    [Fact]
    public void BuildLaunchesMissingExplorerWindowWhenDirectoryExists()
    {
        var scene = SceneWith(App("folder", @"C:\Windows\explorer.exe", explorerPath: @"C:\Work\Project\"));

        var item = Assert.Single(PlannerWithExistingPaths(@"c:\work\project")
            .Build(scene, [], FailureHistory.Empty, safeMode: false).Items);

        Assert.Equal(RestoreDisposition.Launch, item.Disposition);
    }

    /// <summary>验证默认文件系统适配器会把真实存在的目录识别为 Explorer 启动路径。</summary>
    [Fact]
    public void BuildDefaultPathPredicateRecognizesExistingExplorerDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"DeskButler-Restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var scene = SceneWith(App("folder", @"C:\Windows\explorer.exe", explorerPath: directory));

            var item = Assert.Single(new RestorePlanner()
                .Build(scene, [], FailureHistory.Empty, safeMode: false).Items);

            Assert.Equal(RestoreDisposition.Launch, item.Disposition);
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    /// <summary>验证资源管理器目录不存在时标记缺失路径而不是尝试启动。</summary>
    [Fact]
    public void BuildMarksMissingExplorerDirectory()
    {
        var scene = SceneWith(App("folder", @"C:\Windows\explorer.exe", explorerPath: @"C:\Removed"));

        var item = Assert.Single(PlannerWithExistingPaths(@"C:\Windows\explorer.exe")
            .Build(scene, [], FailureHistory.Empty, safeMode: false).Items);

        Assert.Equal(RestoreDisposition.MissingPath, item.Disposition);
    }

    /// <summary>验证已存在的可靠窗口即使启动路径后来丢失也仍可复用。</summary>
    [Fact]
    public void BuildReusesExistingWindowBeforeCheckingLaunchPath()
    {
        var scene = SceneWith(App("gone", @"C:\Removed\gone.exe", "ToolClass", "Tool"));
        var current = Window(401, @"C:\Removed\gone.exe", "ToolClass", "Tool");

        var item = Assert.Single(PlannerWithExistingPaths()
            .Build(scene, [current], FailureHistory.Empty, safeMode: false).Items);

        Assert.Equal(RestoreDisposition.Reuse, item.Disposition);
    }

    /// <summary>验证提升权限的场景项始终默认跳过。</summary>
    [Fact]
    public void BuildSkipsElevatedSceneItemAsUnsafe()
    {
        var scene = SceneWith(App("admin", @"C:\Apps\admin.exe") with { WasElevated = true });

        var item = Assert.Single(PlannerWithExistingPaths(@"C:\Apps\admin.exe")
            .Build(scene, [], FailureHistory.Empty, safeMode: false).Items);

        Assert.Equal(RestoreDisposition.SkipUnsafe, item.Disposition);
    }

    /// <summary>验证可靠身份指向提升权限或不可访问窗口时既不移动也不重复启动。</summary>
    [Fact]
    public void BuildSkipsElevatedOrInaccessibleCurrentWindowAsUnsafe()
    {
        var scene = SceneWith(App("admin", @"C:\Apps\admin.exe", "AdminClass", "Admin"));
        var current = Window(411, @"C:\Apps\admin.exe", "AdminClass", "Admin") with
        {
            WasElevatedOrInaccessible = true
        };

        var item = Assert.Single(PlannerWithExistingPaths(@"C:\Apps\admin.exe")
            .Build(scene, [current], FailureHistory.Empty, safeMode: false).Items);

        Assert.Equal(RestoreDisposition.SkipUnsafe, item.Disposition);
        Assert.Null(item.TargetWindowHandle);
    }

    /// <summary>验证连续失败达到三次的项目默认跳过。</summary>
    [Fact]
    public void BuildSkipsItemAfterThreeConsecutiveFailures()
    {
        var scene = SceneWith(App("flaky", @"C:\Apps\flaky.exe"));
        var history = new FailureHistory(new Dictionary<string, int> { ["flaky"] = 3 });

        var item = Assert.Single(PlannerWithExistingPaths(@"C:\Apps\flaky.exe")
            .Build(scene, [], history, safeMode: false).Items);

        Assert.Equal(RestoreDisposition.SkipUnsafe, item.Disposition);
    }

    /// <summary>验证失败少于三次仍允许正常规划。</summary>
    [Fact]
    public void BuildDoesNotSkipItemBeforeThirdConsecutiveFailure()
    {
        var scene = SceneWith(App("flaky", @"C:\Apps\flaky.exe"));
        var history = new FailureHistory(new Dictionary<string, int> { ["flaky"] = 2 });

        var item = Assert.Single(PlannerWithExistingPaths(@"C:\Apps\flaky.exe")
            .Build(scene, [], history, safeMode: false).Items);

        Assert.Equal(RestoreDisposition.Launch, item.Disposition);
    }

    /// <summary>验证安全模式允许可靠复用普通现有窗口。</summary>
    [Fact]
    public void BuildSafeModeStillReusesReliablyMatchedNormalWindow()
    {
        var scene = SceneWith(App("notes", @"C:\Apps\editor.exe", "EditorClass", "Notes"));
        var current = Window(421, @"C:\Apps\editor.exe", "EditorClass", "Notes");

        var item = Assert.Single(PlannerWithExistingPaths(@"C:\Apps\editor.exe")
            .Build(scene, [current], FailureHistory.Empty, safeMode: true).Items);

        Assert.Equal(RestoreDisposition.Reuse, item.Disposition);
    }

    /// <summary>验证安全模式默认不启动缺失的普通程序。</summary>
    [Fact]
    public void BuildSafeModeSkipsLaunchingOrdinaryApplication()
    {
        var scene = SceneWith(App("tool", @"C:\Apps\tool.exe"));

        var item = Assert.Single(PlannerWithExistingPaths(@"C:\Apps\tool.exe")
            .Build(scene, [], FailureHistory.Empty, safeMode: true).Items);

        Assert.Equal(RestoreDisposition.SkipUnsafe, item.Disposition);
    }

    /// <summary>验证安全模式仍允许恢复明确存在的资源管理器目录。</summary>
    [Fact]
    public void BuildSafeModeAllowsExistingExplorerDirectory()
    {
        var scene = SceneWith(App("folder", @"C:\Windows\explorer.exe", explorerPath: @"C:\Work"));

        var item = Assert.Single(PlannerWithExistingPaths(@"C:\Work")
            .Build(scene, [], FailureHistory.Empty, safeMode: true).Items);

        Assert.Equal(RestoreDisposition.Launch, item.Disposition);
    }

    /// <summary>验证畸形或相对启动路径只影响对应项目，不会让整份计划失败。</summary>
    [Theory]
    [InlineData("bad\0path.exe")]
    [InlineData("..\\relative.exe")]
    [InlineData("relative.exe")]
    public void BuildContainsInvalidExecutablePathAsMissingPath(string invalidPath)
    {
        var scene = SceneWith(
            App("bad", invalidPath),
            App("good", @"C:\Apps\good.exe"));

        var items = PlannerWithExistingPaths(@"C:\Apps\good.exe")
            .Build(scene, [], FailureHistory.Empty, safeMode: false).Items;

        Assert.Equal(RestoreDisposition.MissingPath, items[0].Disposition);
        Assert.Equal(RestoreDisposition.Launch, items[1].Disposition);
    }

    /// <summary>验证畸形或相对资源管理器目录只使该目录不可恢复。</summary>
    [Theory]
    [InlineData("bad\0folder")]
    [InlineData("..\\relative-folder")]
    public void BuildContainsInvalidExplorerPathAsMissingPath(string invalidPath)
    {
        var scene = SceneWith(App("folder", @"C:\Windows\explorer.exe", explorerPath: invalidPath));

        var item = Assert.Single(PlannerWithExistingPaths(@"C:\Windows\explorer.exe")
            .Build(scene, [], FailureHistory.Empty, safeMode: false).Items);

        Assert.Equal(RestoreDisposition.MissingPath, item.Disposition);
    }

    /// <summary>验证路径存在谓词抛出的普通异常只使对应项目缺失，其他项目继续规划。</summary>
    [Fact]
    public void BuildContainsOrdinaryPathPredicateExceptionAsMissingPath()
    {
        var failingPath = NormalizeTestPath(@"C:\Apps\bad.exe");
        var scene = SceneWith(
            App("bad", @"C:\Apps\bad.exe"),
            App("good", @"C:\Apps\good.exe"));
        var planner = new RestorePlanner(path =>
            StringComparer.OrdinalIgnoreCase.Equals(path, failingPath)
                ? throw new InvalidOperationException("test path provider failure")
                : true);

        var items = planner.Build(scene, [], FailureHistory.Empty, safeMode: false).Items;

        Assert.Equal(RestoreDisposition.MissingPath, items[0].Disposition);
        Assert.Equal(RestoreDisposition.Launch, items[1].Disposition);
    }

    /// <summary>验证路径存在谓词的取消和致命异常不会被规划器降级为路径缺失。</summary>
    [Fact]
    public void BuildPropagatesCancellationAndFatalPathPredicateExceptions()
    {
        var scene = SceneWith(App("tool", @"C:\Apps\tool.exe"));
#pragma warning disable CA2201 // 此处有意构造运行时保留异常，以验证规划器不会吞掉致命故障。
        Exception[] exceptions =
        [
            new OperationCanceledException("test cancellation"),
            new OutOfMemoryException("test out of memory"),
            new AccessViolationException("test access violation"),
            new StackOverflowException("test stack overflow")
        ];
#pragma warning restore CA2201

        foreach (var expected in exceptions)
        {
            var actual = Assert.Throws(expected.GetType(), () => new RestorePlanner(_ => throw expected)
                .Build(scene, [], FailureHistory.Empty, safeMode: false));

            Assert.Same(expected, actual);
        }
    }

    /// <summary>验证与场景无关的当前程序不会进入计划，计划也不会产生关闭操作。</summary>
    [Fact]
    public void BuildOmitsUnrelatedCurrentProgramsAndNeverAddsCloseOperation()
    {
        var scene = SceneWith(App("wanted", @"C:\Apps\wanted.exe"));
        var unrelated = Window(501, @"C:\Apps\unrelated.exe", "OtherClass", "Other");

        var plan = PlannerWithExistingPaths(@"C:\Apps\wanted.exe")
            .Build(scene, [unrelated], FailureHistory.Empty, safeMode: false);

        var item = Assert.Single(plan.Items);
        Assert.Equal("wanted", item.SceneItem.Id);
        Assert.Equal(RestoreDisposition.Launch, item.Disposition);
    }

    /// <summary>验证计划会复制项目集合，调用方后续修改源集合不会改变计划。</summary>
    [Fact]
    public void RestorePlanCopiesInputItems()
    {
        var source = new List<RestorePlanItem>
        {
            new(App("one", @"C:\Apps\one.exe"), RestoreDisposition.Launch, null)
        };
        var plan = new RestorePlan(source);

        source.Add(new RestorePlanItem(App("two", @"C:\Apps\two.exe"), RestoreDisposition.Launch, null));

        Assert.Single(plan.Items);
    }

    /// <summary>验证失败历史会复制输入字典，避免外部修改改变规划依据。</summary>
    [Fact]
    public void FailureHistoryCopiesInputDictionary()
    {
        var counts = new Dictionary<string, int> { ["item"] = 2 };
        var history = new FailureHistory(counts);

        counts["item"] = 9;

        Assert.Equal(2, history.CountFor("item"));
        Assert.Equal(0, history.CountFor("unknown"));
    }

    /// <summary>创建使用确定性路径存在集合的规划器，避免测试依赖真实用户文件系统。</summary>
    /// <param name="paths">测试中视为存在的绝对 Windows 路径。</param>
    /// <returns>使用内存路径谓词的恢复规划器。</returns>
    private static RestorePlanner PlannerWithExistingPaths(params string[] paths)
    {
        var existing = new HashSet<string>(paths.Select(NormalizeTestPath), StringComparer.OrdinalIgnoreCase);
        return new RestorePlanner(path => existing.Contains(path));
    }

    /// <summary>按测试手工约定规范化绝对 Windows 路径。</summary>
    /// <param name="path">测试路径。</param>
    /// <returns>去除冗余段和尾分隔符的路径。</returns>
    private static string NormalizeTestPath(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    /// <summary>创建包含指定条目的固定场景快照。</summary>
    /// <param name="items">场景条目。</param>
    /// <returns>确定性场景快照。</returns>
    private static SceneSnapshot SceneWith(params SceneItem[] items)
    {
        return new SceneSnapshot(Guid.Parse("11111111-1111-1111-1111-111111111111"), 1,
            DateTimeOffset.UnixEpoch, "test", items);
    }

    /// <summary>创建具有固定窗口布局的场景程序条目。</summary>
    private static SceneItem App(
        string id,
        string executablePath,
        string windowClass = "ToolClass",
        string? titleHint = null,
        string? explorerPath = null)
    {
        return new SceneItem(id, executablePath, windowClass, titleHint, explorerPath,
            new WindowBounds(10, 20, 800, 600), SceneWindowState.Normal,
            new MonitorIdentity("DISPLAY1", new WindowBounds(0, 0, 1920, 1080), 96, 96), false);
    }

    /// <summary>创建具有固定安全属性的当前窗口候选。</summary>
    private static WindowCandidate Window(
        long handle,
        string? executablePath,
        string windowClass = "ToolClass",
        string? title = null,
        string? explorerPath = null)
    {
        return new WindowCandidate((nint)handle, 100, executablePath, windowClass, title, explorerPath,
            new WindowBounds(10, 20, 800, 600), SceneWindowState.Normal,
            new MonitorIdentity("DISPLAY1", new WindowBounds(0, 0, 1920, 1080), 96, 96),
            true, false, false, false, false);
    }
}
