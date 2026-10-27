using System.Runtime.CompilerServices;
using Gcs.Client.Application;
using Grpc.Core;

namespace Gcs.Client.Grpc;

public sealed class GrpcTelemetrySource(TelemetryService.TelemetryServiceClient client) : ITelemetrySource
{
    public async IAsyncEnumerable<TelemetrySnapshot> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var call = client.SubscribeTelemetry(new SubscribeTelemetryRequest(),
            cancellationToken: cancellationToken);
        while (await MoveNextAsync(call.ResponseStream, cancellationToken).ConfigureAwait(false))
            yield return Map(call.ResponseStream.Current);
    }

    private static async Task<bool> MoveNextAsync(IAsyncStreamReader<TelemetryEvent> stream,
        CancellationToken token)
    {
        try
        {
            return await stream.MoveNext(token).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && token.IsCancellationRequested)
        {
            throw new OperationCanceledException("Subscription cancelled.", ex, token);
        }
    }

    private static TelemetrySnapshot Map(TelemetryEvent value) => new(value.Revision,
        value.Status switch
        {
            LinkStatus.Waiting => TelemetryLinkStatus.Waiting,
            LinkStatus.Live => TelemetryLinkStatus.Live,
            LinkStatus.Stale => TelemetryLinkStatus.Stale,
            _ => throw new InvalidDataException($"Unknown link status: {value.Status}")
        },
        value.Telemetry is { } p
            ? new Position(p.LatitudeDeg, p.LongitudeDeg, p.AltitudeMslM,
                p.RelativeAltM, p.SpeedMps, p.HeadingDeg)
            : null);
}
