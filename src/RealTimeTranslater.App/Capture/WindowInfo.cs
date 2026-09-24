namespace RealTimeTranslater.App.Capture;

public sealed record WindowInfo(
    IntPtr Handle,
    string Title,
    string ProcessName)
{
    public string ProfileKey
        => string.IsNullOrWhiteSpace(ProcessName)
            ? Title.Trim().ToLowerInvariant()
            : ProcessName.Trim().ToLowerInvariant();

    public override string ToString()
        => string.IsNullOrWhiteSpace(ProcessName)
            ? Title
            : $"{Title}  [{ProcessName}]";
}
