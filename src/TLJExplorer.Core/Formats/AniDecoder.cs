using System.Text;

namespace TLJExplorer.Core.Formats;

/// <summary>
/// Decoder for the ANI skeletal animation format: a per-bone set of keyframes (quaternion rotation +
/// position) sampled over time.
/// </summary>
/// <remarks>
/// <para>File header (little-endian):</para>
/// <code>
/// Int32 Id        must equal 3
/// Int32 Version   must be 3 or 256
/// </code>
/// <para>
/// The fields that follow the header differ in both presence and order between the two versions --
/// this is a quirk of the original reverse-engineered format and is replicated exactly:
/// </para>
/// <code>
/// if Version == 3:
///     Unknown1 = 0          -- NOT read from the stream; simply defaulted
///     Int32 MaxTime
///     Int32 Unknown2
/// if Version == 256:
///     Int32 Unknown1
///     Int32 Unknown2
///     Int32 MaxTime         -- note MaxTime is read LAST here, unlike version 3
/// </code>
/// <para>Body:</para>
/// <code>
/// BoneAnims: array of {
///     Int32 BoneIndex
///     KeyDatas: array of {
///         Int32  AnimTime
///         Single QRotX, QRotY, QRotZ, QRotW
///         Single PosX, PosY, PosZ
///     }
/// }
/// </code>
/// <para>As with the CIR format, every array is length-prefixed: an <c>Int32 Count</c> followed by that
/// many entries.</para>
/// </remarks>
public static class AniDecoder
{
    private const int ExpectedId = 3;

    /// <summary>Reads a full ANI animation from <paramref name="stream"/>.</summary>
    /// <exception cref="FormatException">The stream does not contain a recognizable ANI animation.</exception>
    public static AniAnimation Decode(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        int id = reader.ReadInt32();
        if (id != ExpectedId)
        {
            throw new FormatException($"ANI file has unexpected Id {id}; expected {ExpectedId}.");
        }

        int version = reader.ReadInt32();
        if (version is not (3 or 256))
        {
            throw new FormatException($"Unsupported ANI version {version}; expected 3 or 256.");
        }

        int maxTime;
        uint magic;
        if (version == 3)
        {
            // Unknown1 defaults to 0 and is not read from the stream for this version.
            maxTime = reader.ReadInt32();
            magic = reader.ReadUInt32();
        }
        else
        {
            _ = reader.ReadInt32(); // Unknown1
            magic = reader.ReadUInt32();
            maxTime = reader.ReadInt32();
        }

        if (magic != 0xDEADBABE)
        {
            throw new FormatException(
                $"ANI file missing 0xDEADBABE sentinel (found 0x{magic:X8}); stream is misaligned or corrupt.");
        }

        var boneAnims = ReadArray(reader, ReadBoneTrack);

        return new AniAnimation
        {
            Version = version,
            MaxTime = maxTime,
            BoneAnims = boneAnims,
        };
    }

    private static AniBoneTrack ReadBoneTrack(BinaryReader reader)
    {
        int boneIndex = reader.ReadInt32();
        var keyframes = ReadArray(reader, ReadKeyframe);
        return new AniBoneTrack(boneIndex, keyframes);
    }

    private static AniKeyframe ReadKeyframe(BinaryReader reader)
    {
        int animTime = reader.ReadInt32();
        float qRotX = reader.ReadSingle();
        float qRotY = reader.ReadSingle();
        float qRotZ = reader.ReadSingle();
        float qRotW = reader.ReadSingle();
        float posX = reader.ReadSingle();
        float posY = reader.ReadSingle();
        float posZ = reader.ReadSingle();
        return new AniKeyframe(animTime, qRotX, qRotY, qRotZ, qRotW, posX, posY, posZ);
    }

    private static T[] ReadArray<T>(BinaryReader reader, Func<BinaryReader, T> readEntry)
    {
        int count = reader.ReadInt32();
        var items = new T[count];
        for (int i = 0; i < count; i++)
        {
            items[i] = readEntry(reader);
        }

        return items;
    }

    /// <summary>
    /// Produces an indented, human-readable text dump of the whole animation structure, useful as a
    /// debug/inspection view.
    /// </summary>
    public static string DumpAsText(AniAnimation animation)
    {
        ArgumentNullException.ThrowIfNull(animation);

        var sb = new StringBuilder();
        var w = new CirDecoder.TextDumpWriter(sb);

        w.BeginObject("AniAnimation");
        w.Field("Version", animation.Version);
        w.Field("MaxTime", animation.MaxTime);

        w.BeginArrayField("BoneAnims", animation.BoneAnims.Length);
        foreach (var track in animation.BoneAnims)
        {
            w.BeginObject();
            w.Field("BoneIndex", track.BoneIndex);

            w.BeginArrayField("Keyframes", track.Keyframes.Length);
            foreach (var kf in track.Keyframes)
            {
                w.BeginObject();
                w.Field("AnimTime", kf.AnimTime);
                w.Field("QRotX", kf.QRotX);
                w.Field("QRotY", kf.QRotY);
                w.Field("QRotZ", kf.QRotZ);
                w.Field("QRotW", kf.QRotW);
                w.Field("PosX", kf.PosX);
                w.Field("PosY", kf.PosY);
                w.Field("PosZ", kf.PosZ);
                w.EndObject();
            }

            w.EndArray();
            w.EndObject();
        }

        w.EndArray();
        w.EndObject();

        return sb.ToString();
    }
}
