namespace DeskButler.Desktop.Hosting;

/// <summary>定义由桌面层发起的可执行文件选择边界。</summary>
public interface IExecutablePicker
{
    /// <summary>让用户选择一个可执行文件；取消时返回空值。</summary>
    Task<string?> PickAsync(CancellationToken cancellationToken);
}
