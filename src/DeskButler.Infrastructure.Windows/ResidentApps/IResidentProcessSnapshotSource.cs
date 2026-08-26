namespace DeskButler.Infrastructure.Windows.ResidentApps;

/// <summary>定义只读取当前交互 Windows Session 进程公开元数据的观察边界。</summary>
internal interface IResidentProcessSnapshotSource
{
    /// <summary>捕获当前交互 Session 的进程观察快照。</summary>
    Task<ResidentProcessSnapshot> CaptureAsync(CancellationToken cancellationToken);
}
