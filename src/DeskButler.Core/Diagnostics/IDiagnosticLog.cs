namespace DeskButler.Core.Diagnostics;

/// <summary>表示结构化诊断事件的严重程度。</summary>
public enum DiagnosticLevel
{
    /// <summary>普通运行信息。</summary>
    Information,
    /// <summary>可恢复的健康警告。</summary>
    Warning,
    /// <summary>需要用户关注的故障。</summary>
    Error
}

/// <summary>表示不含任意命令或文档内容的结构化诊断事件。</summary>
public sealed record DiagnosticEvent(
    DateTimeOffset Timestamp,
    DiagnosticLevel Level,
    string Category,
    string Message,
    IReadOnlyDictionary<string, object?>? Properties = null);

/// <summary>为平台无关业务提供有界结构化诊断写入边界。</summary>
public interface IDiagnosticLog
{
    /// <summary>异步持久化一条已验证诊断事件。</summary>
    Task WriteAsync(DiagnosticEvent diagnosticEvent, CancellationToken cancellationToken);
}
