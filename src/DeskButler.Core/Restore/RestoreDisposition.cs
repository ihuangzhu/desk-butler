namespace DeskButler.Core.Restore;

/// <summary>表示恢复计划项目的保守处理方式。</summary>
public enum RestoreDisposition
{
    /// <summary>复用本次规划时可靠匹配到的当前窗口。</summary>
    Reuse,

    /// <summary>启动场景项目对应的普通程序或资源管理器目录。</summary>
    Launch,

    /// <summary>因多实例或身份不足无法形成可靠一对一关系而跳过。</summary>
    SkipAmbiguous,

    /// <summary>因权限、失败历史或安全模式限制而默认跳过。</summary>
    SkipUnsafe,

    /// <summary>因启动路径缺失、异常或不是绝对路径而不可恢复。</summary>
    MissingPath
}
