using System.Text.Json.Serialization;

namespace RealTimeTranslater.App.TextSources;

public sealed class BrowserCompanionSnapshot
{
    public int Protocol { get; set; } = 1;
    public string Kind { get; set; } = "dom";
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public bool Visible { get; set; }
    public long TimestampUnixMs { get; set; }
    public int InnerWidth { get; set; }
    public int InnerHeight { get; set; }
    public int OuterWidth { get; set; }
    public int OuterHeight { get; set; }
    public double DevicePixelRatio { get; set; } = 1.0;
    public List<BrowserCompanionRegion> Regions { get; set; } = new();

    [JsonIgnore]
    public DateTimeOffset ReceivedAt { get; set; }
}

public sealed class BrowserCompanionRegion
{
    public string Text { get; set; } = string.Empty;
    public string Role { get; set; } = "text";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Partial { get; set; }
}
