using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RealTimeTranslater.App.TextSources;

internal sealed class UnityAdapterReceiver
{
    internal const int Port = 47851;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();

    private UnityAdapterSnapshot? _latest;
    private string? _lastPayload;
    private long _version;

    public string? LastError { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, Port);

        try
        {
            listener.Start();

            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient? client = null;

                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken);
                    client.NoDelay = true;

                    using (client)
                    using (var stream = client.GetStream())
                    using (var reader = new StreamReader(
                        stream,
                        new UTF8Encoding(false),
                        detectEncodingFromByteOrderMarks: true,
                        bufferSize: 8192,
                        leaveOpen: false))
                    {
                        LastError = null;

                        while (client.Connected &&
                               !cancellationToken.IsCancellationRequested)
                        {
                            var line = await reader.ReadLineAsync(cancellationToken);
                            if (line is null)
                                break;

                            if (line.Length == 0 || line.Length > 1_000_000)
                                continue;

                            Receive(line);
                        }
                    }
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (IOException ex)
                {
                    LastError = ex.Message;
                }
                catch (SocketException ex)
                {
                    LastError = ex.Message;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                }
                finally
                {
                    client?.Dispose();
                }

                try
                {
                    await Task.Delay(100, cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    public bool TryGetLatest(
        TimeSpan maximumAge,
        out UnityAdapterSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_latest is null ||
                DateTimeOffset.UtcNow - _latest.ReceivedAt > maximumAge)
            {
                snapshot = null!;
                return false;
            }

            snapshot = _latest;
            return true;
        }
    }

    private void Receive(string payload)
    {
        UnityAdapterSnapshotDto? dto;

        try
        {
            dto = JsonSerializer.Deserialize<UnityAdapterSnapshotDto>(
                payload,
                JsonOptions);
        }
        catch (JsonException ex)
        {
            LastError = $"Invalid Unity adapter JSON: {ex.Message}";
            return;
        }

        if (dto is null ||
            dto.Protocol is < 1 or > 2 ||
            dto.ScreenWidth < 1 ||
            dto.ScreenHeight < 1)
        {
            return;
        }

        dto.Regions ??= new List<UnityAdapterRegionDto>();

        lock (_gate)
        {
            if (!string.Equals(payload, _lastPayload, StringComparison.Ordinal))
            {
                _lastPayload = payload;
                _version++;
            }

            _latest = new UnityAdapterSnapshot(
                dto,
                _version,
                DateTimeOffset.UtcNow);

            LastError = null;
        }
    }
}
