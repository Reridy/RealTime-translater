namespace RealTimeTranslater.App.TextSources;

internal sealed class UnityAdapterSnapshotDto
{
    public int Protocol { get; set; } = 1;
    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }
    public List<UnityAdapterRegionDto> Regions { get; set; } = new();
}

internal sealed class UnityAdapterRegionDto
{
    public string Text { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string ObjectName { get; set; } = string.Empty;
    public string Hierarchy { get; set; } = string.Empty;
    public string SelectableName { get; set; } = string.Empty;
    public bool IsSelectable { get; set; }
    public bool IsButton { get; set; }
    public bool IsChoiceLike { get; set; }
    public bool IsSpeakerLike { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

internal sealed record UnityAdapterSnapshot(
    UnityAdapterSnapshotDto Data,
    long Version,
    DateTimeOffset ReceivedAt);
