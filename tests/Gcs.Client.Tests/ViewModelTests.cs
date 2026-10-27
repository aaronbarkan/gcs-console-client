using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Gcs.Client.Application;
using Gcs.Client.ConsoleApp;

namespace Gcs.Client.Tests;

public class ViewModelTests
{
    private static readonly Position Position = new(32, 34.8, 100, 50, 5, 90);

    [Fact]
    public async Task PublishesEveryUpdateIncludingStaleAtSameRevisionAndServerRestart()
    {
        var snapshots = new[] {
            new TelemetrySnapshot(0, TelemetryLinkStatus.Waiting, null),
            new TelemetrySnapshot(7, TelemetryLinkStatus.Live, Position),
            new TelemetrySnapshot(7, TelemetryLinkStatus.Stale, Position),
            new TelemetrySnapshot(1, TelemetryLinkStatus.Live, Position)
        };
        var vm = new TelemetryViewModel(new SequenceSource(snapshots));
        var seen = new List<TelemetrySnapshot>();
        var running = new List<bool>();
        vm.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(vm.Latest) && vm.Latest is { } s) seen.Add(s);
            if (e.PropertyName == nameof(vm.IsRunning)) running.Add(vm.IsRunning);
        };
        await vm.RunAsync();
        Assert.Equal(snapshots, seen);
        Assert.Equal(new[] { true, false }, running);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(snapshots[^1], vm.Latest);
    }

    [Fact]
    public async Task CancelStopsPendingReadAndDisposesSubscriptionWithoutError()
    {
        var source = new PendingSource();
        var vm = new TelemetryViewModel(source);
        using var stop = new CancellationTokenSource();
        var run = vm.RunAsync(stop.Token);
        Assert.True(vm.IsRunning);
        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.RunAsync());
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(source.Disposed);
        Assert.False(vm.IsRunning);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task FailureIsObservableAndAnotherRunClearsError()
    {
        var source = new SequenceSource([]) { Failure = new IOException("Connection lost") };
        var vm = new TelemetryViewModel(source);
        await vm.RunAsync();
        Assert.Equal("Connection lost", vm.ErrorMessage);
        Assert.False(vm.IsRunning);
        source.Failure = null;
        await vm.RunAsync();
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task UnrequestedCancellationIsReportedAsFailure()
    {
        var vm = new TelemetryViewModel(new SequenceSource([]) { Failure = new OperationCanceledException("Remote stop") });
        await vm.RunAsync();
        Assert.Equal("Remote stop", vm.ErrorMessage);
    }

    [Fact]
    public async Task ConsolePrintsWaitingAndAllPositionFieldsAndUnsubscribes()
    {
        var vm = new TelemetryViewModel(new SequenceSource([
            new(0, TelemetryLinkStatus.Waiting, null), new(1, TelemetryLinkStatus.Live, Position)]));
        using var output = new StringWriter(System.Globalization.CultureInfo.GetCultureInfo("fr-FR"));
        using var error = new StringWriter();
        var view = new ConsoleView(vm, output, error);
        await vm.RunAsync();
        Assert.Equal($"WAITING rev=0{Environment.NewLine}LIVE rev=1 lat=32.0000000 lon=34.8000000 alt_msl_m=100.00 rel_alt_m=50.00 speed_mps=5.00 hdg_deg=90.00{Environment.NewLine}", output.ToString());
        Assert.Empty(error.ToString());
        view.Dispose();
        var previous = output.ToString();
        await vm.RunAsync();
        Assert.Equal(previous, output.ToString());
    }

    [Fact]
    public async Task ConsoleWritesFailureToStderr()
    {
        var vm = new TelemetryViewModel(new SequenceSource([]) { Failure = new IOException("offline") });
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var view = new ConsoleView(vm, output, error);
        await vm.RunAsync();
        Assert.Empty(output.ToString());
        Assert.Contains("Subscription failed: offline", error.ToString());
    }

    private sealed class SequenceSource(TelemetrySnapshot[] snapshots) : ITelemetrySource
    {
        public Exception? Failure { get; set; }
        public async IAsyncEnumerable<TelemetrySnapshot> SubscribeAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (Failure is { } ex) throw ex;
            foreach (var snapshot in snapshots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return snapshot;
            }
        }
    }

    private sealed class PendingSource : ITelemetrySource
    {
        public bool Disposed { get; private set; }
        public async IAsyncEnumerable<TelemetrySnapshot> SubscribeAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var s in Channel.CreateUnbounded<TelemetrySnapshot>().Reader.ReadAllAsync(cancellationToken))
                    yield return s;
            }
            finally { Disposed = true; }
        }
    }
}
