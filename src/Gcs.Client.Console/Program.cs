using Gcs.Client;
using Gcs.Client.Application;
using Gcs.Client.ConsoleApp;
using Gcs.Client.Grpc;
using Grpc.Net.Client;

if (args is ["--help"] or ["-h"])
{
    Console.WriteLine("Usage: gcs-client [http://localhost:50051]\nPress Ctrl-C to stop.");
    return 0;
}
if (args.Length > 1 || !Uri.TryCreate(args.FirstOrDefault() ?? "http://localhost:50051",
        UriKind.Absolute, out var endpoint) ||
    (endpoint.Scheme != "http" && endpoint.Scheme != "https") ||
    endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 ||
    endpoint.Fragment.Length != 0 || endpoint.UserInfo.Length != 0)
{
    Console.Error.WriteLine("Usage: gcs-client [http://localhost:50051] (HTTP or HTTPS server address)");
    return 2;
}

using var stop = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
Console.CancelKeyPress += cancel;
try
{
    using var channel = GrpcChannel.ForAddress(endpoint);
    var source = new GrpcTelemetrySource(new TelemetryService.TelemetryServiceClient(channel));
    var viewModel = new TelemetryViewModel(source);
    using var view = new ConsoleView(viewModel, Console.Out, Console.Error);
    Console.Error.WriteLine($"Subscribing to {endpoint} — Ctrl-C to stop.");
    await viewModel.RunAsync(stop.Token);
    return viewModel.ErrorMessage is null ? 0 : 1;
}
finally
{
    Console.CancelKeyPress -= cancel;
}
