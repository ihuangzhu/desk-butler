using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using DeskButler.Core.ResidentApps;
using DeskButler.Infrastructure.Windows.Native;

namespace DeskButler.Infrastructure.Windows.ResidentApps;

public sealed class WindowsLogonSessionIdentityProvider : ILogonSessionIdentityProvider
{
    /// <summary>读取当前 token 的 Authentication LUID，并返回不含账号信息的稳定十六进制身份。</summary>
    public string GetCurrent()
    {
        // GetCurrentProcess 返回借用伪句柄；只有 OpenProcessToken 产出的 SafeHandle 由本方法释放。
        if (!NativeMethods.OpenProcessToken(
                NativeMethods.GetCurrentProcess(),
                NativeMethods.TokenQuery,
                out var tokenHandle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法打开当前进程 token。");
        }

        using (tokenHandle)
        {
            var size = (uint)Marshal.SizeOf<TokenStatistics>();
            if (!NativeMethods.GetTokenInformation(
                    tokenHandle,
                    NativeMethods.TokenStatisticsInformationClass,
                    out var statistics,
                    size,
                    out _))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法读取当前登录会话身份。");
            }

            var high = unchecked((uint)statistics.AuthenticationId.HighPart);
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{high:X8}-{statistics.AuthenticationId.LowPart:X8}");
        }
    }
}
