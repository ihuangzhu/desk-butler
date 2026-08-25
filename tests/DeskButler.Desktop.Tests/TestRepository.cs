namespace DeskButler.Desktop.Tests;

/// <summary>集中提供测试输出目录到 DeskButler 仓库根的定位边界。</summary>
internal static class TestRepository
{
    /// <summary>获取当前测试程序集所属的 DeskButler 仓库根目录。</summary>
    internal static string Root { get; } = FindRoot(new DirectoryInfo(AppContext.BaseDirectory));

    /// <summary>从指定目录向上定位 DeskButler 仓库根，便于验证不同 Git checkout 形态。</summary>
    internal static string FindRoot(DirectoryInfo start)
    {
        ArgumentNullException.ThrowIfNull(start);
        var current = start;
        // 解决方案文件在普通 checkout 与 linked worktree 中形态一致，避免依赖 .git 是目录还是文件。
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "DeskButler.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new DirectoryNotFoundException("无法定位 DeskButler 仓库根目录。");
    }
}
