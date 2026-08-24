namespace DeskButler.Infrastructure.Windows.Startup;

/// <summary>管理当前用户的 DeskButler 登录启动注册。</summary>
public interface IStartupRegistration
{
    /// <summary>获取注册表是否精确指向当前 DeskButler 可执行文件。</summary>
    bool IsEnabled { get; }

    /// <summary>启用当前用户登录启动。</summary>
    void Enable();

    /// <summary>禁用当前用户登录启动。</summary>
    void Disable();
}
