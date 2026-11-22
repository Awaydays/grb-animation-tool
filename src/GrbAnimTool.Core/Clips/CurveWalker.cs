using System.Buffers.Binary;

namespace GrbAnimTool.Core.Clips;

/// <summary>Walks a run of curves by the game's curve rule. It never reads past the input's end: a curve that would run past it, or whose
/// stated size doesn't fit its form, stops the walk with a reason, and the curves walked so far are returned.</summary>
public static class CurveWalker
{
    /// <summary>The words a walk ends with when it walked every curve and stopped exactly at the input's end.</summary>
    public const string AtEnd = "the record's end";

    /// <summary>The memory size the head's arenas must hold for this form against the size the file states: a control on every curve read.
    /// The curve's header (2 bytes, 2 more for 256 keys or more, then the times) is padded to one of the alignments 1 to 16, then the values
    /// follow.</summary>
    public static bool SizeFits(byte tag, uint size, uint k, int n, int e)
    {
        foreach (int a in (ReadOnlySpan<int>)[1, 2, 4, 8, 16])
        {
            long head = (tag % 3) switch
            {
                0 => 2 + (k >= 256 ? 2 : 0) + (k > 1 ? k - 1 : 0),
                1 => 2 + (k >= 256 ? 2 : 0) + (k > 1 ? (k - 1) * 2L : 0),
                _ => 2 + n,
            };
            if ((head + a - 1) / a * a + k * (long)e == size) return true;
        }
        return false;
    }

    /// <summary>Up to <paramref name="count"/> curves from <paramref name="cur"/>. <paramref name="stop"/> is <see cref="AtEnd"/> when all
    /// were walked and the last ended exactly at <paramref name="b"/>'s end.</summary>
    public static List<Curve> Walk(byte[] b, int cur, int count, out int end, out string stop)
    {
        var list = new List<Curve>();
        stop = "count reached";
        if (count < 0 || count > b.Length || cur < 0 || cur > b.Length) { stop = $"the curve count {count} at +{cur:X} doesn't fit the record"; end = cur; return list; }
        while (list.Count < count && cur < b.Length)
        {
            byte tag = b[cur];
            if (tag == Curve.ReferenceTag)
            {
                if (cur + 5 > b.Length) { stop = $"reference at +{cur:X} runs past the end"; break; }
                list.Add(new Curve(cur, tag, 0, 0, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(cur + 1)), 5, 0, 0, b, 0, 0, 0));
                cur += 5;
                continue;
            }
            if (cur + 9 > b.Length) { stop = $"short at +{cur:X}"; break; }
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(cur + 1));
            uint k = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(cur + 5));
            int e = CurveTags.ValueBytes(tag);
            if (e < 0) { stop = $"tag 0x{tag:X2} (size 0x{size:X}, keys {k}): a form not read at +{cur:X}"; break; }
            int n = 0, shift = 0;
            long times;
            switch (tag % 3)
            {
                case 0: times = k > 1 ? k - 1L : 0; break;
                case 1: times = k > 1 ? (k - 1L) * 2 : 0; break;
                default:
                    if (cur + 10 > b.Length) { stop = $"tag 0x{tag:X2}: bitmap at +{cur:X} runs past the end"; goto done; }
                    n = b[cur + 9] & 0x3F; shift = b[cur + 9] >> 6; times = 1 + n;
                    break;
            }
            long disk = 9L + times + k * (long)e;
            if (cur + disk > b.Length) { stop = $"tag 0x{tag:X2} runs past the end at +{cur:X}"; break; }
            if (!SizeFits(tag, size, k, n, e)) { stop = $"tag 0x{tag:X2}: size 0x{size:X} doesn't fit {k} keys of {e} B at +{cur:X}"; break; }
            list.Add(new Curve(cur, tag, size, k, 0, (int)disk, cur + 9 + (int)times, e, b, n, shift, cur + 9));
            cur += (int)disk;
        }
    done:
        end = cur;
        if (stop == "count reached" && list.Count < count) stop = $"the record ended after {list.Count} of {count} curves";
        else if (list.Count == count && stop == "count reached" && cur == b.Length) stop = AtEnd;
        else if (stop == "count reached") stop = $"{count} curves walked, {b.Length - cur} B left after them";
        return list;
    }
}
