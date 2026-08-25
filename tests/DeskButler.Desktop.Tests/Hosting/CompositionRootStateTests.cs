using DeskButler.Core.Settings;
using DeskButler.Desktop.Hosting;
using DeskButler.Infrastructure.Windows.Startup;

namespace DeskButler.Desktop.Tests.Hosting;

public sealed class CompositionRootStateTests
{
    /// <summary>注册目标先改变后失败时必须保留根因并独立尝试注册与设置补偿。</summary>
    [Fact]
    public async Task StartupRegistrationTargetFailureAttemptsAllCompensationsAndReleasesGate()
    {
        var calls = new List<string>();
        var targetFailure = new InvalidOperationException("注册目标失败");
        var registrationRollbackFailure = new InvalidOperationException("注册补偿失败");
        var settingsRollbackFailure = new IOException("设置补偿失败");
        var initial = ButlerSettings.Default with { StartupEnabled = false };
        var store = new RegistrationFailureSettingsStore(
            initial, calls, settingsRollbackFailure);
        var registration = new ChangeThenThrowStartupRegistration(
            calls, targetFailure, registrationRollbackFailure);
        using var settings = new SettingsCoordinator(store);
        var handler = new SetStartupEnabledCommandHandler(settings, registration);

        var error = await Assert.ThrowsAsync<AggregateException>(
            () => handler.HandleAsync(new SetStartupEnabledCommand(true), CancellationToken.None));

        Assert.Equal(3, error.InnerExceptions.Count);
        Assert.Same(targetFailure, error.InnerExceptions[0]);
        Assert.Same(registrationRollbackFailure, error.InnerExceptions[1]);
        Assert.Same(settingsRollbackFailure, error.InnerExceptions[2]);
        Assert.Equal(["registration:target", "registration:rollback", "settings:rollback"], calls);
        Assert.False(store.TargetSaveAttempted);
        Assert.True(store.RollbackAttempted);
        Assert.True(registration.RollbackAttempted);

        var updated = await settings.UpdateAsync(
            current => current with { CaptureEnabled = false }, CancellationToken.None);
        Assert.False(updated.CaptureEnabled);
    }

    /// <summary>注册目标失败且补偿成功时必须抛回同一异常实例并保留最初抛出位置。</summary>
    [Fact]
    public async Task StartupRegistrationTargetFailurePreservesOriginalExceptionAndStack()
    {
        var calls = new List<string>();
        var targetFailure = new InvalidOperationException("注册目标失败");
        var initial = ButlerSettings.Default with { StartupEnabled = false };
        var store = new RegistrationFailureSettingsStore(initial, calls);
        var registration = new ChangeThenThrowStartupRegistration(calls, targetFailure);
        using var settings = new SettingsCoordinator(store);
        var handler = new SetStartupEnabledCommandHandler(settings, registration);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(new SetStartupEnabledCommand(true), CancellationToken.None));

        Assert.Same(targetFailure, thrown);
        Assert.Contains(
            nameof(ChangeThenThrowStartupRegistration.ThrowTargetFailure),
            thrown.StackTrace,
            StringComparison.Ordinal);
        Assert.Equal(["registration:target", "registration:rollback", "settings:rollback"], calls);
        Assert.False(store.TargetSaveAttempted);
        Assert.False(registration.IsEnabled);
        Assert.False(store.Current.StartupEnabled);
    }

    /// <summary>仅目标保存失败时必须抛回同一异常实例并保留最初抛出位置。</summary>
    [Fact]
    public async Task StartupSaveFailurePreservesOriginalExceptionAndStack()
    {
        var originalFailure = new IOException("目标保存失败");
        var store = new SingleFailureSettingsStore(
            ButlerSettings.Default with { StartupEnabled = false }, originalFailure);
        var registration = new RecordingStartupRegistration(false);
        using var settings = new SettingsCoordinator(store);
        var handler = new SetStartupEnabledCommandHandler(settings, registration);

        var thrown = await Assert.ThrowsAsync<IOException>(
            () => handler.HandleAsync(new SetStartupEnabledCommand(true), CancellationToken.None));

        Assert.Same(originalFailure, thrown);
        Assert.Contains(nameof(SingleFailureSettingsStore.ThrowOriginalFailure), thrown.StackTrace, StringComparison.Ordinal);
        Assert.False(registration.IsEnabled);
        Assert.False(store.Current.StartupEnabled);
    }

    /// <summary>保存阶段取消后仍须用独立令牌补偿并释放门供后续设置修改。</summary>
    [Fact]
    public async Task StartupSaveCancellationCompensatesAndReleasesMutationGate()
    {
        using var source = new CancellationTokenSource();
        var cancellation = new OperationCanceledException(source.Token);
        var store = new SingleFailureSettingsStore(
            ButlerSettings.Default with { StartupEnabled = false }, cancellation);
        var registration = new RecordingStartupRegistration(false);
        using var settings = new SettingsCoordinator(store);
        var handler = new SetStartupEnabledCommandHandler(settings, registration);

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(
            () => handler.HandleAsync(new SetStartupEnabledCommand(true), source.Token));
        var updated = await settings.UpdateAsync(
            current => current with { CaptureEnabled = false }, CancellationToken.None);

        Assert.Same(cancellation, thrown);
        Assert.Equal(CancellationToken.None, store.RollbackToken);
        Assert.False(registration.IsEnabled);
        Assert.False(store.Current.StartupEnabled);
        Assert.False(updated.CaptureEnabled);
    }

    private sealed class SingleFailureSettingsStore(ButlerSettings initial, Exception failure) : ISettingsStore
    {
        private int saveCount;
        internal ButlerSettings Current { get; private set; } = initial;
        internal CancellationToken? RollbackToken { get; private set; }

        /// <inheritdoc />
        public Task<ButlerSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Current);

        /// <inheritdoc />
        public Task SaveAsync(ButlerSettings settings, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref saveCount) == 1)
            {
                ThrowOriginalFailure(failure);
            }

            RollbackToken ??= cancellationToken;
            Current = settings;
            return Task.CompletedTask;
        }

        /// <summary>从稳定测试边界抛出原始异常，供堆栈保留断言识别。</summary>
        internal static void ThrowOriginalFailure(Exception failure) => throw failure;
    }

    private sealed class RecordingStartupRegistration(bool enabled) : IStartupRegistration
    {
        /// <inheritdoc />
        public bool IsEnabled { get; private set; } = enabled;

        /// <inheritdoc />
        public void Enable() => IsEnabled = true;

        /// <inheritdoc />
        public void Disable() => IsEnabled = false;
    }

    private sealed class RegistrationFailureSettingsStore(
        ButlerSettings initial,
        List<string> calls,
        Exception? firstRollbackFailure = null) : ISettingsStore
    {
        private int originalSaveCount;
        internal ButlerSettings Current { get; private set; } = initial;
        internal bool TargetSaveAttempted { get; private set; }
        internal bool RollbackAttempted { get; private set; }

        /// <inheritdoc />
        public Task<ButlerSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Current);

        /// <inheritdoc />
        public Task SaveAsync(ButlerSettings settings, CancellationToken cancellationToken)
        {
            if (settings.StartupEnabled)
            {
                TargetSaveAttempted = true;
                calls.Add("settings:target");
            }
            else
            {
                RollbackAttempted = true;
                calls.Add("settings:rollback");
                if (Interlocked.Increment(ref originalSaveCount) == 1 && firstRollbackFailure is not null)
                {
                    return Task.FromException(firstRollbackFailure);
                }
            }

            Current = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class ChangeThenThrowStartupRegistration(
        List<string> calls,
        Exception targetFailure,
        Exception? rollbackFailure = null) : IStartupRegistration
    {
        /// <inheritdoc />
        public bool IsEnabled { get; private set; }

        internal bool RollbackAttempted { get; private set; }

        /// <inheritdoc />
        public void Enable()
        {
            calls.Add("registration:target");
            IsEnabled = true;
            ThrowTargetFailure(targetFailure);
        }

        /// <inheritdoc />
        public void Disable()
        {
            calls.Add("registration:rollback");
            RollbackAttempted = true;
            IsEnabled = false;
            if (rollbackFailure is not null)
            {
                throw rollbackFailure;
            }
        }

        /// <summary>从稳定测试边界抛出注册目标异常，供堆栈保留断言识别。</summary>
        internal static void ThrowTargetFailure(Exception failure) => throw failure;
    }
}
