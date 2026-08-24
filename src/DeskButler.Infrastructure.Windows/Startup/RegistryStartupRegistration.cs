using Microsoft.Win32;

namespace DeskButler.Infrastructure.Windows.Startup;

/// <summary>通过 HKCU Run 键管理 DeskButler 登录启动。</summary>
public sealed class RegistryStartupRegistration : IStartupRegistration
{
    private const string DefaultRunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DeskButler";
    private readonly string command;
    private readonly string baseSubKey;
    private readonly RegistryView registryView;

    /// <summary>使用正式当前用户 Run 键创建注册适配器。</summary>
    public RegistryStartupRegistration(string executablePath)
        : this(executablePath, DefaultRunSubKey, RegistryView.Default)
    {
    }

    /// <summary>使用隔离子键和指定视图创建注册适配器，供测试使用。</summary>
    internal RegistryStartupRegistration(string executablePath, string baseSubKey, RegistryView registryView)
    {
        if (string.IsNullOrWhiteSpace(executablePath) ||
            !Path.IsPathFullyQualified(executablePath) ||
            executablePath.Any(character => character == '"' || char.IsControl(character)))
        {
            throw new ArgumentException("登录启动可执行文件必须是不含引号、控制字符或参数的完整路径。", nameof(executablePath));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(baseSubKey);
        if (registryView is not RegistryView.Default and not RegistryView.Registry32 and not RegistryView.Registry64)
        {
            throw new ArgumentOutOfRangeException(nameof(registryView));
        }

        var normalizedPath = Path.GetFullPath(executablePath);
        command = $"\"{normalizedPath}\"";
        this.baseSubKey = baseSubKey;
        this.registryView = registryView;
    }

    /// <inheritdoc />
    public bool IsEnabled
    {
        get
        {
            using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, registryView);
            using var runKey = currentUser.OpenSubKey(baseSubKey, writable: false);
            var stored = runKey?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return stored is string storedCommand &&
                   string.Equals(storedCommand, command, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <inheritdoc />
    public void Enable()
    {
        using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, registryView);
        using var runKey = currentUser.CreateSubKey(baseSubKey, writable: true);
        runKey.SetValue(ValueName, command, RegistryValueKind.String);
    }

    /// <inheritdoc />
    public void Disable()
    {
        using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, registryView);
        using var runKey = currentUser.OpenSubKey(baseSubKey, writable: true);
        runKey?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
