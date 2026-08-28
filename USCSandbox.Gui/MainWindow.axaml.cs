using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using USCSandbox.Common;
using USCSandbox.Processor;
using UnityVersion = AssetRipper.Primitives.UnityVersion;

namespace USCSandbox.Gui;

public partial class MainWindow : Window
{
    private const int MaxLogLines = 5000;

    private static readonly IBrush DropIdleFill = new SolidColorBrush(Color.Parse("#1A1C20"));
    private static readonly IBrush DropIdleStroke = new SolidColorBrush(Color.Parse("#343841"));
    private static readonly IBrush DropActiveFill = new SolidColorBrush(Color.Parse("#1B2436"));
    private static readonly IBrush DropActiveStroke = new SolidColorBrush(Color.Parse("#3D6FF5"));

    private readonly ObservableCollection<InputEntry> _inputs = [];
    private readonly ObservableCollection<LogEntry> _log = [];
    private readonly ConcurrentQueue<LogEntry> _pendingLog = new();
    private readonly DispatcherTimer _logFlushTimer;

    private CancellationTokenSource? _cts;
    private bool _running;

    public MainWindow()
    {
        InitializeComponent();

        InputList.ItemsSource = _inputs;
        LogList.ItemsSource = _log;

        PlatformBox.ItemsSource = new List<PlatformChoice>
        {
            new("Auto (best available)", null),
            new("OpenGL ES 3", GPUPlatform.gles3),
            new("Vulkan", GPUPlatform.vulkan),
            new("DirectX 11", GPUPlatform.d3d11),
            new("Switch (NVN)", GPUPlatform.Switch),
            new("Metal", GPUPlatform.metal)
        };
        PlatformBox.SelectedIndex = 0;

        OutputBox.Text = Path.Combine(AppContext.BaseDirectory, "Shaders");

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        DropZone.PointerPressed += async (_, _) => await PickFilesAsync();
        AddFilesButton.Click += async (_, _) => await PickFilesAsync();
        AddFolderButton.Click += async (_, _) => await PickFolderAsync();
        ClearButton.Click += OnClear;
        CopyLogButton.Click += OnCopyLog;
        BrowseOutputButton.Click += async (_, _) => await PickOutputAsync();
        OpenOutputButton.Click += OnOpenOutput;
        RunButton.Click += OnRun;
        CancelButton.Click += OnCancel;
        InputList.KeyDown += OnInputListKeyDown;

        _inputs.CollectionChanged += (_, _) => UpdateQueueState();

        // The exporter logs from a worker thread and can produce thousands of lines, so batch
        // them instead of posting to the dispatcher once per shader.
        _logFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _logFlushTimer.Tick += (_, _) => FlushLog();
        _logFlushTimer.Start();

        UpdateQueueState();
    }

    #region drag and drop

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasFiles = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = hasFiles && !_running ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropZoneActive(hasFiles && !_running);
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        SetDropZoneActive(false);
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        SetDropZoneActive(false);
        e.Handled = true;

        if (_running)
            return;

        var items = e.DataTransfer.TryGetFiles();
        if (items is null)
            return;

        var paths = new List<string>();
        foreach (var item in items)
        {
            var local = item.TryGetLocalPath();
            if (!string.IsNullOrEmpty(local))
                paths.Add(local);
        }

        AddInputs(paths);
    }

    private void SetDropZoneActive(bool active)
    {
        DropZoneOutline.Fill = active ? DropActiveFill : DropIdleFill;
        DropZoneOutline.Stroke = active ? DropActiveStroke : DropIdleStroke;
        DropZoneTitle.Text = active ? "Release to add" : "Drop bundles here";
    }

    #endregion

    #region input queue

    private void AddInputs(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths)
        {
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch
            {
                continue;
            }

            if (!File.Exists(full) && !Directory.Exists(full))
                continue;

            if (_inputs.Any(i => string.Equals(i.Path, full, StringComparison.OrdinalIgnoreCase)))
                continue;

            _inputs.Add(new InputEntry(full));
            added++;
        }

        if (added > 0)
            Log($"Added {added} item(s).");
    }

    private void OnInputListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || _running)
            return;

        foreach (var selected in InputList.SelectedItems?.Cast<InputEntry>().ToList() ?? [])
            _inputs.Remove(selected);

        e.Handled = true;
    }

    private void OnClear(object? sender, RoutedEventArgs e)
    {
        if (_running)
            return;

        _inputs.Clear();
        _log.Clear();
        RunProgress.Value = 0;
    }

    private void UpdateQueueState()
    {
        QueueHeader.Text = $"DROPPED ({_inputs.Count})";
        RunButton.IsEnabled = _inputs.Count > 0 && !_running;

        if (!_running)
        {
            StatusText.Text = _inputs.Count == 0
                ? "Waiting for files."
                : $"{_inputs.Count} item(s) ready.";
        }
    }

    #endregion

    #region pickers

    private async Task PickFilesAsync()
    {
        if (_running)
            return;

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Pick asset bundles or assets files",
                AllowMultiple = true
            });

            AddInputs(files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p))!);
        }
        catch (Exception ex)
        {
            Log($"Couldn't open the file picker: {ex.Message}", LogLevel.Error);
        }
    }

    private async Task PickFolderAsync()
    {
        if (_running)
            return;

        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Pick a folder to scan",
                AllowMultiple = true
            });

            AddInputs(folders.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p))!);
        }
        catch (Exception ex)
        {
            Log($"Couldn't open the folder picker: {ex.Message}", LogLevel.Error);
        }
    }

    private async Task PickOutputAsync()
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Pick an output folder",
                AllowMultiple = false
            });

            var picked = folders.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(picked))
                OutputBox.Text = picked;
        }
        catch (Exception ex)
        {
            Log($"Couldn't open the folder picker: {ex.Message}", LogLevel.Error);
        }
    }

    private void OnOpenOutput(object? sender, RoutedEventArgs e)
    {
        var dir = OutputBox.Text?.Trim();
        if (string.IsNullOrEmpty(dir))
            return;

        try
        {
            Directory.CreateDirectory(dir);

            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", dir);
            else
                Process.Start("xdg-open", dir);
        }
        catch (Exception ex)
        {
            Log($"Couldn't open {dir}: {ex.Message}", LogLevel.Error);
        }
    }

    #endregion

    #region export

    private async void OnRun(object? sender, RoutedEventArgs e)
    {
        if (_running || _inputs.Count == 0)
            return;

        var outDir = OutputBox.Text?.Trim();
        if (string.IsNullOrEmpty(outDir))
        {
            Log("Set an output folder first.", LogLevel.Error);
            return;
        }

        UnityVersion? versionOverride = null;
        var versionText = VersionBox.Text?.Trim();
        if (!string.IsNullOrEmpty(versionText))
        {
            try
            {
                versionOverride = UnityVersion.Parse(versionText);
            }
            catch (Exception ex)
            {
                Log($"Couldn't read the Unity version \"{versionText}\": {ex.Message}", LogLevel.Error);
                return;
            }
        }

        var options = new ShaderExportOptions
        {
            Platform = (PlatformBox.SelectedItem as PlatformChoice)?.Platform,
            VersionOverride = versionOverride,
            OutputDirectory = outDir,
            GroupBySourceFile = GroupBySourceCheck.IsChecked == true
        };

        var paths = _inputs.Select(i => i.Path).ToList();

        SetRunning(true);
        _pendingLog.Clear();
        _log.Clear();
        RunProgress.Value = 0;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var lastProgressUpdate = DateTime.MinValue;
        var progress = new Progress<ShaderExportProgress>(p =>
        {
            var now = DateTime.UtcNow;
            if (p.FilesProcessed < p.FilesTotal && (now - lastProgressUpdate).TotalMilliseconds < 80)
                return;

            lastProgressUpdate = now;
            RunProgress.Value = p.FilesTotal == 0 ? 0 : (double)p.FilesProcessed / p.FilesTotal;
            StatusText.Text = string.IsNullOrEmpty(p.CurrentFile)
                ? $"{p.FilesProcessed}/{p.FilesTotal} files - {p.ExportedShaders} shader(s) exported"
                : $"{p.FilesProcessed}/{p.FilesTotal} files - {p.ExportedShaders} shader(s) exported - {p.CurrentFile}";
        });

        ShaderExportResult result;
        try
        {
            result = await Task.Run(() =>
            {
                var exporter = new ShaderExporter(
                    options,
                    line => _pendingLog.Enqueue(LogEntry.FromExporterLine(line)),
                    progress);

                return exporter.Export(paths, token);
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            FlushLog();
            SetRunning(false);
            Log($"Export failed: {ex.Message}", LogLevel.Error);
            StatusText.Text = "Export failed.";
            return;
        }

        FlushLog();

        // after SetRunning, or UpdateQueueState puts the idle text back
        SetRunning(false);

        RunProgress.Value = 1;
        StatusText.Text = result.Cancelled
            ? $"Cancelled after {result.ExportedShaders} shader(s)."
            : $"Exported {result.ExportedShaders} shader(s) from {result.SourceFiles} file(s)"
              + (result.FailedShaders > 0 ? $", {result.FailedShaders} failed." : ".");
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        if (!_running)
            return;

        _cts?.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = "Cancelling...";
    }

    private void SetRunning(bool running)
    {
        _running = running;

        RunButton.Content = running ? "Working..." : "Decompile";
        CancelButton.IsEnabled = running;
        AddFilesButton.IsEnabled = !running;
        AddFolderButton.IsEnabled = !running;
        ClearButton.IsEnabled = !running;
        PlatformBox.IsEnabled = !running;
        VersionBox.IsEnabled = !running;
        OutputBox.IsEnabled = !running;
        BrowseOutputButton.IsEnabled = !running;
        GroupBySourceCheck.IsEnabled = !running;

        if (!running)
        {
            _cts?.Dispose();
            _cts = null;
        }

        UpdateQueueState();
    }

    #endregion

    #region log

    private void Log(string text, LogLevel level = LogLevel.Info)
    {
        _pendingLog.Enqueue(new LogEntry(text, level));
        FlushLog();
    }

    private void FlushLog()
    {
        var appended = false;
        while (_pendingLog.TryDequeue(out var entry))
        {
            _log.Add(entry);
            appended = true;
        }

        if (!appended)
            return;

        while (_log.Count > MaxLogLines)
            _log.RemoveAt(0);

        LogList.ScrollIntoView(_log.Count - 1);
    }

    private void OnCopyLog(object? sender, RoutedEventArgs e)
    {
        var clipboard = Clipboard;
        if (clipboard is null)
            return;

        var sb = new StringBuilder();
        foreach (var entry in _log)
            sb.AppendLine(entry.Text);

        _ = clipboard.SetTextAsync(sb.ToString());
    }

    #endregion
}
