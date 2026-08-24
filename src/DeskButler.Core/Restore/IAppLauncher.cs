using DeskButler.Core.Scenes;

namespace DeskButler.Core.Restore;

/// <summary>定义只负责发起安全启动且不暴露可终止进程句柄的平台边界。</summary>
public interface IAppLauncher
{
    /// <summary>按照已批准场景项目启动普通程序或资源管理器目录。</summary>
    /// <param name="sceneItem">已由恢复计划批准的场景项目。</param>
    /// <param name="cancellationToken">限制本次启动的取消令牌。</param>
    Task LaunchAsync(SceneItem sceneItem, CancellationToken cancellationToken);
}
