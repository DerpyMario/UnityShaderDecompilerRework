using AssetsTools.NET;
using AssetsTools.NET.Extra;
using USCSandbox.Common;
using UnityVersion = AssetRipper.Primitives.UnityVersion;

namespace USCSandbox.Processor;

public enum UnityFileType
{
    Unknown,
    Bundle,
    Assets
}

/// <summary>
/// Everything the exporter needs to know about a run. Shared by the CLI and the GUI so both
/// behave identically.
/// </summary>
public class ShaderExportOptions
{
    /// <summary>
    /// Platform to decompile. When null, the best platform present in each shader is picked
    /// from <see cref="PlatformPreference"/>.
    /// </summary>
    public GPUPlatform? Platform { get; set; }

    /// <summary>
    /// Unity version to use instead of the one stored in the file. Required for bundles with a
    /// stripped version string.
    /// </summary>
    public UnityVersion? VersionOverride { get; set; }

    public string OutputDirectory { get; set; } = "Shaders";

    /// <summary>
    /// Write each source file's shaders into their own subfolder so bundles that share shader
    /// names don't overwrite each other.
    /// </summary>
    public bool GroupBySourceFile { get; set; }

    /// <summary>
    /// Order the exporter falls back through when the requested platform isn't in a shader
    /// (and the order used outright when <see cref="Platform"/> is null).
    /// </summary>
    public static readonly GPUPlatform[] PlatformPreference =
    [
        GPUPlatform.gles3,
        GPUPlatform.vulkan,
        GPUPlatform.d3d11,
        GPUPlatform.Switch,
        GPUPlatform.metal
    ];
}

public class ShaderExportProgress
{
    public int FilesProcessed { get; init; }
    public int FilesTotal { get; init; }
    public string CurrentFile { get; init; } = "";
    public int ExportedShaders { get; init; }
}

public class ShaderExportResult
{
    public int ExportedShaders { get; set; }
    public int FailedShaders { get; set; }
    public int SourceFiles { get; set; }
    public bool Cancelled { get; set; }
}

/// <summary>
/// Walks asset bundles / assets files and writes every shader it can decompile to disk.
/// </summary>
public class ShaderExporter
{
    private readonly ShaderExportOptions _options;
    private readonly Action<string>? _log;
    private readonly IProgress<ShaderExportProgress>? _progress;

    private int _exported;
    private int _failed;
    private int _sourceFiles;
    private int _filesProcessed;
    private int _filesTotal;

    public ShaderExporter(
        ShaderExportOptions options,
        Action<string>? log = null,
        IProgress<ShaderExportProgress>? progress = null)
    {
        _options = options;
        _log = log;
        _progress = progress;
    }

    /// <summary>
    /// Exports every shader found under the given paths. Paths may be directories (scanned
    /// recursively) or individual bundle / assets files, which is what drag and drop hands us.
    /// </summary>
    public ShaderExportResult Export(IEnumerable<string> inputPaths, CancellationToken ct = default)
    {
        _exported = 0;
        _failed = 0;
        _sourceFiles = 0;
        _filesProcessed = 0;

        var files = CollectFiles(inputPaths);
        _filesTotal = files.Count;

        var result = new ShaderExportResult();
        if (files.Count == 0)
        {
            _log?.Invoke("No files to scan.");
            return result;
        }

        _log?.Invoke($"Output directory: {_options.OutputDirectory}");
        _log?.Invoke($"Scanning {files.Count} file(s)...");
        _log?.Invoke("");

        foreach (var filePath in files)
        {
            if (ct.IsCancellationRequested)
            {
                result.Cancelled = true;
                break;
            }

            ReportProgress(Path.GetFileName(filePath));

            try
            {
                ExportFromFile(filePath, ct);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"  Skipping {Path.GetFileName(filePath)}: {ex.Message}");
            }

            _filesProcessed++;
        }

        ReportProgress("");

        result.ExportedShaders = _exported;
        result.FailedShaders = _failed;
        result.SourceFiles = _sourceFiles;

        _log?.Invoke("");
        if (result.Cancelled)
            _log?.Invoke($"Cancelled. Exported {_exported} shader(s) from {_sourceFiles} file(s) to {_options.OutputDirectory}");
        else
            _log?.Invoke($"Done. Exported {_exported} shader(s) from {_sourceFiles} file(s) to {_options.OutputDirectory}");

        if (_failed > 0)
            _log?.Invoke($"  ({_failed} shader(s) failed)");

        return result;
    }

    /// <summary>
    /// Expands the given paths into the list of candidate Unity files to scan.
    /// </summary>
    public static List<string> CollectFiles(IEnumerable<string> inputPaths)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in inputPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var trimmed = path.Trim().Trim('"');

            if (Directory.Exists(trimmed))
            {
                foreach (var file in Directory.GetFiles(trimmed, "*", SearchOption.AllDirectories))
                    AddFile(file);
            }
            else if (File.Exists(trimmed))
            {
                AddFile(trimmed);
            }
        }

        return files;

        void AddFile(string file)
        {
            if (file.EndsWith(".resource", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".resS", StringComparison.OrdinalIgnoreCase))
                return;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(file);
            }
            catch
            {
                return;
            }

            if (seen.Add(fullPath))
                files.Add(fullPath);
        }
    }

    private void ExportFromFile(string filePath, CancellationToken ct)
    {
        var fileName = Path.GetFileName(filePath);
        var fileType = DetectFileType(filePath);

        if (fileType == UnityFileType.Bundle)
        {
            var manager = new AssetsManager();
            BundleFileInstance bundleFile;
            try
            {
                bundleFile = manager.LoadBundleFile(filePath, true);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"  Skipping {fileName}: failed to load bundle ({ex.Message})");
                return;
            }

            try
            {
                foreach (var dirInf in bundleFile.file.BlockAndDirInfo.DirectoryInfos)
                {
                    if (ct.IsCancellationRequested)
                        return;

                    if ((dirInf.Flags & 4) == 0)
                        continue;

                    AssetsFileInstance afileInst;
                    try
                    {
                        afileInst = manager.LoadAssetsFileFromBundle(bundleFile, dirInf.Name);
                    }
                    catch
                    {
                        continue;
                    }

                    var ver = _options.VersionOverride ?? ParseVersion(bundleFile.file.Header.EngineVersion);
                    if (ver is null)
                    {
                        _log?.Invoke($"  Skipping {fileName}/{dirInf.Name}: version stripped (set a Unity version override)");
                        continue;
                    }

                    var count = ExportShadersFromAssetsFile(manager, afileInst, ver.Value, fileName, ct);
                    if (count > 0)
                        _sourceFiles++;
                }
            }
            finally
            {
                manager.UnloadAll();
            }
        }
        else if (fileType == UnityFileType.Assets)
        {
            var manager = new AssetsManager();
            AssetsFileInstance afileInst;
            try
            {
                afileInst = manager.LoadAssetsFile(filePath);
            }
            catch
            {
                return;
            }

            try
            {
                var ver = _options.VersionOverride
                    ?? ParseVersion(afileInst.file.Metadata.UnityVersion)
                    ?? TryGetVersionFromDirectory(Path.GetDirectoryName(filePath));

                if (ver is null)
                    return;

                var count = ExportShadersFromAssetsFile(manager, afileInst, ver.Value, fileName, ct);
                if (count > 0)
                    _sourceFiles++;
            }
            finally
            {
                manager.UnloadAll();
            }
        }
    }

    private int ExportShadersFromAssetsFile(
        AssetsManager manager, AssetsFileInstance afileInst, UnityVersion ver, string sourceName, CancellationToken ct)
    {
        try
        {
            manager.LoadClassPackage(ClassDataPackagePath());
            manager.LoadClassDatabaseFromPackage(ver.ToString());
        }
        catch
        {
            return 0;
        }

        List<AssetFileInfo> shaderAssets;
        try
        {
            shaderAssets = afileInst.file.GetAssetsOfType(AssetClassID.Shader).ToList();
            if (shaderAssets.Count == 0)
                return 0;
        }
        catch
        {
            return 0;
        }

        _log?.Invoke($"[{sourceName}] Found {shaderAssets.Count} shader(s)");

        var baseOutDir = _options.GroupBySourceFile
            ? Path.Combine(_options.OutputDirectory, SanitizeFileName(Path.GetFileName(sourceName)))
            : _options.OutputDirectory;

        var exported = 0;
        foreach (var shaderInf in shaderAssets)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                var shaderBf = manager.GetBaseField(afileInst, shaderInf);
                if (shaderBf == null)
                    continue;

                var shaderName = shaderBf["m_ParsedForm"]["m_Name"].AsString;

                var shaderPlatforms = shaderBf["platforms.Array"]
                    .Select(i => (GPUPlatform)i.AsInt).ToList();

                var actualPlatform = ResolvePlatform(shaderPlatforms);

                var shaderTextWriter = new ShaderTextWriter(shaderBf, ver);
                var output = shaderTextWriter.LoadAndWrite(actualPlatform);

                var safeName = SanitizeFileName(shaderName);
                var shaderOutPath = Path.Combine(baseOutDir, safeName + ".shader");
                Directory.CreateDirectory(Path.GetDirectoryName(shaderOutPath) ?? baseOutDir);
                File.WriteAllText(shaderOutPath, output);

                exported++;
                _exported++;
                _log?.Invoke($"  Exported: {shaderName} ({actualPlatform})");
                ReportProgress(sourceName);
            }
            catch (Exception ex)
            {
                _failed++;
                _log?.Invoke($"  Failed to export shader (path id {shaderInf.PathId}): {ex}");
            }
        }

        return exported;
    }

    private GPUPlatform ResolvePlatform(List<GPUPlatform> shaderPlatforms)
    {
        var requested = _options.Platform;

        if (requested is not null && (shaderPlatforms.Contains(requested.Value) || shaderPlatforms.Count == 0))
            return requested.Value;

        if (shaderPlatforms.Count == 0)
            return ShaderExportOptions.PlatformPreference[0];

        return ShaderExportOptions.PlatformPreference
            .FirstOrDefault(p => shaderPlatforms.Contains(p), shaderPlatforms[0]);
    }

    private void ReportProgress(string currentFile)
    {
        _progress?.Report(new ShaderExportProgress
        {
            FilesProcessed = _filesProcessed,
            FilesTotal = _filesTotal,
            CurrentFile = currentFile,
            ExportedShaders = _exported
        });
    }

    /// <summary>
    /// classdata.tpk sits next to the executable, so don't rely on the working directory
    /// (the GUI is usually launched from somewhere else entirely).
    /// </summary>
    public static string ClassDataPackagePath()
    {
        var next = Path.Combine(AppContext.BaseDirectory, "classdata.tpk");
        return File.Exists(next) ? next : "classdata.tpk";
    }

    public static UnityVersion? ParseVersion(string? versionString)
    {
        if (string.IsNullOrEmpty(versionString) || versionString == "0.0.0")
            return null;

        try
        {
            var fixedVerStr = new AssetsTools.NET.Extra.UnityVersion(versionString).ToString();
            return UnityVersion.Parse(fixedVerStr);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Bare .assets files sometimes have no version of their own. Borrow one from a sibling
    /// globalgamemanagers / .assets file.
    /// </summary>
    public static UnityVersion? TryGetVersionFromDirectory(string? directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return null;

        var ggmPath = Path.Combine(directory, "globalgamemanagers");
        string[] candidates = File.Exists(ggmPath)
            ? [ggmPath, .. Directory.GetFiles(directory, "*.assets")]
            : Directory.GetFiles(directory, "*.assets");

        foreach (var candidate in candidates)
        {
            try
            {
                var tmpManager = new AssetsManager();
                var tmpInst = tmpManager.LoadAssetsFile(candidate);
                var verStr = tmpInst.file.Metadata.UnityVersion;
                tmpManager.UnloadAll();

                var ver = ParseVersion(verStr);
                if (ver is not null)
                    return ver;
            }
            catch { }
        }

        return null;
    }

    public static UnityFileType DetectFileType(string filePath)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            if (fs.Length < 16)
                return UnityFileType.Unknown;

            var buf = new byte[16];
            fs.ReadExactly(buf, 0, 16);
            var sig = System.Text.Encoding.ASCII.GetString(buf, 0, 8);

            if (sig.StartsWith("UnityFS") || sig.StartsWith("UnityWeb") || sig.StartsWith("UnityRaw"))
                return UnityFileType.Bundle;

            int version = (buf[4] << 24) | (buf[5] << 16) | (buf[6] << 8) | buf[7];
            if (version >= 9 && version <= 50)
            {
                return UnityFileType.Assets;
            }

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".assets")
                return UnityFileType.Assets;

            var name = Path.GetFileName(filePath);
            if (name.Length >= 32 && !name.Contains('.') && name.All(c => "0123456789abcdef".Contains(c)))
                return UnityFileType.Assets;

            return UnityFileType.Unknown;
        }
        catch
        {
            return UnityFileType.Unknown;
        }
    }

    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var parts = name.Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            foreach (var c in invalid)
                parts[i] = parts[i].Replace(c, '_');
        }
        return Path.Combine(parts);
    }
}
