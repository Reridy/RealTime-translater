namespace RealTimeTranslater.App.Capture;

public sealed record WindowInfo(IntPtr Handle, string Title)
{
    public override string ToString() => Title;
}
