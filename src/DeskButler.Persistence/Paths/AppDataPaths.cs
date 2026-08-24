namespace DeskButler.Persistence.Paths;

/// <summary>集中管理 DeskButler 当前用户数据目录中的文件路径。</summary>
public sealed class AppDataPaths
{
    /// <summary>使用当前用户 LocalAppData 下的 DeskButler 目录初始化路径。</summary>
    public AppDataPaths()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskButler"))
    {
    }

    /// <summary>使用显式根目录初始化路径，供宿主组合和隔离测试注入。</summary>
    /// <param name="rootDirectory">DeskButler 专属应用数据根目录。</param>
    public AppDataPaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    /// <summary>获取 DeskButler 专属应用数据根目录。</summary>
    public string RootDirectory { get; }

    /// <summary>获取 SQLite 快照数据库的完整路径。</summary>
    public string DatabasePath => Path.Combine(RootDirectory, "deskbutler.db");

    /// <summary>获取 JSON 用户设置文件的完整路径。</summary>
    public string SettingsFilePath => Path.Combine(RootDirectory, "settings.json");

    /// <summary>获取日志和数据库故障证据所在的诊断目录。</summary>
    public string DiagnosticsDirectory => Path.Combine(RootDirectory, "diagnostics");

    /// <summary>获取有界 JSONL 日志专属目录。</summary>
    public string LogDirectory => Path.Combine(DiagnosticsDirectory, "logs");

    /// <summary>确保应用数据根目录存在。</summary>
    public void EnsureRootDirectoryExists()
    {
        Directory.CreateDirectory(RootDirectory);
    }
}
