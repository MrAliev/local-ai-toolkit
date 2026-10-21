using LocalAi.Contracts.Activation;

namespace LocalAi.Launcher.Tests;

public sealed class VersionLeaseTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-version-lease-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Multiple_shared_leases_block_exclusive_activation()
    {
        var path = Path.Combine(_root, "current.lock");
        using var first = VersionLease.AcquireShared(path);
        using var second = VersionLease.AcquireShared(path);

        var error = Assert.Throws<LauncherException>(
            () => VersionLease.AcquireExclusive(path, TimeSpan.Zero));

        Assert.Equal("version_in_use", error.Code);
    }

    [Fact]
    public void Exclusive_activation_succeeds_after_shared_lease_is_released()
    {
        var path = Path.Combine(_root, "current.lock");
        using (VersionLease.AcquireShared(path))
        {
        }

        using var exclusive = VersionLease.AcquireExclusive(
            path,
            TimeSpan.Zero);

        Assert.NotNull(exclusive);
    }

    [Fact]
    public void Hostile_named_mutex_precreate_is_rejected_with_sanitized_code()
    {
        var factory = new FakeSecureNamedMutexFactory(
            _ => throw new UnauthorizedAccessException("hostile descriptor details"));

        var error = Assert.Throws<ActivationCoordinationException>(() =>
            ActivationCoordinator.AcquireStartupGate(
                _root,
                TimeSpan.Zero,
                factory,
                TestContext.Current.CancellationToken));

        Assert.Equal("activation_unavailable", error.Code);
        Assert.DoesNotContain("hostile", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Secure_gate_name_is_local_and_bound_to_current_user()
    {
        var held = new FakeSecureNamedMutex(waitResult: true);
        var factory = new FakeSecureNamedMutexFactory(_ => held);

        using var gate = ActivationCoordinator.AcquireStartupGate(
            _root,
            TimeSpan.Zero,
            factory,
            TestContext.Current.CancellationToken);

        Assert.StartsWith(@"Local\LocalAi.Launcher.Activation.", factory.Name, StringComparison.Ordinal);
        Assert.Equal(1, held.WaitCount);
        Assert.Equal(0, held.ReleaseCount);
        gate.Dispose();
        Assert.Equal(1, held.ReleaseCount);
    }

    [Theory]
    [InlineData(0, 1_000)]
    [InlineData(700, 300)]
    [InlineData(1_000, 0)]
    [InlineData(1_400, 0)]
    public void Exclusive_uses_one_total_timeout_for_gate_and_file_lease(
        int gateMilliseconds,
        int remainingMilliseconds)
    {
        var budget = TimeSpan.FromSeconds(1);
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeProvider = new ManualTimeProvider();
        var held = new FakeSecureNamedMutex(true, (timeout, token) =>
        {
            Assert.Equal(budget, timeout);
            Assert.Equal(cancellationToken, token);
            timeProvider.Advance(TimeSpan.FromMilliseconds(gateMilliseconds));
        });
        var factory = new FakeSecureNamedMutexFactory(_ => held);
        var expectedError = new InvalidOperationException("file-lease phase failed");
        TimeSpan? fileBudget = null;

        // Capture the actual argument passed to the same second-phase callback used
        // in production. Virtual gate time avoids measuring CI scheduling jitter.
        var error = Assert.Throws<InvalidOperationException>(() =>
            ActivationCoordinator.AcquireExclusive(
                _root,
                budget,
                factory,
                timeProvider,
                (gate, timeout, token, ownsGate) =>
                {
                    fileBudget = timeout;
                    Assert.Equal(_root, gate.BinRoot);
                    Assert.Equal(cancellationToken, token);
                    Assert.True(ownsGate);
                    throw expectedError;
                },
                cancellationToken));

        Assert.Same(expectedError, error);
        Assert.Equal(TimeSpan.FromMilliseconds(remainingMilliseconds), fileBudget);
        Assert.Equal(1, held.WaitCount);
        Assert.Equal(1, held.ReleaseCount);
        Assert.Equal(1, held.DisposeCount);
    }

    [Fact]
    public void Exclusive_transfers_gate_ownership_to_the_returned_file_lease()
    {
        var held = new FakeSecureNamedMutex(waitResult: true);
        var factory = new FakeSecureNamedMutexFactory(_ => held);

        using var lease = ActivationCoordinator.AcquireExclusive(
            _root,
            TimeSpan.Zero,
            factory,
            TimeProvider.System,
            (gate, timeout, token, ownsGate) =>
            {
                Assert.True(ownsGate);
                Directory.CreateDirectory(gate.BinRoot);
                var stream = new FileStream(
                    Path.Combine(gate.BinRoot, "current.lock"),
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None);
                return new ActivationExclusiveLease(gate.BinRoot, stream, gate);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, held.ReleaseCount);
        Assert.Equal(0, held.DisposeCount);
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(1, held.ReleaseCount);
        Assert.Equal(1, held.DisposeCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeSecureNamedMutexFactory(
        Func<string, ISecureNamedMutex> create) : ISecureNamedMutexFactory
    {
        public string? Name { get; private set; }

        public ISecureNamedMutex Create(string name)
        {
            Name = name;
            return create(name);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan elapsed) => timestamp += elapsed.Ticks;
    }

    private sealed class FakeSecureNamedMutex(
        bool waitResult,
        Action<TimeSpan, CancellationToken>? onWait = null) : ISecureNamedMutex
    {
        public int WaitCount { get; private set; }
        public int ReleaseCount { get; private set; }
        public int DisposeCount { get; private set; }

        public bool WaitOne(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            WaitCount++;
            onWait?.Invoke(timeout, cancellationToken);
            return waitResult;
        }

        public void Release() => ReleaseCount++;
        public void Dispose() => DisposeCount++;
    }
}
