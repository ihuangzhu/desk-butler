using DeskButler.Core.Restore;

namespace DeskButler.Desktop.ViewModels;

/// <summary>把逐项恢复结果投影为首页和恢复卡共享的中文诊断摘要。</summary>
internal static class RestoreResultSummary
{
    /// <summary>生成稳定数量摘要，并附加去重后的失败或取消原因。</summary>
    internal static string Format(RestoreResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var succeeded = result.Items.Count(item => item.Status == RestoreItemStatus.Succeeded);
        var skipped = result.Items.Count(item => item.Status == RestoreItemStatus.Skipped);
        var failed = result.Items.Count(item => item.Status == RestoreItemStatus.Failed);
        var cancelled = result.Items.Count(item => item.Status == RestoreItemStatus.Cancelled);
        var reasons = result.Items
            .Where(item => item.Status is RestoreItemStatus.Failed or RestoreItemStatus.Cancelled)
            .Select(item => item.ErrorMessage)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var summary = $"成功 {succeeded}，跳过 {skipped}，失败 {failed}，取消 {cancelled}";
        return reasons.Length == 0 ? summary : $"{summary}；原因：{string.Join("；", reasons)}";
    }
}
