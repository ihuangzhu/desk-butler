using DeskButler.Core.Capture;
using DeskButler.Core.Scenes;

namespace DeskButler.Modules.WorkspaceRecovery.Tests.Capture;

internal static class CandidateFactory
{
    /// <summary>创建满足普通可见主窗口条件的候选窗口，供过滤规则测试按需覆盖属性。</summary>
    /// <param name="executablePath">候选窗口所属可执行文件路径。</param>
    /// <param name="title">候选窗口标题。</param>
    /// <returns>具有稳定手工测试数据的普通窗口候选项。</returns>
    public static WindowCandidate Normal(string? executablePath = @"C:\Apps\example.exe", string? title = "Example")
    {
        return new WindowCandidate(
            (nint)42,
            100,
            executablePath,
            "ExampleWindowClass",
            title,
            null,
            new WindowBounds(10, 20, 800, 600),
            SceneWindowState.Normal,
            new MonitorIdentity("DISPLAY1", new WindowBounds(0, 0, 1920, 1080), 96, 96),
            true,
            false,
            false,
            false,
            false);
    }
}
