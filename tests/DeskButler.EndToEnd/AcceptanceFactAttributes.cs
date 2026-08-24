using Xunit;

namespace DeskButler.EndToEnd;

/// <summary>只在 Windows 上运行不改变交互桌面的验收测试。</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class WindowsFactAttribute : FactAttribute
{
    /// <summary>为非 Windows 环境设置明确跳过原因。</summary>
    public WindowsFactAttribute(
        [System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "仅在 Windows 10/11 验收环境运行。";
        }
    }
}

/// <summary>仅由显式环境变量开启会启动受控窗口的交互验收测试。</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class InteractiveWindowsFactAttribute : FactAttribute
{
    /// <summary>默认阻止窗口启动；显式设置 DESKBUTLER_RUN_INTERACTIVE_E2E=1 后启用。</summary>
    public InteractiveWindowsFactAttribute(
        [System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "仅在 Windows 10/11 验收环境运行。";
        }
        else if (!Environment.UserInteractive)
        {
            Skip = "当前会话不是交互桌面。";
        }
        else if (!string.Equals(Environment.GetEnvironmentVariable("DESKBUTLER_RUN_INTERACTIVE_E2E"), "1", StringComparison.Ordinal))
        {
            Skip = "设置 DESKBUTLER_RUN_INTERACTIVE_E2E=1 才会启动项目 fixture 窗口。";
        }
    }
}

/// <summary>仅由独立环境变量开启真实三十分钟资源采样。</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class LongRunningWindowsFactAttribute : FactAttribute
{
    /// <summary>要求 Windows、交互会话及 DESKBUTLER_RUN_LONG_E2E=1 三重门禁。</summary>
    public LongRunningWindowsFactAttribute(
        [System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "仅在 Windows 10/11 验收环境运行。";
        }
        else if (!Environment.UserInteractive)
        {
            Skip = "当前会话不是交互桌面。";
        }
        else if (!string.Equals(Environment.GetEnvironmentVariable("DESKBUTLER_RUN_LONG_E2E"), "1", StringComparison.Ordinal))
        {
            Skip = "设置 DESKBUTLER_RUN_LONG_E2E=1 才会运行真实三十分钟采样。";
        }
        else if (Environment.GetEnvironmentVariable("DESKBUTLER_EVIDENCE_DIRECTORY") is not { } evidenceDirectory ||
                 !Path.IsPathFullyQualified(evidenceDirectory))
        {
            Skip = "真实长测还必须提供绝对 DESKBUTLER_EVIDENCE_DIRECTORY，证据不会写入仓库。";
        }
    }
}
