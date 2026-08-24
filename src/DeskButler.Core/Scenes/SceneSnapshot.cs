namespace DeskButler.Core.Scenes;

/// <summary>表示一次工作区场景捕获的不可变快照。</summary>
/// <param name="Id">快照唯一标识。</param>
/// <param name="FormatVersion">快照数据格式版本。</param>
/// <param name="CapturedAt">快照捕获时刻。</param>
/// <param name="CaptureReason">触发本次捕获的原因。</param>
/// <param name="Items">快照中的窗口条目。</param>
public sealed record SceneSnapshot(
    Guid Id,
    int FormatVersion,
    DateTimeOffset CapturedAt,
    string CaptureReason,
    IReadOnlyList<SceneItem> Items);
