using DeskButler.Infrastructure.Windows.Startup;
using Microsoft.Win32;

namespace DeskButler.Infrastructure.Windows.Tests.Startup;

public sealed class RegistryStartupRegistrationTests
{
    /// <summary>启用登录启动必须在当前用户测试键写入无参数的完整带引号路径。</summary>
    [Fact]
    public void EnableWritesExactlyQuotedExecutablePath()
    {
        using var registry = TestRegistryKey.Create(RegistryView.Default);
        var registration = registry.CreateRegistration(@"C:\Program Files\DeskButler\DeskButler.exe");

        registration.Enable();

        Assert.Equal(
            "\"C:\\Program Files\\DeskButler\\DeskButler.exe\"",
            registry.ReadValue("DeskButler"));
        Assert.True(registration.IsEnabled);
    }

    /// <summary>状态只承认自身精确命令，不接受参数、未引号或其他路径。</summary>
    [Theory]
    [InlineData("\"C:\\Apps\\DeskButler.exe\" --hidden")]
    [InlineData("C:\\Apps\\DeskButler.exe")]
    [InlineData("\"C:\\Apps\\Other.exe\"")]
    [InlineData("%LOCALAPPDATA%\\DeskButler\\DeskButler.exe")]
    public void IsEnabledRejectsAnythingExceptExactOwnCommand(string storedCommand)
    {
        using var registry = TestRegistryKey.Create(RegistryView.Default);
        registry.WriteValue("DeskButler", storedCommand);
        var registration = registry.CreateRegistration(@"C:\Apps\DeskButler.exe");

        Assert.False(registration.IsEnabled);
    }

    /// <summary>禁用只删除 DeskButler 值，不影响同一 Run 键中的其他软件。</summary>
    [Fact]
    public void DisableDeletesOnlyDeskButlerValue()
    {
        using var registry = TestRegistryKey.Create(RegistryView.Default);
        registry.WriteValue("DeskButler", "stale");
        registry.WriteValue("OtherApplication", "preserve-me");
        var registration = registry.CreateRegistration(@"C:\Apps\DeskButler.exe");

        registration.Disable();
        registration.Disable();

        Assert.Null(registry.ReadValue("DeskButler"));
        Assert.Equal("preserve-me", registry.ReadValue("OtherApplication"));
    }

    /// <summary>注册适配器必须使用调用方指定的当前用户 32/64 位视图。</summary>
    [Theory]
    [InlineData(RegistryView.Registry32)]
    [InlineData(RegistryView.Registry64)]
    public void EnableUsesRequestedCurrentUserRegistryView(RegistryView view)
    {
        using var registry = TestRegistryKey.Create(view);
        var registration = registry.CreateRegistration(@"C:\Apps\DeskButler.exe");

        registration.Enable();

        Assert.Equal("\"C:\\Apps\\DeskButler.exe\"", registry.ReadValue("DeskButler"));
    }

    /// <summary>相对路径、引号和控制字符必须在访问注册表之前拒绝。</summary>
    [Theory]
    [InlineData("DeskButler.exe")]
    [InlineData("C:\\Apps\\DeskButler.exe\" --evil")]
    [InlineData("C:\\Apps\\DeskButler.exe\n--evil")]
    public void ConstructorRejectsPathsThatCouldInjectArguments(string executablePath)
    {
        using var registry = TestRegistryKey.Create(RegistryView.Default);

        Assert.Throws<ArgumentException>(() => registry.CreateRegistration(executablePath));
        Assert.Null(registry.ReadValue("DeskButler"));
    }

    private sealed class TestRegistryKey : IDisposable
    {
        private readonly RegistryView view;
        private readonly string cleanupSubKey;

        private TestRegistryKey(RegistryView view, string baseSubKey, string cleanupSubKey)
        {
            this.view = view;
            BaseSubKey = baseSubKey;
            this.cleanupSubKey = cleanupSubKey;
        }

        public string BaseSubKey { get; }

        public static TestRegistryKey Create(RegistryView view)
        {
            var cleanupSubKey = $@"Software\DeskButler.Tests\Task9\{Guid.NewGuid():N}";
            return new TestRegistryKey(view, $@"{cleanupSubKey}\Run", cleanupSubKey);
        }

        public RegistryStartupRegistration CreateRegistration(string executablePath) =>
            new(executablePath, BaseSubKey, view);

        public object? ReadValue(string name)
        {
            using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            using var key = currentUser.OpenSubKey(BaseSubKey, writable: false);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }

        public void WriteValue(string name, string value)
        {
            using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            using var key = currentUser.CreateSubKey(BaseSubKey, writable: true);
            key.SetValue(name, value, RegistryValueKind.String);
        }

        public void Dispose()
        {
            using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            currentUser.DeleteSubKeyTree(cleanupSubKey, throwOnMissingSubKey: false);
        }
    }
}
