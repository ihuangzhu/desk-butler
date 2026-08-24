namespace DeskButler.Infrastructure.Windows.Tests;

/// <summary>仅在 Windows 上执行依赖真实桌面 API 的测试。</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    /// <summary>根据当前操作系统决定是否跳过 Windows 专用测试。</summary>
    /// <param name="sourceFilePath">由编译器提供的测试源文件路径。</param>
    /// <param name="sourceLineNumber">由编译器提供的测试源代码行号。</param>
    public WindowsFactAttribute(
        [System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "此测试需要 Windows。";
        }
    }
}
