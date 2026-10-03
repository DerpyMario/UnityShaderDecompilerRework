using AssetsTools.NET;
using AssetsTools.NET.Extra.Decompressors.LZ4;
using USCSandbox.Common;
using USCSandbox.Processor;
using UnityVersion = AssetRipper.Primitives.UnityVersion;

namespace USCSandbox.Metadata;

public class SerializedShader
{
    private readonly AssetTypeValueField _shaderBf;
    private readonly UnityVersion _engVer;

    public string Name;
    public string FallbackName;
    public List<string> KeywordNames;
    public List<GPUPlatform> Platforms;
    // Pre-6000 files: one list with one entry per platform (each platform is a single LZ4
    // segment sharing one global entry table that lives in segment 0). Unity 6000+ stores
    // these fields as arrays of arrays: one inner list per platform, each platform carrying
    // its own entry table in its first segment.
    public List<List<uint>> Offsets;
    public List<List<(uint, uint)>> CompDecompLengths;
    public readonly bool PlatformSegments;
    public byte[] CompressedBlob;
    public List<SerializedSubShader> SubShaders;

    
    
    public AssetTypeValueField PropsField => _shaderBf["m_ParsedForm"]["m_PropInfo"]["m_Props.Array"];

    public SerializedShader(AssetTypeValueField shaderBf, UnityVersion engVer)
    {
        _shaderBf = shaderBf;
        _engVer = engVer;

        var parsedForm = shaderBf["m_ParsedForm"];
        Name = parsedForm["m_Name"].AsString;
        FallbackName = parsedForm["m_FallbackName"].AsString;
        KeywordNames = parsedForm["m_KeywordNames.Array"]
            .Select(i => i.AsString).ToList();

        Platforms = shaderBf["platforms.Array"]
            .Select(i => (GPUPlatform)i.AsInt).ToList();

        var offsetGroups = SerializedMetadataHelpers.GetArrayGroups(shaderBf["offsets.Array"]);
        var compressedGroups = SerializedMetadataHelpers.GetArrayGroups(shaderBf["compressedLengths.Array"]);
        var decompressedGroups = SerializedMetadataHelpers.GetArrayGroups(shaderBf["decompressedLengths.Array"]);

        List<uint> ReadFlat(AssetTypeValueField field)
            => SerializedMetadataHelpers.GetArrayFirstValue(field)
                .Select(o => o.AsUInt).ToList();

        if (offsetGroups != null && compressedGroups != null && decompressedGroups != null)
        {
            PlatformSegments = true;
            Offsets = offsetGroups
                .Select(g => g.Select(o => o.AsUInt).ToList()).ToList();
            CompDecompLengths = compressedGroups.Zip(decompressedGroups)
                .Select(p => p.First.Zip(p.Second)
                    .Select(c => (c.First.AsUInt, c.Second.AsUInt)).ToList())
                .ToList();
        }
        else
        {
            PlatformSegments = false;
            Offsets = [ReadFlat(shaderBf["offsets.Array"])];
            CompDecompLengths = [ReadFlat(shaderBf["compressedLengths.Array"])
                .Zip(ReadFlat(shaderBf["decompressedLengths.Array"]))
                .Select(p => (p.First, p.Second)).ToList()];
        }

        CompressedBlob = shaderBf["compressedBlob.Array"].AsByteArray;

        SubShaders = parsedForm["m_SubShaders.Array"]
            .Select(s => new SerializedSubShader(s)).ToList();
    }

    public BlobManager? MakeBlobManager(GPUPlatform platform)
    {
        var platformIndex = Platforms.IndexOf(platform);
        if (platformIndex == -1)
            return null;

        var compStream = new MemoryStream(CompressedBlob);

        List<uint> offsets;
        List<(uint, uint)> lengths;

        if (PlatformSegments)
        {
            // Unity 6000+: each platform owns its segments and its own entry table, so only
            // this platform's segments must be handed to the BlobManager.
            if (platformIndex >= Offsets.Count)
                return null;
            offsets = Offsets[platformIndex];
            lengths = CompDecompLengths[platformIndex];
        }
        else
        {
            // Legacy: one segment per platform sharing the global entry table in segment 0.
            offsets = Offsets.SelectMany(o => o).ToList();
            lengths = CompDecompLengths.SelectMany(l => l).ToList();
        }

        var blobs = new byte[lengths.Count][];
        for (var i = 0; i < lengths.Count; i++)
        {
            var offset = offsets[i];
            var (compressedLength, decompressedLength) = lengths[i];

            var decompressedBlob = new byte[decompressedLength];

            var segStream = new SegmentStream(compStream, offset, compressedLength);
            var lz4Decoder = new Lz4DecoderStream(segStream);
            lz4Decoder.Read(decompressedBlob, 0, (int)decompressedLength);
            lz4Decoder.Dispose();

            blobs[i] = decompressedBlob;
        }

        return new BlobManager(blobs, _engVer);
    }
}
