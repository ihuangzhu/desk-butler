using DeskButler.Application.Commands;
using DeskButler.Core.Persistence;
using DeskButler.Core.Scenes;
using DeskButler.Core.Settings;
using DeskButler.Core.Time;

namespace DeskButler.Desktop.Tests.ViewModels;

/// <summary>为 ViewModel 测试记录真实命令对象，不对命令内容做宽松模拟。</summary>
internal sealed class RecordingCommandBus : ICommandBus
{
    internal List<object> SentCommands { get; } = [];

    /// <inheritdoc />
    public Task<TResponse> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SentCommands.Add(command);
        return Task.FromResult(default(TResponse)!);
    }
}

/// <summary>允许测试在命令边界精确控制完成和失败时机。</summary>
internal sealed class ControlledCommandBus(Func<object, Task> handleAsync) : ICommandBus
{
    internal List<object> SentCommands { get; } = [];

    /// <inheritdoc />
    public async Task<TResponse> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken)
    {
        SentCommands.Add(command);
        await handleAsync(command);
        return default!;
    }
}

/// <summary>提供可由测试同步推进的单调虚拟时钟。</summary>
internal sealed class FakeClock : IClock
{
    private readonly List<Delay> delays = [];

    internal FakeClock()
    {
        UtcNow = new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);
    }

    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; private set; }

    /// <summary>获取测试期间启动的延时总数，用于验证没有创建新的计时器。</summary>
    internal int DelayCallCount { get; private set; }

    /// <summary>记录并挂起一个可由测试同步推进的延时。</summary>
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DelayCallCount++;
        // 同步完成让 AdvanceAsync 成为确定性的“时间已推进且延续已观察”边界。
        var completion = new TaskCompletionSource();
        var scheduled = new Delay(UtcNow + delay, completion);
        scheduled.Registration = cancellationToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetCanceled(), completion);
        delays.Add(scheduled);
        return completion.Task;
    }

    /// <summary>推进虚拟时钟并等待到期延续任务排空。</summary>
    internal async Task AdvanceAsync(TimeSpan duration)
    {
        UtcNow += duration;
        var due = delays.Where(item => item.DueAt <= UtcNow).ToArray();
        delays.RemoveAll(item => item.DueAt <= UtcNow);
        foreach (var delay in due)
        {
            delay.Registration.Dispose();
            delay.Completion.TrySetResult();
        }

        for (var index = 0; index < 8; index++)
        {
            await Task.Yield();
        }
    }

    private sealed class Delay(DateTimeOffset dueAt, TaskCompletionSource completion)
    {
        internal DateTimeOffset DueAt { get; } = dueAt;

        internal TaskCompletionSource Completion { get; } = completion;

        internal CancellationTokenRegistration Registration { get; set; }
    }
}

/// <summary>保存最近现场的内存仓库，严格保留调用方请求的数量。</summary>
internal sealed class InMemorySceneRepository(params SceneSnapshot[] scenes) : ISceneRepository
{
    private readonly List<SceneSnapshot> scenes = [.. scenes];

    /// <inheritdoc />
    public Task SaveAsync(SceneSnapshot snapshot, CancellationToken cancellationToken)
    {
        scenes.Insert(0, snapshot);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SceneSnapshot>> GetRecentAsync(int maximumCount, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SceneSnapshot>>(scenes.Take(maximumCount).ToArray());

    /// <inheritdoc />
    public Task MarkInvalidAsync(Guid snapshotId, string reason, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>提供可观察保存结果的内存设置存储。</summary>
internal sealed class InMemorySettingsStore(ButlerSettings initial) : ISettingsStore
{
    internal ButlerSettings Current { get; private set; } = initial;

    /// <inheritdoc />
    public Task<ButlerSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Current);

    /// <inheritdoc />
    public Task SaveAsync(ButlerSettings settings, CancellationToken cancellationToken)
    {
        Current = settings;
        return Task.CompletedTask;
    }
}

/// <summary>构造带稳定字面量的场景，避免测试期望复用生产投影逻辑。</summary>
internal static class SceneFactory
{
    internal static SceneSnapshot Create(string id, DateTimeOffset capturedAt, params string[] executablePaths)
    {
        var monitor = new MonitorIdentity("DISPLAY1", new WindowBounds(0, 0, 1920, 1080), 96, 96);
        var items = executablePaths.Select((path, index) => new SceneItem(
            $"item-{id}-{index}", path, "WindowClass", $"窗口 {index + 1}", null,
            new WindowBounds(20 + index, 30 + index, 800, 600), SceneWindowState.Normal, monitor, false)).ToArray();
        return new SceneSnapshot(Guid.Parse(id), 1, capturedAt, "test", items);
    }
}
