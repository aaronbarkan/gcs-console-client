using Gcs.Client.Application;
using Gcs.Client.Grpc;
using Grpc.Core;

namespace Gcs.Client.Tests;

public class GrpcTelemetrySourceTests
{
    [Theory]
    [InlineData(LinkStatus.Waiting, TelemetryLinkStatus.Waiting, false)]
    [InlineData(LinkStatus.Live, TelemetryLinkStatus.Live, true)]
    [InlineData(LinkStatus.Stale, TelemetryLinkStatus.Stale, true)]
    public async Task MapsWireDataAndDisposesCall(LinkStatus status, TelemetryLinkStatus expected, bool hasPosition)
    {
        var message = new TelemetryEvent { Revision = 42, Status = status };
        if (hasPosition) message.Telemetry = new TelemetryData {
            LatitudeDeg = 32.1, LongitudeDeg = 34.8, AltitudeMslM = 101,
            RelativeAltM = 51, SpeedMps = 5, HeadingDeg = 90 };
        var client = new FakeClient(new Reader(message));
        using var stop = new CancellationTokenSource();
        var seen = new List<TelemetrySnapshot>();
        await foreach (var s in new GrpcTelemetrySource(client).SubscribeAsync(stop.Token)) seen.Add(s);
        var snapshot = Assert.Single(seen);
        Assert.Equal(42UL, snapshot.Revision);
        Assert.Equal(expected, snapshot.Status);
        Assert.Equal(hasPosition ? new Position(32.1, 34.8, 101, 51, 5, 90) : null, snapshot.Telemetry);
        Assert.Equal(stop.Token, client.Token);
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task LocalRpcCancellationBecomesOperationCanceledAndDisposesCall()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var client = new FakeClient(new Reader(null, new RpcException(new Status(StatusCode.Cancelled, "cancelled"))));
        await Assert.ThrowsAsync<OperationCanceledException>(async () => {
            await foreach (var _ in new GrpcTelemetrySource(client).SubscribeAsync(stop.Token)) { }
        });
        Assert.True(client.Disposed);
    }

    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.Cancelled)]
    public async Task RemoteFailuresPropagateAndDisposeCall(StatusCode code)
    {
        var client = new FakeClient(new Reader(null, new RpcException(new Status(code, "server failed"))));
        var error = await Assert.ThrowsAsync<RpcException>(async () => {
            await foreach (var _ in new GrpcTelemetrySource(client).SubscribeAsync()) { }
        });
        Assert.Equal(code, error.StatusCode);
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task UnknownStatusFailsExplicitly()
    {
        var client = new FakeClient(new Reader(new TelemetryEvent { Status = (LinkStatus)99 }));
        await Assert.ThrowsAsync<InvalidDataException>(async () => {
            await foreach (var _ in new GrpcTelemetrySource(client).SubscribeAsync()) { }
        });
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task EarlyConsumerExitDisposesCall()
    {
        var client = new FakeClient(new Reader(new TelemetryEvent()));
        await foreach (var _ in new GrpcTelemetrySource(client).SubscribeAsync()) break;
        Assert.True(client.Disposed);
    }

    private sealed class FakeClient(IAsyncStreamReader<TelemetryEvent> reader) : TelemetryService.TelemetryServiceClient
    {
        public bool Disposed { get; private set; }
        public CancellationToken Token { get; private set; }
        public override AsyncServerStreamingCall<TelemetryEvent> SubscribeTelemetry(
            SubscribeTelemetryRequest request, CallOptions options)
        {
            Token = options.CancellationToken;
            return new(reader, Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => Disposed = true);
        }
    }

    private sealed class Reader(TelemetryEvent? message, Exception? failure = null) : IAsyncStreamReader<TelemetryEvent>
    {
        private bool read;
        public TelemetryEvent Current => message!;
        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (failure is not null) return Task.FromException<bool>(failure);
            var available = !read && message is not null;
            read = true;
            return Task.FromResult(available);
        }
    }
}
