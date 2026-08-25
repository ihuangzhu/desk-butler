namespace DeskButler.Modules.WorkspaceRecovery;

/// <summary>表示不改写用户设置的进程内自动捕获门禁。</summary>
public sealed class AutomaticCaptureGate
{
    private int paused;

    /// <summary>以指定初始暂停状态和用户可见原因创建运行期门禁。</summary>
    public AutomaticCaptureGate(bool paused, string? pauseReason = null)
    {
        this.paused = paused ? 1 : 0;
        PauseReason = paused ? pauseReason : null;
    }

    /// <summary>获取自动捕获当前是否因运行期安全策略暂停。</summary>
    public bool IsPaused => Volatile.Read(ref paused) != 0;

    /// <summary>获取运行期暂停原因；未暂停时为空。</summary>
    public string? PauseReason { get; private set; }

    /// <summary>响应用户明确继续运行，解除本进程自动捕获门禁。</summary>
    public void Resume()
    {
        Interlocked.Exchange(ref paused, 0);
        PauseReason = null;
    }
}
