using Avalonia.Media;
using USCSandbox.Common;
using USCSandbox.Processor;

namespace USCSandbox.Gui;

public enum LogLevel
{
    Info,
    Detail,
    Success,
    Warning,
    Error
}

/// <summary>
/// One line in the log pane. Kept as an object rather than raw text so lines can be coloured.
/// </summary>
public class LogEntry
{
    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#D6D8DD"));
    private static readonly IBrush DetailBrush = new SolidColorBrush(Color.Parse("#8B909A"));
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#69D18B"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#E3B341"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#F0736A"));

    public LogEntry(string text, LogLevel level = LogLevel.Info)
    {
        Text = text;
        Level = level;
    }

    public string Text { get; }
    public LogLevel Level { get; }

    public IBrush Foreground => Level switch
    {
        LogLevel.Detail => DetailBrush,
        LogLevel.Success => SuccessBrush,
        LogLevel.Warning => WarningBrush,
        LogLevel.Error => ErrorBrush,
        _ => InfoBrush
    };

    /// <summary>
    /// Guesses a level from an exporter log line so the shared exporter doesn't need to know
    /// anything about the GUI.
    /// </summary>
    public static LogEntry FromExporterLine(string line)
    {
        // a skip still mentions the failure that caused it, so check for it first
        if (line.Contains("Skipping", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("stripped", StringComparison.OrdinalIgnoreCase))
            return new LogEntry(line, LogLevel.Warning);

        if (line.Contains("Failed", StringComparison.OrdinalIgnoreCase))
            return new LogEntry(line, LogLevel.Error);

        if (line.StartsWith("  Exported:", StringComparison.Ordinal))
            return new LogEntry(line, LogLevel.Detail);

        if (line.StartsWith("Done.", StringComparison.Ordinal))
            return new LogEntry(line, LogLevel.Success);

        return new LogEntry(line);
    }
}

/// <summary>
/// An entry in the "Dropped" list. A null <see cref="GPUPlatform"/> on the platform picker and
/// a directory here both mean "work it out yourself".
/// </summary>
public class InputEntry
{
    public InputEntry(string path)
    {
        Path = path;
        IsDirectory = Directory.Exists(path);
        Display = IsDirectory
            ? new DirectoryInfo(path).Name
            : System.IO.Path.GetFileName(path);

        if (string.IsNullOrEmpty(Display))
            Display = path;

        Kind = IsDirectory
            ? "Folder"
            : ShaderExporter.DetectFileType(path) switch
            {
                UnityFileType.Bundle => "Asset bundle",
                UnityFileType.Assets => "Assets file",
                _ => "Unrecognised file"
            };
    }

    public string Path { get; }
    public string Display { get; }
    public string Kind { get; }
    public bool IsDirectory { get; }
}

public class PlatformChoice
{
    public PlatformChoice(string label, GPUPlatform? platform)
    {
        Label = label;
        Platform = platform;
    }

    public string Label { get; }
    public GPUPlatform? Platform { get; }

    public override string ToString() => Label;
}
