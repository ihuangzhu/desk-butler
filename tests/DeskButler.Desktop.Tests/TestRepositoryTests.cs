namespace DeskButler.Desktop.Tests;

/// <summary>验证测试仓库根定位兼容不同 Git checkout 形态。</summary>
public sealed class TestRepositoryTests
{
    /// <summary>linked worktree 的 .git 为文件时仍必须以解决方案哨兵定位根目录。</summary>
    [Fact]
    public void SolutionSentinelFindsRootWhenGitMetadataIsFile()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "DeskButler.TestRepository.Tests", Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "tests", "bin", "Debug");
        try
        {
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: C:/worktrees/deskbutler");
            File.WriteAllText(Path.Combine(root, "DeskButler.slnx"), "<Solution />");
            Assert.Equal(root, TestRepository.FindRoot(new DirectoryInfo(nested)));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
