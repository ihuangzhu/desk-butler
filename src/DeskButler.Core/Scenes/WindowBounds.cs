namespace DeskButler.Core.Scenes;

/// <summary>表示窗口在虚拟桌面坐标系中的矩形边界。</summary>
/// <param name="Left">窗口左侧坐标。</param>
/// <param name="Top">窗口顶部坐标。</param>
/// <param name="Width">窗口宽度。</param>
/// <param name="Height">窗口高度。</param>
public readonly record struct WindowBounds(int Left, int Top, int Width, int Height);
