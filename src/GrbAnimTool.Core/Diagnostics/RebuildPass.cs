using System.Buffers.Binary;
using System.Security.Cryptography;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Datafiles;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Writing;

namespace GrbAnimTool.Core.Diagnostics;

/// <summary>One full pass of the write path over every clip: each record parsed again from its bytes, every key of every curve decoded,
/// then rebuilt twice: with nothing replaced (it must give the record back byte for byte), and with every inline curve of a form with a
/// writer decoded and written again (it must read to its end, as long as the original, every re-encoded curve keeping its size, key
/// count and times). The digest is a SHA-256 over every decoded key and both rebuilt records of every clip, so two passes over the same data
/// give the same one.</summary>
public static class RebuildPass
{
    /// <param name="Clips">Clips rebuilt (those whose curves read to their end).</param>
    /// <param name="Skipped">Clips whose curves don't read to their end: no rebuild is tried.</param>
    /// <param name="SameBytes">Clips whose rebuild with nothing replaced gave the record back byte for byte.</param>
    /// <param name="CurvesReencoded">Inline curves decoded and written again.</param>
    /// <param name="ReencodedSound">Clips whose rebuild with re-encoded curves read back as described.</param>
    /// <param name="Keys">Keys decoded.</param>
    /// <param name="Faults">The first faults, in words (empty on a clean pass).</param>
    public sealed record Result(int Clips, int Skipped, int SameBytes, long CurvesReencoded, int ReencodedSound, long Keys, string Digest, IReadOnlyList<string> Faults)
    {
        public bool Clean => Faults.Count == 0 && SameBytes == Clips && ReencodedSound == Clips;
    }

    public static Result Run(GameInstall g, int maxFaults = 20) => Run(g, null, maxFaults);

    /// <summary>Runs one pass over the clip records of <paramref name="records"/> (a decode of the animation datafile made again; the
    /// install's own clips when null). Every decoded key (its time and its value) goes into the digest with both rebuilt records.</summary>
    public static Result Run(GameInstall g, IReadOnlyList<DatafileRecord>? records, int maxFaults = 20)
    {
        int clips = 0, skipped = 0, same = 0, sound = 0; long reenc = 0, keys = 0;
        var faults = new List<string>();
        void Fault(string s) { if (faults.Count < maxFaults) faults.Add(s); }
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[20];
        void Floats(params ReadOnlySpan<float> v)
        {
            for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(4 * i), v[i]);
            sha.AppendData(buf, 0, 4 * v.Length);
        }
        var source = records is null
            ? g.Clips.Select(e => (e.Index, e.Name, e.Bytes))
            : records.Where(r => r.Type == ClipRecord.Type).Select(r => (r.Index, r.Name, r.Bytes));
        foreach (var e in source)
        {
            var bytes = e.Bytes.ToArray();
            ClipRecord c;
            try { c = ClipRecord.Parse(bytes); }
            catch (InvalidDataException) { skipped++; continue; }
            if (!c.WalkedToEnd) { skipped++; continue; }
            clips++;
            foreach (var cv in c.Curves)
            {
                var u = SharedCurveTable.Resolve(cv, g.SharedTable);
                if (u is null) continue;
                foreach (int t in CurveDecoder.KeyTicks(u)) Floats(t);
                for (int k = 0; k < u.Keys; k++)
                {
                    keys++;
                    if (CurveDecoder.KeyRot(u, k) is { } q) Floats(q.X, q.Y, q.Z, q.W);
                    if (CurveDecoder.KeyPos(u, k) is { } p) Floats(p.X, p.Y, p.Z);
                    if (CurveDecoder.Plant(u, k) is { } f) Floats(f.Tick, f.LengthTicks, f.Point.X, f.Point.Y, f.Point.Z);
                }
            }
            try
            {
                var a = ClipRebuild.Rebuild(c, g.SharedTable);
                if (a.Bytes.AsSpan().SequenceEqual(bytes)) same++;
                else Fault($"{e.Index}_-_{e.Name}: rebuilt with nothing replaced, {a.Bytes.Length} B against {bytes.Length} B, not the same bytes");
                sha.AppendData(a.Bytes);
            }
            catch (RebuildRefusedException ex) { Fault($"{e.Index}_-_{e.Name}: rebuild with nothing replaced refused: {ex.Message}"); }
            try
            {
                int n = 0;
                var b = ClipRebuild.Rebuild(c, g.SharedTable, (_, _, u) =>
                {
                    if (ReferenceEquals(u.Source, g.SharedTable.Bytes)) return null;   // a reference stays a reference
                    var w = CurveReencoder.Reencode(u);
                    if (w is not null) n++;
                    return w;
                });
                reenc += n;
                bool ok = b.Bytes.Length == bytes.Length;
                if (!ok) Fault($"{e.Index}_-_{e.Name}: rebuilt with re-encoded curves, {b.Bytes.Length} B against {bytes.Length} B");
                for (int i = 0; ok && i < c.Curves.Count; i++)
                {
                    Curve o = c.Curves[i], w = b.Record.Curves[i];
                    if (o.Tag != w.Tag || o.Size != w.Size || o.Keys != w.Keys || !CurveDecoder.KeyTicks(o).AsSpan().SequenceEqual(CurveDecoder.KeyTicks(w)))
                    { ok = false; Fault($"{e.Index}_-_{e.Name}: track {i} re-encoded differs in tag, size, keys or times"); }
                }
                if (ok) sound++;
                sha.AppendData(b.Bytes);
            }
            catch (Exception ex) when (ex is RebuildRefusedException or ArgumentException) { Fault($"{e.Index}_-_{e.Name}: rebuild with re-encoded curves refused: {ex.Message}"); }
        }
        return new Result(clips, skipped, same, reenc, sound, keys, Convert.ToHexStringLower(sha.GetHashAndReset()), faults);
    }
}
