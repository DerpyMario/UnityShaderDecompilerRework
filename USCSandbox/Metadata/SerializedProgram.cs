using AssetsTools.NET;

namespace USCSandbox.Metadata;
public class SerializedProgram
{
    public string Name;
    public List<SerializedSubProgram> SubProgramInfos;
    public SerializedProgramParameters? CommonParams;

    public SerializedProgram(AssetTypeValueField program, string fieldName, Dictionary<int, string> nameTable)
    {
        Name = fieldName;
        if (!program["m_PlayerSubPrograms"].IsDummy)
        {
            // Unity 6000+ groups both of these arrays per hardware tier; flatten the groups so
            // subprogram infos keep pairing with their parameter blob indices. Pre-6000 files
            // have plain 1D arrays here.
            var subProgramGroups = SerializedMetadataHelpers.GetArrayGroups(program["m_PlayerSubPrograms.Array"]);
            var parameterBlobGroups = SerializedMetadataHelpers.GetArrayGroups(program["m_ParameterBlobIndices.Array"]);

            uint[]? parameterBlobIndicesArr;
            if (parameterBlobGroups != null)
            {
                parameterBlobIndicesArr = parameterBlobGroups
                    .SelectMany(g => g)
                    .Select(i => i.AsUInt)
                    .ToArray();
            }
            else
            {
                var parameterBlobIndices = program["m_ParameterBlobIndices.Array"];
                if (parameterBlobIndices.Children.Count > 0)
                {
                    parameterBlobIndicesArr = SerializedMetadataHelpers.GetArrayFirstValue(parameterBlobIndices)
                        .Select(i => i.AsUInt)
                        .ToArray();
                }
                else
                {
                    parameterBlobIndicesArr = null;
                }
            }

            IEnumerable<AssetTypeValueField> subProgramFields;
            if (subProgramGroups != null)
            {
                subProgramFields = subProgramGroups.SelectMany(g => g);
            }
            else
            {
                var subProgramInfos = program["m_PlayerSubPrograms.Array"];
                subProgramFields = subProgramInfos.Children.Count > 0
                    ? SerializedMetadataHelpers.GetArrayFirstValue(subProgramInfos).Cast<AssetTypeValueField>()
                    : [];
            }

            SubProgramInfos = subProgramFields
                .Select((i, idx) => new SerializedSubProgram(
                    i, nameTable,
                    parameterBlobIndicesArr != null && idx < parameterBlobIndicesArr.Length
                        ? parameterBlobIndicesArr[idx]
                        : uint.MaxValue))
                .ToList();
        }
        else
        {
            SubProgramInfos = program["m_SubPrograms.Array"]
                .Select(i => new SerializedSubProgram(i, nameTable))
                .ToList();
        }

        if (!program["m_CommonParameters"].IsDummy)
        {

            CommonParams = new SerializedProgramParameters(program["m_CommonParameters"], nameTable);
        }
    }
}
