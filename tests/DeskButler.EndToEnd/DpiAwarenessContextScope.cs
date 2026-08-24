using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace DeskButler.EndToEnd;

/// <summary>隔离线程 DPI 上下文 API，支持确定性验证进入与恢复失败。</summary>
internal interface IDpiAwarenessContextNative
{
    nint SetThreadContext(nint context);

    int GetLastError();
}

/// <summary>在 Win10 支持的 PMv2 上下文中运行测试侧物理 Win32 操作，并严格恢复原上下文。</summary>
internal static class DpiAwarenessContextScope
{
    private static readonly nint PerMonitorAwareV2 = new(-4);

    /// <summary>使用真实 user32 API 运行并恢复。</summary>
    internal static T Run<T>(Func<T> action) => Run(Win32DpiAwarenessContextNative.Instance, action);

    /// <summary>进入和恢复失败均可观察；主体与恢复同时失败时保留两项异常。</summary>
    internal static T Run<T>(IDpiAwarenessContextNative native, Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(action);
        var previous = native.SetThreadContext(PerMonitorAwareV2);
        if (previous == 0)
        {
            throw new Win32Exception(native.GetLastError(), "无法切换测试线程到 PMv2 DPI 上下文。");
        }

        T? result = default;
        Exception? bodyFailure = null;
        Exception? restoreFailure = null;
        try
        {
            try
            {
                result = action();
            }
            catch (Exception exception)
            {
                bodyFailure = exception;
            }
        }
        finally
        {
            if (native.SetThreadContext(previous) == 0)
            {
                restoreFailure = new Win32Exception(native.GetLastError(), "无法恢复测试线程原 DPI 上下文。");
            }
        }
        if (bodyFailure is not null && restoreFailure is not null)
        {
            throw new AggregateException("DPI 操作与上下文恢复均失败。", bodyFailure, restoreFailure);
        }

        if (restoreFailure is not null)
        {
            throw restoreFailure;
        }

        if (bodyFailure is not null)
        {
            ExceptionDispatchInfo.Capture(bodyFailure).Throw();
        }

        return result!;
    }

    private sealed class Win32DpiAwarenessContextNative : IDpiAwarenessContextNative
    {
        internal static Win32DpiAwarenessContextNative Instance { get; } = new();

        public nint SetThreadContext(nint context) => SetThreadDpiAwarenessContext(context);

        public int GetLastError() => Marshal.GetLastPInvokeError();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern nint SetThreadDpiAwarenessContext(nint dpiContext);
    }
}
