using DeskButler.Infrastructure.Windows.Native;

namespace DeskButler.Infrastructure.Windows.Tests.Windows;

public sealed class NativeWindowPropertyReaderTests
{
    /// <summary>验证 owner/style 合法返回零时，即使先前存在陈旧错误也解释为成功。</summary>
    [Fact]
    public void TryRead属性合法零返回解释为无Owner和无Style()
    {
        var reader = new NativeWindowPropertyReader(new FakeWindowPropertyNativeApi(null));

        var ownerSucceeded = reader.TryGetOwner(42, out var owner);
        var styleSucceeded = reader.TryGetExtendedStyle(42, out var style);

        Assert.True(ownerSucceeded);
        Assert.Equal(0, owner);
        Assert.True(styleSucceeded);
        Assert.Equal(0, style);
    }

    /// <summary>验证 owner/style 返回零且设置错误码时解释为窗口读取失败。</summary>
    [Fact]
    public void TryRead属性零返回且有错误码时解释为失败()
    {
        var reader = new NativeWindowPropertyReader(new FakeWindowPropertyNativeApi(1400));

        Assert.False(reader.TryGetOwner(42, out _));
        Assert.False(reader.TryGetExtendedStyle(42, out _));
    }

    private sealed class FakeWindowPropertyNativeApi : IWindowPropertyNativeApi
    {
        private readonly int? callError;
        private int lastError = 5;

        /// <summary>创建零返回的 fake API，并可选择在调用时设置新错误码。</summary>
        /// <param name="callError">调用设置的错误码；空表示合法零返回。</param>
        public FakeWindowPropertyNativeApi(int? callError)
        {
            this.callError = callError;
        }

        /// <summary>清除或设置线程关联的模拟 last-error。</summary>
        public void SetLastError(int errorCode)
        {
            lastError = errorCode;
        }

        /// <summary>读取当前线程关联的模拟 last-error。</summary>
        public int GetLastError() => lastError;

        /// <summary>模拟 GetWindow 的可歧义零返回。</summary>
        public nint GetOwner(nint windowHandle)
        {
            SetCallErrorIfNeeded();
            return 0;
        }

        /// <summary>模拟 GetWindowLongPtr 的可歧义零返回。</summary>
        public nint GetExtendedStyle(nint windowHandle)
        {
            SetCallErrorIfNeeded();
            return 0;
        }

        /// <summary>仅在本次调用被配置为失败时写入错误码。</summary>
        private void SetCallErrorIfNeeded()
        {
            if (callError is not null)
            {
                lastError = callError.Value;
            }
        }
    }
}
