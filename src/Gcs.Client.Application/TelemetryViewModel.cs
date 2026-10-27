using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Gcs.Client.Application;

public sealed class TelemetryViewModel(ITelemetrySource source) : INotifyPropertyChanged
{
    private int running;
    public TelemetrySnapshot? Latest { get; private set; }
    public bool IsRunning { get; private set; }
    public string? ErrorMessage { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    // Invoke from the UI thread in WPF. Await deliberately captures its context,
    // so all property notifications stay on that thread. Do not wrap in Task.Run.
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("A subscription is already running.");

        try
        {
            IsRunning = true;
            Notify(nameof(IsRunning));
            ErrorMessage = null;
            Notify(nameof(ErrorMessage));
            Latest = null;
            Notify(nameof(Latest));
            await foreach (var snapshot in source.SubscribeAsync(cancellationToken)
                .WithCancellation(cancellationToken))
            {
                Latest = snapshot;
                Notify(nameof(Latest));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A user-requested stop is normal completion.
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Notify(nameof(ErrorMessage));
        }
        finally
        {
            IsRunning = false;
            Interlocked.Exchange(ref running, 0);
            Notify(nameof(IsRunning));
        }
    }

    private void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
