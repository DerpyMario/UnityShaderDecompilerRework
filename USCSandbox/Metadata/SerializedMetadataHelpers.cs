using AssetsTools.NET;

namespace USCSandbox.Metadata;
public static class SerializedMetadataHelpers
{
    public static AssetTypeValueField GetArrayFirstValue(AssetTypeValueField field)
    {
        
        
        
        var tempField = field.TemplateField;
        if (tempField.Children.Count < 2)
            return field;

        var possibleArrayType = tempField[1];
        if (possibleArrayType.Type != "vector" || possibleArrayType.Name != "data")
            return field;

        return field[field.Children.Count - 1]["Array"];
    }

    /// <summary>
    /// Unity 6000+ turned several shader blob arrays (offsets, compressedLengths,
    /// decompressedLengths, m_PlayerSubPrograms, m_ParameterBlobIndices) into arrays of arrays.
    /// Returns the inner arrays when <paramref name="field"/> is such a 2D array, or null when it
    /// is a plain 1D array (pre-6000 format).
    /// </summary>
    public static List<AssetTypeValueField>? GetArrayGroups(AssetTypeValueField field)
    {
        var tempField = field.TemplateField;
        if (tempField.Children.Count < 2)
            return null;

        var possibleArrayType = tempField[1];
        if (possibleArrayType.Type != "vector" || possibleArrayType.Name != "data")
            return null;

        // every child of the Array node is one inner vector (no size child at runtime)
        return field.Children
            .Select(c => c["Array"])
            .ToList();
    }
}
