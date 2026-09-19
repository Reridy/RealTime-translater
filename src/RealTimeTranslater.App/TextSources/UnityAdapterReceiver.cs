using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace RealTimeTranslater.App.TextSources;

internal sealed class UnityAdapterReceiver
{
    internal const string PipeName = "RealTimeTranslater.UnityText.v1";

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
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(cancellationToken);

                using var reader = new StreamReader(
                    pipe,
                    new UTF8Encoding(false),
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 8192,
                    leaveOpen: true);

                while (pipe.IsConnected &&
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
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException ex)
            {
                LastError = ex.Message;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }

            try
            {
                await Task.Delay(250, cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
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
            dto.Protocol != 1 ||
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
