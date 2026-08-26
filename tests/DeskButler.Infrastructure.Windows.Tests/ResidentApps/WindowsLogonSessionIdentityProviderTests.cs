using System.Text.RegularExpressions;
using DeskButler.Infrastructure.Windows.ResidentApps;

namespace DeskButler.Infrastructure.Windows.Tests.ResidentApps;

public sealed class WindowsLogonSessionIdentityProviderTests
{
    /// <summary>同一进程读取的 Authentication LUID 必须稳定且只含固定宽度大写十六进制。</summary>
    [WindowsFact]
    public void GetCurrentReturnsStableAuthenticationLuid()
    {
        var provider = new WindowsLogonSessionIdentityProvider();

        var first = provider.GetCurrent();
        var second = provider.GetCurrent();

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
        Assert.Matches(new Regex("^[0-9A-F]{8}-[0-9A-F]{8}$", RegexOptions.CultureInvariant), first);
    }
}
