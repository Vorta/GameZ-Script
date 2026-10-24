using GameZ.Script.Core.Formats;

namespace GameZ.Script.Core.Sources;

/// <summary>Shared scalar construction for exact source token edits.</summary>
public static class SourceResourceEdits
{
    /// <summary>A float scalar node, as placement edits write coordinates (an edited integer coordinate becomes a float).</summary>
    public static ZrdNode Float(float value) => new(Guid.NewGuid(), ZrdKind.Float, BitConverter.SingleToUInt32Bits(value), "", []);
}
