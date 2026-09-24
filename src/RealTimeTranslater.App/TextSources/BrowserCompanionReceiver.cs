using System.IO;
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

    private readonly Dictionary<string, BrowserCompanionSnapshot> _latestByPage =
        new(StringComparer.Ordinal);
    private HttpListener? _listener;

    public async Task RunAsync(
        CancellationToken cancellationToken)
    {
        using var listener =
            new HttpListener();

        listener.Prefixes.Add(
            $"http://127.0.0.1:{Port}/");

        try
        {
            listener.Start();
        }
        catch (HttpListenerException)
        {
            return;
        }

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
        string targetTitle,
        out BrowserCompanionSnapshot snapshot)
    {
        lock (_gate)
        {
            var now =
                DateTimeOffset.UtcNow;

            var expired =
                _latestByPage
                    .Where(pair =>
                        now -
                        pair.Value.ReceivedAt >
                        maxAge)
                    .Select(pair =>
                        pair.Key)
                    .ToArray();

            foreach (var key in expired)
                _latestByPage.Remove(key);

            var candidates =
                _latestByPage.Values
                    .Where(item =>
                        item.Visible)
                    .OrderByDescending(item =>
                        TitleScore(
                            targetTitle,
                            item.Title))
                    .ThenByDescending(item =>
                        item.ReceivedAt)
                    .ToArray();

            if (candidates.Length > 0)
            {
                snapshot =
                    candidates[0];
                return true;
            }
        }

        snapshot = null!;
        return false;
    }

    private static int TitleScore(
        string targetTitle,
        string browserTitle)
    {
        if (string.IsNullOrWhiteSpace(
                targetTitle) ||
            string.IsNullOrWhiteSpace(
                browserTitle))
        {
            return 0;
        }

        var target =
            targetTitle.Trim();
        var browser =
            browserTitle.Trim();

        if (target.Contains(
                browser,
                StringComparison.OrdinalIgnoreCase) ||
            browser.Contains(
                target,
                StringComparison.OrdinalIgnoreCase))
        {
            return 1000 +
                Math.Min(
                    target.Length,
                    browser.Length);
        }

        var max =
            Math.Min(
                target.Length,
                browser.Length);
        var prefix = 0;

        while (prefix < max &&
               char.ToUpperInvariant(
                   target[prefix]) ==
               char.ToUpperInvariant(
                   browser[prefix]))
        {
            prefix++;
        }

        return prefix;
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

                var pageKey =
                    snapshot.Title +
                    "\u001f" +
                    snapshot.Url;

                lock (_gate)
                {
                    if (snapshot.Visible)
                    {
                        _latestByPage[
                            pageKey] =
                            snapshot;
                    }
                    else
                    {
                        _latestByPage.Remove(
                            pageKey);
                    }
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
