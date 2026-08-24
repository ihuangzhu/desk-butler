using DeskButler.Core.Scenes;

namespace DeskButler.Core.Restore;

/// <summary>定义将既有顶层窗口恢复到已保存布局的平台边界。</summary>
public interface IWindowPositioner
{
    /// <summary>先设置普通边界，再应用场景中保存的窗口显示状态。</summary>
    /// <param name="windowHandle">本次会话内借用的窗口句柄。</param>
    /// <param name="sceneItem">提供布局和显示器信息的场景项目。</param>
    /// <param name="cancellationToken">限制本次定位的取消令牌。</param>
    Task PositionAsync(nint windowHandle, SceneItem sceneItem, CancellationToken cancellationToken);
}
