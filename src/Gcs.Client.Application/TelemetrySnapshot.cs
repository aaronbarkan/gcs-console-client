namespace Gcs.Client.Application;

public enum TelemetryLinkStatus { Waiting, Live, Stale }

public sealed record Position(
    double LatitudeDeg, double LongitudeDeg, double AltitudeMslM,
    double RelativeAltM, double SpeedMps, double HeadingDeg);

// Immutable state, independent of protobuf and any specific UI framework.
public sealed record TelemetrySnapshot(
    ulong Revision, TelemetryLinkStatus Status, Position? Telemetry);

public interface ITelemetrySource
{
    IAsyncEnumerable<TelemetrySnapshot> SubscribeAsync(CancellationToken cancellationToken);
}
