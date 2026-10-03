using AssetsTools.NET;
using AssetsTools.NET.Extra;
using USCSandbox.Common;
using USCSandbox.Processor;
using UnityVersion = AssetRipper.Primitives.UnityVersion;

namespace USCSandbox;
internal class Program
{
    static void Main(string[] args)
    {
        GPUPlatform platform = GPUPlatform.d3d11;
        UnityVersion? ver = null;
        bool allSet = false;
        string? batchDir = null;
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        string outDir = Path.Combine(exeDir, "Shaders");

        List<string> argList = [];
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--"))
            {
                switch (arg)
                {
                    case "--platform":
                        platform = Enum.Parse<GPUPlatform>(args[++i]);
                        break;
                    case "--version":
                        ver = UnityVersion.Parse(args[++i]);
                        break;
                    case "--all":
                        allSet = true;
                        break;
                    case "--dir":
                        batchDir = args[++i];
                        break;
                    case "--out":
                        outDir = args[++i];
                        break;
                    default:
                        Console.WriteLine($"Optional argmuent {arg} is invalid.");
                        return;
                }
            }
            else
            {
                argList.Add(arg);
            }
        }

        if (args.Length == 0)
        {
            Console.WriteLine("=== USCS Shader Exporter ===");
            Console.WriteLine();
            Console.WriteLine("Tip: run USCSandbox.Gui to drag and drop bundles instead.");
            Console.WriteLine();

            Console.Write("Enter the path to the folder containing asset bundles: ");
            batchDir = Console.ReadLine()?.Trim().Trim('"');

            if (string.IsNullOrEmpty(batchDir))
            {
                Console.WriteLine("No path entered.");
                WaitForExit();
                return;
            }

            Console.WriteLine();
            Console.WriteLine($"Platform options: gles3, vulkan, d3d11, Switch, metal");
            Console.Write("Enter platform (or press Enter for gles3): ");
            platform = GPUPlatform.gles3;
            var platInput = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(platInput))
            {
                if (!Enum.TryParse<GPUPlatform>(platInput, true, out platform))
                {
                    Console.WriteLine($"Invalid platform: {platInput}");
                    WaitForExit();
                    return;
                }
            }

            Console.WriteLine();
            ExportAllFromPath(batchDir, outDir, platform, ver);
            WaitForExit();
            return;
        }

        if (batchDir != null)
        {
            ExportAllFromPath(batchDir, outDir, platform, ver);
            return;
        }

        if (argList.Count == 0)
        {
            Console.WriteLine("USCS [bundle path] [assets path] [shader path id] <--platform> <--version> <--all>");
            Console.WriteLine("  [bundle path (or \"null\" for no bundle)]");
            Console.WriteLine("  [assets path (or file name in bundle)]");
            Console.WriteLine("  [shader path id (or --all to load all shaders)]");
            Console.WriteLine("  --platform <[d3d11, Switch] (or skip this arg for d3d11)>");
            Console.WriteLine("  --version <unity version override>");
            Console.WriteLine("  --dir <directory or file> : export all shaders from all asset bundles found");
            Console.WriteLine("  --out <directory> : output directory for exported shaders (default: ./Shaders)");
            Console.WriteLine();
            Console.WriteLine("Or just run the exe with no arguments for interactive mode.");
            Console.WriteLine("Or run USCSandbox.Gui to drag and drop a bundle onto a window.");
            return;
        }

        var manager = new AssetsManager();
        AssetsFileInstance afileInst;

        var bundlePath = argList[0];
        if (argList.Count == 1)
        {
            var bundleFile = manager.LoadBundleFile(bundlePath, true);
            var dirInfs = bundleFile.file.BlockAndDirInfo.DirectoryInfos;
            Console.WriteLine("Available files in bundle:");
            foreach (var dirInf in dirInfs)
            {
                if ((dirInf.Flags & 4) == 0)
                    continue;

                Console.WriteLine($"  {dirInf.Name}");
            }
            return;
        }

        var assetsFileName = argList[1];
        if (argList.Count == 2 && !allSet)
        {
            if (bundlePath != "null")
            {
                var bundleFile = manager.LoadBundleFile(bundlePath, true);
                afileInst = manager.LoadAssetsFileFromBundle(bundleFile, assetsFileName);

                manager.LoadClassPackage(ShaderExporter.ClassDataPackagePath());
                manager.LoadClassDatabaseFromPackage(bundleFile.file.Header.EngineVersion);

                Console.WriteLine("Available shaders in bundle:");
            }
            else
            {
                afileInst = manager.LoadAssetsFile(assetsFileName);

                manager.LoadClassPackage(ShaderExporter.ClassDataPackagePath());
                manager.LoadClassDatabaseFromPackage(afileInst.file.Metadata.UnityVersion);

                Console.WriteLine("Available shaders in assets file:");
            }

            foreach (var shaderInf in afileInst.file.GetAssetsOfType(AssetClassID.Shader))
            {
                var tmpShaderBf = manager.GetBaseField(afileInst, shaderInf);
                var tmpShaderName = tmpShaderBf["m_ParsedForm"]["m_Name"].AsString;
                Console.WriteLine($"  {tmpShaderName} (path id {shaderInf.PathId})");
            }
            return;
        }

        long shaderPathId = 0;
        if (argList.Count > 2)
            shaderPathId = long.Parse(argList[2]);

        if (bundlePath != "null")
        {
            var bundleFile = manager.LoadBundleFile(bundlePath, true);
            afileInst = manager.LoadAssetsFileFromBundle(bundleFile, assetsFileName);

            ver ??= ShaderExporter.ParseVersion(bundleFile.file.Header.EngineVersion);
        }
        else
        {
            afileInst = manager.LoadAssetsFile(assetsFileName);

            ver ??= ShaderExporter.ParseVersion(afileInst.file.Metadata.UnityVersion);
        }

        if (ver is null)
        {
            Console.WriteLine("File version was stripped. Please set --version flag.");
            return;
        }

        manager.LoadClassPackage(ShaderExporter.ClassDataPackagePath());
        manager.LoadClassDatabaseFromPackage(ver.ToString());

        var shadersToLoad = new List<AssetFileInfo>();
        if (shaderPathId != 0)
            shadersToLoad.Add(afileInst.file.GetAssetInfo(shaderPathId));
        else
            shadersToLoad.AddRange(afileInst.file.GetAssetsOfType(AssetClassID.Shader));

        foreach (var shaderInf in shadersToLoad)
        {
            var shaderBf = manager.GetBaseField(afileInst, shaderInf);
            if (shaderBf == null)
            {
                Console.WriteLine("Shader asset not found or couldn't be read.");
                return;
            }

            var shaderName = shaderBf["m_ParsedForm"]["m_Name"].AsString;

            var shaderTextWriter = new ShaderTextWriter(shaderBf, ver.Value);
            var output = shaderTextWriter.LoadAndWrite(platform);
            Console.WriteLine(output);

            Console.WriteLine($"{shaderName} decompiled");
        }
    }

    static void ExportAllFromPath(string path, string outDir, GPUPlatform platform, UnityVersion? versionOverride)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            Console.WriteLine($"Path not found: {path}");
            return;
        }

        Console.WriteLine($"Scanning: {path}");

        var exporter = new ShaderExporter(
            new ShaderExportOptions
            {
                Platform = platform,
                VersionOverride = versionOverride,
                OutputDirectory = outDir,
                GroupBySourceFile = true
            },
            Console.WriteLine);

        exporter.Export([path]);
    }

    static void WaitForExit()
    {
        Console.WriteLine();
        Console.WriteLine("Press any key to exit...");
        Console.ReadKey(true);
    }
}
