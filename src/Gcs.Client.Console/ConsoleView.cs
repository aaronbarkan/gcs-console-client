using System.ComponentModel;
using Gcs.Client.Application;

namespace Gcs.Client.ConsoleApp;

public sealed class ConsoleView : IDisposable
{
    private readonly TelemetryViewModel viewModel;
    private readonly TextWriter output;
    private readonly TextWriter error;

    public ConsoleView(TelemetryViewModel viewModel, TextWriter output, TextWriter error)
    {
        this.viewModel = viewModel;
        this.output = output;
        this.error = error;
        viewModel.PropertyChanged += OnPropertyChanged;
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TelemetryViewModel.Latest) && viewModel.Latest is { } s)
        {
            var line = $"{s.Status.ToString().ToUpperInvariant()} rev={s.Revision}";
            if (s.Telemetry is { } p)
                line += FormattableString.Invariant(
                    $" lat={p.LatitudeDeg:F7} lon={p.LongitudeDeg:F7} alt_msl_m={p.AltitudeMslM:F2} rel_alt_m={p.RelativeAltM:F2} speed_mps={p.SpeedMps:F2} hdg_deg={p.HeadingDeg:F2}");
            output.WriteLine(line);
            output.Flush();
        }
        if (e.PropertyName == nameof(TelemetryViewModel.ErrorMessage) && viewModel.ErrorMessage is { } message)
            error.WriteLine($"Subscription failed: {message}");
    }

    public void Dispose() => viewModel.PropertyChanged -= OnPropertyChanged;
}
