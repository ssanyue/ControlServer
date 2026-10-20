using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using ControlServer.Server.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// <see cref="SwitchableConnectionListener"/> 的单元测试。
///
/// <para>
/// <b>为什么这些测试值得单独存在</b>：端口热切换的失败方式全都藏在"HTTP 层看不到"的地方 ——
/// 新端口明明 <c>LISTENING</c>、TCP 也能 <c>ESTABLISHED</c>，但没有任何请求得到响应。
/// 这类症状用集成测试（走内存 TestServer）根本复现不了，只能在这里把语义钉死。
/// </para>
/// </summary>
public sealed class SwitchableConnectionListenerTests
{
    [Fact]
    public async Task Retarget_successfully_moves_to_the_new_port()
    {
        var factory = new FakeListenerFactory();
        var listener = await NewListenerAsync(factory, port: 18080);

        var outcome = await listener.RetargetAsync(19090, CancellationToken.None);

        outcome.Ok.Should().BeTrue();
        outcome.Changed.Should().BeTrue();
        listener.Port.Should().Be(19090);
        factory.BoundPorts.Should().Equal(new[] { 18080, 19090 }, "先绑新的，成功后才关旧的");
        factory.UnboundCount.Should().Be(1, "旧监听器应被停止接受新连接");
    }

    [Fact]
    public async Task Retarget_failure_leaves_the_old_listener_serving()
    {
        // 这条就是方案 §4.4 要求的验收："新端口被占用时服务仍存活在旧端口"。
        var factory = new FakeListenerFactory { FailOnPort = 19090 };
        var listener = await NewListenerAsync(factory, port: 18080);

        var outcome = await listener.RetargetAsync(19090, CancellationToken.None);

        outcome.Ok.Should().BeFalse();
        outcome.Error.Should().Contain("19090");
        listener.Port.Should().Be(18080, "端口必须留在原处");
        factory.UnboundCount.Should().Be(0, "失败时旧监听器一动都不能动");

        // 还能继续取连接 —— 这才叫"服务仍存活"。
        // ⚠️ 必须先 Enqueue 再 Accept：空监听器上的 Accept 会一直挂着（那正是它的语义），
        //    直接 await 会把测试挂死，而不是失败。
        var oldListener = (FakeConnectionListener)factory.LastListener!;
        var expected = new FakeConnectionContext();
        oldListener.Enqueue(expected);

        var accepted = await listener.AcceptAsync(CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        accepted.Should().BeSameAs(expected, "失败后旧监听器应仍在接受连接");
    }

    [Fact]
    public async Task Retarget_reports_occupied_port_in_readable_chinese()
    {
        var factory = new FakeListenerFactory
        {
            FailOnPort = 19090,
            Exception = new SocketException((int)SocketError.AddressAlreadyInUse),
        };

        var listener = await NewListenerAsync(factory, port: 18080);
        var outcome = await listener.RetargetAsync(19090, CancellationToken.None);

        outcome.Error.Should().Contain("已被其它进程占用");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public async Task Retarget_rejects_out_of_range_ports(int port)
    {
        var factory = new FakeListenerFactory();
        var listener = await NewListenerAsync(factory, port: 18080);

        var outcome = await listener.RetargetAsync(port, CancellationToken.None);

        outcome.Ok.Should().BeFalse();
        outcome.Error.Should().Contain("越界");
        listener.Port.Should().Be(18080);
        factory.BoundPorts.Should().Equal(new[] { 18080 }, "越界端口连试都不该试");
    }

    [Fact]
    public async Task Retarget_to_the_same_port_is_a_no_op()
    {
        var factory = new FakeListenerFactory();
        var listener = await NewListenerAsync(factory, port: 18080);

        var outcome = await listener.RetargetAsync(18080, CancellationToken.None);

        outcome.Ok.Should().BeTrue();
        outcome.Changed.Should().BeFalse();
        factory.BoundPorts.Should().HaveCount(1, "同端口不该重复绑定");
    }

    [Fact]
    public async Task Retarget_after_unbind_fails_instead_of_resurrecting_the_listener()
    {
        var factory = new FakeListenerFactory();
        var listener = await NewListenerAsync(factory, port: 18080);

        await listener.UnbindAsync(CancellationToken.None);

        var outcome = await listener.RetargetAsync(19090, CancellationToken.None);

        outcome.Ok.Should().BeFalse("服务正在停机，此时换端口不该把监听'复活'");
        outcome.Error.Should().Contain("监听已停止");
        factory.BoundPorts.Should().Equal(new[] { 18080 });
    }

    [Fact]
    public async Task Accept_returns_null_only_when_the_listener_is_really_stopped()
    {
        var factory = new FakeListenerFactory();
        var listener = await NewListenerAsync(factory, port: 18080);

        await listener.UnbindAsync(CancellationToken.None);

        var accepted = await listener.AcceptAsync(CancellationToken.None);

        // null 是"结束"信号 —— 只有真的停机才能交给 Kestrel。
        accepted.Should().BeNull();
    }

    /// <summary>
    /// ⭐ 守门用例：换端口时旧 listener 的 <c>AcceptAsync</c> 返回 <c>null</c>，
    /// <b>绝不能</b>把 null 透传给 Kestrel。
    ///
    /// <para>
    /// 实测过的症状：Kestrel 收到 null 就认为"这个监听结束了"，<b>永久退出接受循环</b>。
    /// 于是新旧端口都还在 <c>LISTENING</c>、TCP 握手也照常完成（连接躺在 backlog 里），
    /// 但<b>没有任何请求得到响应</b> —— 表现为"改完端口页面就打不开了"。
    /// </para>
    /// <para>
    /// 修法是：内层返回 null 时先看"是不是被换掉了"，只有真停机才交给 Kestrel。
    /// 这条用例把那个判断钉死。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Accept_does_not_forward_null_from_a_replaced_listener()
    {
        var factory = new FakeListenerFactory();
        var listener = await NewListenerAsync(factory, port: 18080);
        var initial = (FakeConnectionListener)factory.LastListener!;

        // 让旧 listener 的 AcceptAsync "停下"（返回 null）—— 这是 Retarget 关闭旧监听器的真实效果。
        // 具体返回 null 还是抛异常属实现细节，这里两种都覆盖（见下一条用例）。
        var accepting = listener.AcceptAsync(CancellationToken.None).AsTask();

        await listener.RetargetAsync(19090, CancellationToken.None);

        // 换代之后，AcceptAsync 必须去接「新」listener，而不是把旧 listener 的 null 交上去。
        factory.LastListener.Should().NotBeSameAs(initial, "应当已经换成新监听器");

        // 给新 listener 一个连接，验证 AcceptAsync 确实接到了它。
        var expected = new FakeConnectionContext();
        factory.LastListener!.Enqueue(expected);

        var accepted = await accepting.WaitAsync(TimeSpan.FromSeconds(5));

        accepted.Should().BeSameAs(expected, "换端口后必须无缝接到新监听器上");
    }

    /// <summary>
    /// 同上，但走"旧 listener 抛异常"这条路径（不同 Kestrel 版本的行为可能不同，
    /// 两条都必须能自愈）。
    /// </summary>
    [Fact]
    public async Task Accept_recovers_when_the_replaced_listener_throws()
    {
        var factory = new FakeListenerFactory();
        var listener = await NewListenerAsync(factory, port: 18080);
        var initial = (FakeConnectionListener)factory.LastListener!;

        var accepting = listener.AcceptAsync(CancellationToken.None).AsTask();

        await listener.RetargetAsync(19090, CancellationToken.None);
        initial.FailAllPending(new ObjectDisposedException("old listener"));

        factory.LastListener.Should().NotBeSameAs(initial);

        var expected = new FakeConnectionContext();
        factory.LastListener!.Enqueue(expected);

        var accepted = await accepting.WaitAsync(TimeSpan.FromSeconds(5));

        accepted.Should().BeSameAs(expected);
    }

    private static async Task<SwitchableConnectionListener> NewListenerAsync(
        FakeListenerFactory factory,
        int port)
    {
        var listener = new SwitchableConnectionListener(
            factory, IPAddress.Loopback, "127.0.0.1", port, NullLogger.Instance);

        // Kestrel 期望 BindAsync 返回时已经在监听，所以生产路径里这一步是工厂做的。
        // 测试里必须走同一步 —— 否则 _inner 是 null，所有切换路径都会掉进
        // "监听已停止"分支，测出来的东西跟生产完全不是一回事。
        await listener.BindInitialAsync(CancellationToken.None);
        return listener;
    }

    // ═══════════════════════════════════════════════════════════════
    // 测试替身
    // ═══════════════════════════════════════════════════════════════

    private sealed class FakeListenerFactory : IConnectionListenerFactory
    {
        private readonly List<int> _boundPorts = new();

        /// <summary>在这个端口上绑定就抛异常（模拟"被占用"）。</summary>
        public int? FailOnPort { get; init; }

        /// <summary>绑定失败时抛的异常；默认 AddressAlreadyInUse。</summary>
        public Exception? Exception { get; init; }

        public IReadOnlyList<int> BoundPorts
        {
            get
            {
                lock (_boundPorts)
                {
                    return _boundPorts.ToList();
                }
            }
        }

        public int UnboundCount { get; private set; }

        public FakeConnectionListener? LastListener { get; private set; }

        public ValueTask<IConnectionListener> BindAsync(
            EndPoint endpoint,
            CancellationToken cancellationToken = default)
        {
            var port = ((IPEndPoint)endpoint).Port;

            if (FailOnPort == port)
            {
                throw Exception ?? new SocketException((int)SocketError.AddressAlreadyInUse);
            }

            lock (_boundPorts)
            {
                _boundPorts.Add(port);
            }

            LastListener = new FakeConnectionListener(port, () => UnboundCount++);
            return ValueTask.FromResult<IConnectionListener>(LastListener);
        }
    }

    private sealed class FakeConnectionListener : IConnectionListener
    {
        private readonly Action _onUnbind;
        private readonly Queue<ConnectionContext> _queue = new();
        private readonly List<TaskCompletionSource<ConnectionContext?>> _waiting = new();
        private bool _stopped;

        public FakeConnectionListener(int port, Action onUnbind)
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port);
            _onUnbind = onUnbind;
        }

        public EndPoint EndPoint { get; }

        public void Enqueue(ConnectionContext context)
        {
            lock (_queue)
            {
                if (_waiting.Count > 0)
                {
                    var waiter = _waiting[0];
                    _waiting.RemoveAt(0);
                    waiter.TrySetResult(context);
                    return;
                }

                _queue.Enqueue(context);
            }
        }

        /// <summary>让所有挂起的 Accept 以异常结束（模拟监听器被关）。</summary>
        public void FailAllPending(Exception exception)
        {
            lock (_queue)
            {
                foreach (var waiter in _waiting)
                {
                    waiter.TrySetException(exception);
                }

                _waiting.Clear();
            }
        }

        public ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
        {
            lock (_queue)
            {
                // 已停止 = 返回 null。这正是 Retarget 关掉旧监听器后 Kestrel 会看到的东西。
                if (_stopped)
                {
                    return ValueTask.FromResult<ConnectionContext?>(null);
                }

                if (_queue.Count > 0)
                {
                    return ValueTask.FromResult<ConnectionContext?>(_queue.Dequeue());
                }

                var tcs = new TaskCompletionSource<ConnectionContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiting.Add(tcs);
                return new ValueTask<ConnectionContext?>(tcs.Task);
            }
        }

        public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
        {
            lock (_queue)
            {
                _stopped = true;
                _onUnbind();

                // 真实实现里"停止监听"会让挂起的 Accept 结束 —— 这里保持同样的语义，
                // 否则换端口时 AcceptAsync 会永远挂着（那就测不出"无缝切到新监听器"了）。
                foreach (var waiter in _waiting)
                {
                    waiter.TrySetResult(null);
                }

                _waiting.Clear();
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>只用来做"同一个对象"的身份断言，不参与任何 I/O。</summary>
    private sealed class FakeConnectionContext : ConnectionContext
    {
        public override string ConnectionId { get; set; } = Guid.NewGuid().ToString("N");

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();

        public override EndPoint? LocalEndPoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 0);

        public override EndPoint? RemoteEndPoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 0);

        public override IDuplexPipe Transport { get; set; } = null!;

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
