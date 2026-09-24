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

    // Optional protocol 2+ layout/style metadata. Older adapters leave these
    // at their default values and the desktop app falls back gracefully.
    public int LayoutX { get; set; }
    public int LayoutY { get; set; }
    public int LayoutWidth { get; set; }
    public int LayoutHeight { get; set; }
    public int ForegroundArgb { get; set; }
    public int SourceLineCount { get; set; }
    public string SourceAlignment { get; set; } = string.Empty;
}

internal sealed record UnityAdapterSnapshot(
    UnityAdapterSnapshotDto Data,
    long Version,
    DateTimeOffset ReceivedAt);
