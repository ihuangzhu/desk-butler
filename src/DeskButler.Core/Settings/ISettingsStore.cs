namespace DeskButler.Core.Settings;

/// <summary>定义用户设置的读取和原子保存边界。</summary>
public interface ISettingsStore
{
    /// <summary>读取用户设置；不存在时返回默认值。</summary>
    /// <param name="cancellationToken">用于取消读取操作的令牌。</param>
    /// <returns>已加载或默认的用户设置。</returns>
    Task<ButlerSettings> LoadAsync(CancellationToken cancellationToken);

    /// <summary>原子保存用户设置。</summary>
    /// <param name="settings">要保存的用户设置。</param>
    /// <param name="cancellationToken">用于取消保存操作的令牌。</param>
    /// <returns>保存完成的任务。</returns>
    Task SaveAsync(ButlerSettings settings, CancellationToken cancellationToken);
}
