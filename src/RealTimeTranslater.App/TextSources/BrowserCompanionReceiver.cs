using System.Net;
using System.Text;
using System.Text.Json;

namespace RealTimeTranslater.App.TextSources;

public sealed class BrowserCompanionReceiver : IDisposable
{
    public const int Port = 47852;

    private readonly object _gate = new();
    private readonly JsonSerializerOptions _jsonOptions =
        new()
        {
            PropertyNameCaseInsensitive = true
        };

    private BrowserCompanionSnapshot? _latest;
    private HttpListener? _listener;

    public async Task RunAsync(
        CancellationToken cancellationToken)
    {
        using var listener =
            new HttpListener();

        listener.Prefixes.Add(
            $"http://127.0.0.1:{Port}/");

        listener.Start();
        _listener = listener;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;

                try
                {
                    context =
                        await listener
                            .GetContextAsync()
                            .WaitAsync(
                                cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpListenerException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                _ = HandleAsync(
                    context,
                    cancellationToken);
            }
        }
        finally
        {
            _listener = null;
        }
    }

    public bool TryGetLatest(
        TimeSpan maxAge,
        out BrowserCompanionSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_latest is not null &&
                DateTimeOffset.UtcNow -
                _latest.ReceivedAt <=
                maxAge)
            {
                snapshot = _latest;
                return true;
            }
        }

        snapshot = null!;
        return false;
    }

    public void Dispose()
    {
        try
        {
            _listener?.Close();
        }
        catch
        {
        }
    }

    private async Task HandleAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        var response =
            context.Response;

        response.Headers[
            "Access-Control-Allow-Origin"] = "*";
        response.Headers[
            "Access-Control-Allow-Headers"] =
            "Content-Type";
        response.Headers[
            "Access-Control-Allow-Methods"] =
            "POST, OPTIONS";

        if (context.Request.HttpMethod.Equals(
                "OPTIONS",
                StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = 204;
            response.Close();
            return;
        }

        if (!context.Request.HttpMethod.Equals(
                "POST",
                StringComparison.OrdinalIgnoreCase) ||
            !context.Request.Url!.AbsolutePath.Equals(
                "/v1/push",
                StringComparison.Ordinal))
        {
            response.StatusCode = 404;
            response.Close();
            return;
        }

        try
        {
            using var reader =
                new StreamReader(
                    context.Request.InputStream,
                    Encoding.UTF8);

            var json =
                await reader.ReadToEndAsync(
                    cancellationToken);

            var snapshot =
                JsonSerializer.Deserialize<
                    BrowserCompanionSnapshot>(
                    json,
                    _jsonOptions);

            if (snapshot is null ||
                snapshot.Protocol != 1 ||
                snapshot.Regions.Count > 256)
            {
                response.StatusCode = 400;
            }
            else
            {
                snapshot.ReceivedAt =
                    DateTimeOffset.UtcNow;

                snapshot.Regions =
                    snapshot.Regions
                        .Where(region =>
                            !string.IsNullOrWhiteSpace(
                                region.Text))
                        .Take(128)
                        .ToList();

                lock (_gate)
                {
                    _latest = snapshot;
                }

                response.StatusCode = 204;
            }
        }
        catch
        {
            response.StatusCode = 400;
        }
        finally
        {
            response.Close();
        }
    }
}
