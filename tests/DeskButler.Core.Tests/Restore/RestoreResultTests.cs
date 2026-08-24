using DeskButler.Core.Restore;

namespace DeskButler.Core.Tests.Restore;

public sealed class RestoreResultTests
{
    /// <summary>验证重复 SceneItemId 在结果构造边界立即以清晰参数异常拒绝。</summary>
    [Fact]
    public void Constructor拒绝重复SceneItemId()
    {
        var exception = Assert.Throws<ArgumentException>(() => new RestoreResult(
        [
            new RestoreItemResult("duplicate", RestoreItemStatus.Succeeded),
            new RestoreItemResult("duplicate", RestoreItemStatus.Failed)
        ]));

        Assert.Contains("duplicate", exception.Message, StringComparison.Ordinal);
    }
}
