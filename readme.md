# UnityShaderDecompiler

A fork of [nesrak1/USCSandbox (rework branch)](https://github.com/nesrak1/USCSandbox/tree/rework)

The only reason this exists is because im reverse engineering a android game and couldnt get the shader accurate to the real game 

READ THIS: 
These shaders **WILL NOT** compile in editor, this is just for reference to make a accurate shader based off the decompiled code.
There is no shader decompiler that will give you accurate, compilable hlsl to use in editor
If compiling yourself you need to put [spirv-cross](https://github.com/KhronosGroup/SPIRV-Cross) in the build folder

## INFO

Supported shader backends:
- **Vulkan (SPIR-V)** - Primary target, reason I forked this.
- **DirectX (DXBC/DXIL)** - decently well supported
- **OpenGL ES 3 (GLES3)** - supported
- **Metal** - work in progress, not fully functional
- **Switch NVN** - work in progress, not fully functional

## How to use

There are two builds. `USCSandbox.Gui` is the drag and drop window, `USCSandbox` is the console tool. Both do exactly the same work, they just share the same exporter.

### GUI (recommended)

Run `USCSandbox.Gui`. Drag an asset bundle, an `.assets` file, or a whole folder onto the window (or click the drop zone to pick files) and press **Decompile**. Folders are scanned recursively.

- **Platform** - which GPU backend to pull out of each shader. `Auto` picks the best one actually present in the shader, falling back in the order gles3, vulkan, d3d11, Switch, metal.
- **Unity version override** - needed only when the bundle has a stripped version string, e.g. `2021.3.16f1`.
- **Output folder** - defaults to `Shaders/` next to the exe. `Open` opens it in your file browser.
- **Put each source file's shaders in its own subfolder** - turn this on when you drop several bundles that contain shaders with the same name, otherwise the later one wins.

The log pane shows every shader as it is written, and warnings for anything skipped. `Cancel` stops after the shader currently being written.

The GUI is Avalonia, so it runs on Windows, Linux and macOS.

### Interactive console mode

Run `USCSandbox` by double-clicking it or launching it from a terminal with no arguments. You will be prompted to enter the path to a folder containing asset bundles, then choose a platform. The tool will scan the entire directory recursively, decompile every shader found, and write the results to a `Shaders/` folder next to the executable.

### Command line
```
USCSandbox.exe --dir <bundle directory> [--out <output directory>] [--platform <platform>] [--version <unity version>]
```

**Arguments:**
- `[bundle path]` - path to a `.bundle` file, or `null` for a bare `.assets` file
- `[assets path]` - name of the assets file inside the bundle (e.g. `CAB-abcdef0123456789`)
- `[shader path id]` - path ID of a specific shader asset
- `--all` - decompile all shaders instead of a single one
- `--platform` - target GPU platform: `d3d11`, `gles3`, `vulkan`, `Switch` (default: `gles3` in interactive, `d3d11` in CLI)
- `--version` - override the Unity version (required if the bundle has a stripped version)
- `--dir` - path to scan. A directory is walked recursively, a single bundle or assets file also works
- `--out` - output directory for exported shaders (default: `./Shaders`)

## Building

```
dotnet build -c Release
```

That builds both projects. The binaries land in `USCSandbox/bin/Release/net8.0` and `USCSandbox.Gui/bin/Release/net8.0`.

Every push also builds Debug and Release for Windows, Linux and macOS on GitHub Actions, so you can grab a build from the Actions tab instead of compiling it yourself.

## How it works

A Unity shader asset has two major parts: serialized metadata (the `m_ParsedForm` field) and the compiled shader blob (`compressedBlob`). The metadata describes shader properties, passes, subshaders, render state, and constant buffer layouts. The blob contains the platform-specific compiled shader code.

`ShaderExporter` walks whatever you point it at, works out the Unity version, and hands each shader asset to `ShaderTextWriter`. Both the GUI and the console tool go through it, so they behave identically.

The decompiler pairs up metadata and blob data into "shader baskets", then converts the platform bytecode into USIL (Ultra Shader Intermediate Language) using a per-platform converter (`DirectXProgramToUSIL`, `NvnProgramToUSIL`, etc.). USIL is then processed in three passes:

- **Fixers** - required corrections for accurate decompilation
- **Metadders** - inject metadata from outside the native shader format (constant buffer names, input/output semantics, etc.)
- **Optimizers** - optional cleanup for readability (loop reconstruction, matrix multiply detection, etc.)

Finally, `UShaderFunctionToHLSL` converts the processed USIL to an HLSL function body, and the metadata parsers reconstruct the surrounding ShaderLab structure.

---

## Credits

- **Juelz Irons** - Vulkan + metal support, misc fixes 
- **nesrak1** - original USCSandbox and USC rework
- **ds5678** - AssetRipper stuff
- **uTinyRipper** - base Unity shader parsing
- **3dmigoto contributors** - DirectX shader disassembler foundation
- **Ryujinx contributors** - NVN/Switch shader translation via Ryujinx.Graphics.Shader

*Claude AI was used to assist in speeding up development of this fork*
