using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Datafiles;
using GrbAnimTool.Core.Install;

namespace GrbAnimTool.Core.Diagnostics;

/// <summary>One full pass of the read path over every clip: each record parsed again from its bytes, every key of every curve decoded
/// (rotations, positions, plants and key times), and the clip sampled at every tick with forward kinematics on the merged skeleton. The
/// checksum sums what was decoded, so two passes over the same data must give the same one.</summary>
public static class DecodePass
{
    public sealed record Result(int Clips, long Keys, long Poses, double Checksum);

    /// <summary>Runs one pass over <paramref name="records"/> (the install's own when null).</summary>
    public static Result Run(GameInstall g, IReadOnlyList<DatafileRecord>? records = null)
    {
        int clips = 0; long keys = 0, poses = 0; double sum = 0;
        IEnumerable<ReadOnlyMemory<byte>> bytes = records is null
            ? g.ForgeClips.Select(c => c.Bytes)
            : records.Where(r => r.Type == ClipRecord.Type).Select(r => r.Bytes);
        foreach (var b in bytes)
        {
            ClipRecord c;
            try { c = ClipRecord.Parse(b.ToArray()); }
            catch (InvalidDataException) { continue; }
            clips++;
            foreach (var cv in c.Curves)
            {
                var u = SharedCurveTable.Resolve(cv, g.SharedTable);
                if (u is null) continue;
                var ticks = CurveDecoder.KeyTicks(u);
                for (int k = 0; k < u.Keys; k++)
                {
                    keys++;
                    sum += ticks[Math.Min(k, ticks.Length - 1)] * 1e-6;
                    if (CurveDecoder.TryKeyRot(u, k, out var q, out _)) sum += q.W;
                    else if (CurveDecoder.KeyPos(u, k) is { } p) sum += p.X + p.Y + p.Z;
                    else if (CurveDecoder.Plant(u, k) is { } f) sum += f.LengthTicks + f.Point.X;
                }
            }
            if (!c.WalkedToEnd) continue;
            var s = new PoseSampler(g.Skeleton, c, g.SharedTable);
            var pose = s.NewPose();
            for (int t = 0; t <= c.LastTick; t++)   // LastTick is at most MaxDuration x 60 (36,000)
            {
                s.Sample(t / 60f, pose);
                poses++;
                var w = pose.World(pose.Model.Length - 1);
                sum += w.Position.X + w.Position.Y + w.Position.Z + pose.Model[0].Rotation.W;
            }
        }
        return new Result(clips, keys, poses, sum);
    }
}
