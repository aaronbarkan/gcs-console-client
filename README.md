# GCS Console Client

Minimal .NET 10 console MVVM client for gcs-core's `SubscribeTelemetry` gRPC
server stream. Prints each notification, including WAITING without a position
and STALE with the last position. There is no client-side polling or history.

## Build and run

Install the .NET 10 SDK, then run from this directory:

```sh
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
```

Start Core separately (see the adjacent `gcs-core/README.md`), then:

```sh
dotnet run --project src/Gcs.Client.Console -c Release --no-build
# Optional server address:
dotnet run --project src/Gcs.Client.Console -c Release --no-build -- http://127.0.0.1:50051
```

Default endpoint: `http://localhost:50051`, matching Core's plaintext HTTP/2
listener. HTTPS addresses are also accepted for a TLS-enabled server. Use `--help`
for usage. Press Ctrl-C to cancel and dispose the stream and channel.

Telemetry goes to stdout; startup and errors go to stderr. Exit codes: 0 for
local cancellation or graceful stream completion, 1 for subscription failure,
2 for invalid arguments. A server shutdown may complete or fail the stream;
the app exits accordingly. It does not automatically reconnect; restart it to
subscribe again. No timeout is imposed on the long-lived subscription.

Example output:

```text
WAITING rev=0
LIVE rev=1 lat=32.0000000 lon=34.8000000 alt_msl_m=100.00 rel_alt_m=50.00 speed_mps=5.00 hdg_deg=90.00
STALE rev=1 lat=32.0000000 lon=34.8000000 alt_msl_m=100.00 rel_alt_m=50.00 speed_mps=5.00 hdg_deg=90.00
```

Core streams its latest state; intermediate packets may be coalesced. A status
change can reuse the previous revision, and revisions reset when Core restarts.
The client prints every delivered event without assuming increasing revisions.

## Architecture and WPF migration

- `src/Gcs.Client.Application`: immutable `TelemetrySnapshot`/`Position` Model,
  `ITelemetrySource` boundary, and `TelemetryViewModel`. Depends only on .NET.
- `src/Gcs.Client.Grpc`: generated protocol client and transport adapter. Maps
  protobuf into the Model, disposes each stream, and normalizes local gRPC
  cancellation into `OperationCanceledException`.
- `src/Gcs.Client.Console`: `ConsoleView` observes `INotifyPropertyChanged` and
  formats output; `Program` wires dependencies, endpoint, cancellation and exit code.
- `tests/Gcs.Client.Tests`: xUnit tests for ViewModel, transport mapping/lifetime,
  and console rendering/error handling.

For WPF, reference Application and Grpc from a `net10.0-windows` WPF project.
Set `TelemetryViewModel` as the DataContext and bind to `Latest.Status`,
`Latest.Revision`, `Latest.Telemetry.LatitudeDeg` (and other position properties),
`IsRunning`, and `ErrorMessage`. `Latest` is null before the first event and its
`Telemetry` is null while WAITING. `IsRunning` describes the subscription task,
not aircraft link health; use `Latest.Status` for the latter.

Call and await `RunAsync(token)` from the WPF UI thread, and cancel on view/window
closure. The ViewModel preserves the caller's synchronization context across
awaits, so notifications stay on the UI thread. Do not start it with `Task.Run`.
Only one subscription can run per ViewModel; another run is allowed after it
stops and clears the previous error/state. The WPF composition root should own
the channel lifetime, as the console Program does. WPF-specific commands, XAML,
and window lifecycle wiring belong in that later host, not this library.

## Protocol ownership

`proto/telemetry.proto` is a byte-for-byte copy of Core's contract, including its
`Gcs.Client` generated namespace. It is included locally so the client builds
without requiring a sibling checkout. `Grpc.Tools` generates C# at build time;
generated files are not committed. Core remains the source of truth. After a
contract change, copy it again and rebuild:

```sh
cp ../gcs-core/proto/telemetry.proto proto/telemetry.proto
dotnet build -c Release
```

Package versions and transitive dependency lock files are checked in. Use
`dotnet restore --locked-mode` for reproducible package resolution.

## Real Core integration test

Requires Python 3, the adjacent native Core binary at `build-mac/gcs-core`, and
the adjacent simulator's existing `.venv/bin/python` with pymavlink installed.
Build Core and prepare the simulator using their READMEs first. Stop other
processes on TCP 50051 / UDP 14550 before running:

```sh
dotnet build -c Release
python3 scripts/integration.py
# Optional checkout locations:
python3 scripts/integration.py --core-dir ../gcs-core --simulator-dir ../uav-simulator
```

The script verifies the proto matches Core, starts the real C++ server and MAVLink
simulator, runs the built C# console application, and checks WAITING, moving LIVE
positions/units/revisions, STALE retention, resume, Ctrl-C exit, a new subscription,
server shutdown, offline failure, and invalid endpoint handling. It refuses
occupied ports and cleans up its processes in `finally`. Core runs in a temporary
working directory so its telemetry log does not overwrite your existing log.

Verified on 2026-10-27 with macOS Apple Silicon and .NET SDK 10.0.401:
Release build, 14 client unit tests, the real Core/simulator integration script,
and all three native Core CTest suites passed. WPF itself is a future iteration
and has not been run on Windows.

Streaming call disposal/cancellation follows the official
[.NET gRPC client guidance](https://learn.microsoft.com/en-us/aspnet/core/grpc/client).
