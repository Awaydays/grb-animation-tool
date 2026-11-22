using System.Numerics;
using GrbAnimTool.Core.Clips;

namespace GrbAnimTool.Core.Writing;

/// <summary>Foot plants made from a foot's path: where the toe stands near the ground and nearly still, the foot is planted. The same rule
/// read on the game's own clips lands near their stored plants, so it is the rule a clip's plants are written again by.</summary>
public static class FootContacts
{
    /// <summary>The stances of one foot, from its toe's place in the world at a run of ticks (rising): each a run of samples where the toe
    /// stands within <paramref name="zSlack"/> m of its lowest and moves at most <paramref name="speedLimit"/> m/s across the ground. A run
    /// shorter than 4 ticks counts only at the clip's start or end.</summary>
    public static List<(int From, int To)> Stances(IReadOnlyList<(int Tick, Vector3 ToeWorld)> toe, float zSlack, float speedLimit)
    {
        var l = new List<(int, int)>();
        if (toe.Count == 0) return l;
        float zMin = toe.Min(x => x.ToeWorld.Z);
        var on = new bool[toe.Count];
        for (int i = 0; i < toe.Count; i++)
        {
            int a = Math.Max(0, i - 1), b = Math.Min(toe.Count - 1, i + 1);
            float dt = (toe[b].Tick - toe[a].Tick) / 60f;
            var d = toe[b].ToeWorld - toe[a].ToeWorld;
            float speed = dt > 0 ? new Vector2(d.X, d.Y).Length() / dt : 0;
            on[i] = toe[i].ToeWorld.Z <= zMin + zSlack && speed <= speedLimit;
        }
        for (int i = 0; i < toe.Count; i++)
        {
            if (!on[i]) continue;
            int j = i;
            while (j + 1 < toe.Count && on[j + 1]) j++;
            if (toe[j].Tick - toe[i].Tick >= 4 || i == 0 || j == toe.Count - 1) l.Add((toe[i].Tick, toe[j].Tick));
            i = j;
        }
        return l;
    }

    /// <summary>The plant keys from the stances, for <see cref="CurveWriter.PlantCurve"/>: the first at tick 0 (all zeros when the foot is in
    /// the air then, as the game's own clips hold it), then each stance's start, its length, and the toe's model-space place at its start.</summary>
    public static List<FootPlant> PlantKeys(IReadOnlyList<(int From, int To)> stances, Func<int, Vector3> toeAt)
    {
        var k = new List<FootPlant>();
        if (stances.Count == 0 || stances[0].From != 0) k.Add(new FootPlant(0, 0, Vector3.Zero));
        foreach (var (f, t) in stances) k.Add(new FootPlant(f, t - f, toeAt(f)));
        return k;
    }
}
