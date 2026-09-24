using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RealTimeTranslater.App.TextSources;

public sealed class BrowserCompanionReceiver : IDisposable
{
    public const int Port = 47852;

    private const int MaximumHeaderBytes = 64 * 1024;
    private const int MaximumBodyBytes = 1024 * 1024;

    private readonly object _gate = new();
    private readonly JsonSerializerOptions _jsonOptions =
        new()
        {
            PropertyNameCaseInsensitive = true
        };

    private readonly Dictionary<string, BrowserCompanionSnapshot> _latestByPage =
        new(StringComparer.Ordinal);

    private TcpListener? _listener;

    public string? LastError { get; private set; }

    public async Task RunAsync(
        CancellationToken cancellationToken)
    {
        var listener =
            new TcpListener(
                IPAddress.Loopback,
                Port);

        try
        {
            listener.Start(
                backlog: 32);
        }
        catch (SocketException ex)
        {
            LastError =
                $"Browser Companion bridge unavailable: {ex.Message}";
            return;
        }

        _listener = listener;
        LastError = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client =
                        await listener.AcceptTcpClientAsync(
                            cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                _ = HandleClientAsync(
                    client,
                    cancellationToken);
            }
        }
        finally
        {
            _listener = null;

            try
            {
                listener.Stop();
            }
            catch
            {
            }
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

    public void Dispose()
    {
        try
        {
            _listener?.Stop();
        }
        catch
        {
        }
    }

    private async Task HandleClientAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;

                using var stream =
                    client.GetStream();

                var request =
                    await ReadRequestAsync(
                        stream,
                        cancellationToken);

                if (request is null)
                {
                    await WriteResponseAsync(
                        stream,
                        400,
                        cancellationToken);
                    return;
                }

                if (request.Method.Equals(
                        "OPTIONS",
                        StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(
                        stream,
                        204,
                        cancellationToken);
                    return;
                }

                if (!request.Method.Equals(
                        "POST",
                        StringComparison.OrdinalIgnoreCase) ||
                    !request.Path.Equals(
                        "/v1/push",
                        StringComparison.Ordinal))
                {
                    await WriteResponseAsync(
                        stream,
                        404,
                        cancellationToken);
                    return;
                }

                if (!IsTrustedBrowserBridge(
                        request.Headers))
                {
                    await WriteResponseAsync(
                        stream,
                        403,
                        cancellationToken);
                    return;
                }

                var snapshot =
                    JsonSerializer.Deserialize<
                        BrowserCompanionSnapshot>(
                        request.Body,
                        _jsonOptions);

                if (snapshot is null ||
                    snapshot.Protocol != 1 ||
                    snapshot.Regions.Count > 256)
                {
                    await WriteResponseAsync(
                        stream,
                        400,
                        cancellationToken);
                    return;
                }

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

                await WriteResponseAsync(
                    stream,
                    204,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                // The browser companion is optional. A malformed or aborted
                // localhost request must never disturb realtime translation.
            }
        }
    }

    private static async Task<LocalHttpRequest?> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var collected =
            new MemoryStream();
        var buffer =
            new byte[4096];

        var headerEnd = -1;

        while (collected.Length <
               MaximumHeaderBytes)
        {
            var read =
                await stream.ReadAsync(
                    buffer,
                    cancellationToken);

            if (read <= 0)
                return null;

            collected.Write(
                buffer,
                0,
                read);

            var data =
                collected.GetBuffer();

            headerEnd =
                FindHeaderEnd(
                    data,
                    checked((int)collected.Length));

            if (headerEnd >= 0)
                break;
        }

        if (headerEnd < 0)
            return null;

        var all =
            collected.ToArray();

        var headerText =
            Encoding.ASCII.GetString(
                all,
                0,
                headerEnd);

        var lines =
            headerText.Split(
                "\r\n",
                StringSplitOptions.None);

        if (lines.Length == 0)
            return null;

        var requestLine =
            lines[0].Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries);

        if (requestLine.Length < 2)
            return null;

        var headers =
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var line in
                 lines.Skip(1))
        {
            var separator =
                line.IndexOf(':');

            if (separator <= 0)
                continue;

            headers[
                line[..separator].Trim()] =
                line[(separator + 1)..]
                    .Trim();
        }

        if (requestLine[0].Equals(
                "OPTIONS",
                StringComparison.OrdinalIgnoreCase))
        {
            return new LocalHttpRequest(
                requestLine[0],
                requestLine[1],
                string.Empty,
                headers);
        }

        if (!headers.TryGetValue(
                "Content-Length",
                out var rawLength) ||
            !int.TryParse(
                rawLength,
                out var contentLength) ||
            contentLength < 0 ||
            contentLength >
                MaximumBodyBytes)
        {
            return null;
        }

        var body =
            new byte[contentLength];

        var bodyStart =
            headerEnd + 4;

        var alreadyRead =
            Math.Min(
                contentLength,
                Math.Max(
                    0,
                    all.Length -
                    bodyStart));

        if (alreadyRead > 0)
        {
            Buffer.BlockCopy(
                all,
                bodyStart,
                body,
                0,
                alreadyRead);
        }

        var offset =
            alreadyRead;

        while (offset <
               contentLength)
        {
            var read =
                await stream.ReadAsync(
                    body.AsMemory(
                        offset,
                        contentLength -
                        offset),
                    cancellationToken);

            if (read <= 0)
                return null;

            offset += read;
        }

        return new LocalHttpRequest(
            requestLine[0],
            requestLine[1],
            Encoding.UTF8.GetString(
                body),
            headers);
    }

    private static int FindHeaderEnd(
        byte[] data,
        int length)
    {
        for (var i = 0;
             i <= length - 4;
             i++)
        {
            if (data[i] == '\r' &&
                data[i + 1] == '\n' &&
                data[i + 2] == '\r' &&
                data[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int statusCode,
        CancellationToken cancellationToken)
    {
        var reason =
            statusCode switch
            {
                204 => "No Content",
                400 => "Bad Request",
                403 => "Forbidden",
                404 => "Not Found",
                _ => "OK"
            };

        var response =
            $"HTTP/1.1 {statusCode} {reason}\r\n" +
            "Access-Control-Allow-Headers: Content-Type, X-RealTime-Translater\r\n" +
            "Access-Control-Allow-Methods: POST, OPTIONS\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n" +
            "Content-Length: 0\r\n\r\n";

        await stream.WriteAsync(
            Encoding.ASCII.GetBytes(
                response),
            cancellationToken);
    }

    private static bool IsTrustedBrowserBridge(
        IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue(
                "X-RealTime-Translater",
                out var bridge) ||
            !string.Equals(
                bridge,
                "browser-companion-v1",
                StringComparison.Ordinal))
        {
            return false;
        }

        if (!headers.TryGetValue(
                "Origin",
                out var origin) ||
            string.IsNullOrWhiteSpace(origin))
        {
            // MV3 service-worker requests can omit Origin. The non-forgeable
            // custom header still prevents accidental page-origin posts.
            return true;
        }

        return origin.StartsWith(
                "chrome-extension://",
                StringComparison.OrdinalIgnoreCase) ||
            origin.StartsWith(
                "moz-extension://",
                StringComparison.OrdinalIgnoreCase);
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

    private sealed record LocalHttpRequest(
        string Method,
        string Path,
        string Body,
        IReadOnlyDictionary<string, string> Headers);
}
