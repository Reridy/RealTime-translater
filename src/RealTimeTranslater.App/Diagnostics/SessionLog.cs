using System.Globalization;

namespace RealTimeTranslater.App.Diagnostics;

internal static class SessionLog
{
    private static readonly object Gate = new();

    internal static string FilePath { get; } =
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "RealTimeTranslater",
            "logs",
            "latest.log");

    internal static void Initialize()
    {
        lock (Gate)
        {
            try
            {
                var directory =
                    Path.GetDirectoryName(
                        FilePath)!;

                Directory.CreateDirectory(
                    directory);

                if (File.Exists(FilePath) &&
                    new FileInfo(FilePath).Length >
                        1024 * 1024)
                {
                    var previous =
                        Path.Combine(
                            directory,
                            "previous.log");

                    File.Move(
                        FilePath,
                        previous,
                        overwrite: true);
                }

                File.AppendAllText(
                    FilePath,
                    Environment.NewLine +
                    "=== RealTime Translater session " +
                    DateTimeOffset.Now.ToString(
                        "yyyy-MM-dd HH:mm:ss zzz",
                        CultureInfo.InvariantCulture) +
                    " ===" +
                    Environment.NewLine);
            }
            catch
            {
                // Diagnostics must never prevent the app from starting.
            }
        }
    }

    internal static void Status(
        string message)
        => Write(
            "STATUS",
            message);

    internal static void Warning(
        string message)
        => Write(
            "WARN",
            message);

    internal static void Error(
        Exception exception)
        => Write(
            "ERROR",
            exception.ToString());

    internal static void Write(
        string level,
        string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(
                    Path.GetDirectoryName(
                        FilePath)!);

                var sanitized =
                    message
                        .Replace(
                            "\r",
                            " ")
                        .Replace(
                            "\n",
                            " ")
                        .Trim();

                File.AppendAllText(
                    FilePath,
                    $"[{DateTimeOffset.Now:HH:mm:ss.fff}] {level}: {sanitized}" +
                    Environment.NewLine);
            }
            catch
            {
                // Logging remains best-effort and contains no captured game
                // frames or recognized dialogue text.
            }
        }
    }
}
