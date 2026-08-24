using DeskButler.Core.Scenes;

namespace DeskButler.EndToEnd;

/// <summary>仅供验收测试使用的独立 DPI 几何 oracle，不调用生产定位器或其 internal 算法。</summary>
internal static class IndependentDpiOracle
{
    /// <summary>按保存工作区相对坐标独立缩放四边，并完全约束到目标工作区。</summary>
    internal static PhysicalWindowBounds Calculate(
        WindowBounds saved,
        WindowBounds sourceWorkArea,
        WindowBounds targetWorkArea,
        uint sourceDpi,
        uint targetDpi)
    {
        var ratio = targetDpi / (double)sourceDpi;
        var width = Math.Clamp(
            Round(saved.Width * ratio), Math.Min(200, targetWorkArea.Width), targetWorkArea.Width);
        var height = Math.Clamp(
            Round(saved.Height * ratio), Math.Min(120, targetWorkArea.Height), targetWorkArea.Height);
        var left = Math.Clamp(
            targetWorkArea.Left + Round((saved.Left - sourceWorkArea.Left) * ratio),
            targetWorkArea.Left,
            targetWorkArea.Left + targetWorkArea.Width - width);
        var top = Math.Clamp(
            targetWorkArea.Top + Round((saved.Top - sourceWorkArea.Top) * ratio),
            targetWorkArea.Top,
            targetWorkArea.Top + targetWorkArea.Height - height);
        return PhysicalWindowBounds.FromEdges(left, top, checked(left + width), checked(top + height));
    }

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
