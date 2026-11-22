using System.Numerics;
using GrbAnimTool.Core.Checks;
using GrbAnimTool.Core.Clips;
using GrbAnimTool.Core.Export;
using GrbAnimTool.Core.Graph;
using GrbAnimTool.Core.Hashing;
using GrbAnimTool.Core.Import;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Package;
using GrbAnimTool.Core.Records;

namespace GrbAnimTool.Tests;

/// <summary>A test that needs the game data and Blender (GRBANIM_BLENDER, the path of <c>blender.exe</c>); it is skipped, and reported as
/// skipped, without either. Set to a path that isn't a file, the test runs and fails saying so.</summary>
public sealed class BlenderFactAttribute : FactAttribute
{
    public BlenderFactAttribute()
    {
        if (!GameData.Available) Skip = GameData.SkipText;
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GRBANIM_BLENDER"))) Skip = "Blender not configured: set GRBANIM_BLENDER to the path of blender.exe";
    }
}

/// <summary>The import on the game's own clips: the tool's export imported back, unchanged and changed, into slots short and long, armed
/// and in a motion field, and Blender's own re-exports with its default settings, made by Blender in the test (GRBANIM_BLENDER).</summary>
[Collection("install")]
public sealed class ImportGameTests(InstallFixture fx) : IDisposable
{
    readonly string _dir = Path.Combine(AppContext.BaseDirectory, "import-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { } }

    GameInstall G => fx.Install;

    /// <summary>The clip exported by the tool, with <paramref name="edit"/> applied to its samples before writing (none: the export itself).</summary>
    string Export(ClipEntry c, Action<SampledClip>? edit = null)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, ClipExport.FileNameFor(c.Name));
        if (edit is null) { Assert.True(ClipExport.Clip(G, c, path).Check.Passed); return path; }
        var sc = SampledClip.Of(G.Skeleton, c.Record!, G.SharedTable);
        edit(sc);
        File.WriteAllBytes(path, GlbWriter.Clip(G.Skeleton, sc, c.Name));
        return path;
    }

    void Turn(SampledClip sc, string bone, float deg)
    {
        int b = G.Skeleton.IndexOf(Crc32.Of(bone));
        var r = sc.Rotations[b]!;
        for (int t = 0; t < r.Length; t++) r[t] = Quaternion.Normalize(r[t] * Quaternion.CreateFromAxisAngle(Vector3.UnitX, deg * MathF.Sin(MathF.PI * t / (r.Length - 1)) * MathF.PI / 180));
    }

    /// <summary>Every track the import didn't write keeps its curve's bytes, and the record its ID, name, duration, words and track list.</summary>
    void NonBoneTracksKept(ClipEntry c, ImportResult r)
    {
        var a = c.Record!; var b = r.Rebuilt.Record;
        Assert.Equal((a.Id, a.Name, a.Duration, a.Word1, a.Word2, a.TrackListWord), (b.Id, b.Name, b.Duration, b.Word1, b.Word2, b.TrackListWord));
        Assert.Equal(a.Tracks, b.Tracks);
        for (int i = 0; i < a.Tracks.Count; i++)
        {
            bool bone = r.Fates[i].Kind is TrackKind.Rotation or TrackKind.Position && !HandTracks.IsHandTrack(a.Tracks[i]) && (a.Tracks[i] == 0 || G.Skeleton.Contains(a.Tracks[i]));
            if (!bone) Assert.False(r.Fates[i].Written, $"track {i} {a.Tracks[i]:X8} was written");
            if (!r.Fates[i].Written)
                Assert.True(a.Bytes.AsSpan(a.Curves[i].At, a.Curves[i].DiskLength).SequenceEqual(b.Bytes.AsSpan(b.Curves[i].At, b.Curves[i].DiskLength)), $"track {i} changed its bytes");
        }
    }

    [GameDataFact]
    public void The_tools_own_export_imports_back_unchanged()
    {
        foreach (var name in new[] { "mca_emote_Hello", "mcm_nwp_fm_std_V0_idle_03", "Mch_M72_Std_Get", "mcm_nwp_fm_std_V1_cycle" })
        {
            var c = G.FindClip(name)!;
            var r = ClipImport.Run(G, c, Export(c));
            Assert.True(r.Record.AsSpan().SequenceEqual(c.Bytes.Span), $"{name}: the record isn't the game's");
            Assert.Equal(0, r.Written);
            // such an import changes nothing: it is never kept for a package
            Assert.Equal(PackageEntries.GamesOwnUnchanged, PackageEntries.Unchanged(G, c, r.Record));
            // every check passes; the motion field's isn't known without the graph read
            foreach (var x in r.Report.Results) Assert.True(x.Level == CheckLevel.Pass || x.Check == ClipImport.GraphCheckName, $"{name}: {x.Line}");
            NonBoneTracksKept(c, r);
        }
    }

    /// <summary>Long clips exported and imported back untouched are the game's records byte for byte: the 2,866-tick motion-field clip
    /// whose <c>Reference</c> and helper <c>826846F3</c> jump metres in one tick (beside such a jump the file read at a tick lies a sliver
    /// of it away, the float rounding of its key's time, which isn't a change), and an 8,940-tick one whose mover does the same.</summary>
    [GameDataFact]
    public void Long_clips_exported_and_imported_back_untouched_are_the_games_records()
    {
        foreach (var name in new[] { "fm_std_V0_to_N_to_fm_std_V2_S_turn_L_R_ale", "fm_std_little_starts_stop_cool" })
        {
            var c = G.FindClip(name)!;
            Assert.True(c.LastTick >= 255, $"{name}: {c.LastTick} ticks");
            var r = ClipImport.Run(G, c, Export(c));
            Assert.True(r.Record.AsSpan().SequenceEqual(c.Bytes.Span), $"{name}: written {string.Join("; ", r.Fates.Where(f => f.Written))}");
            Assert.Equal(0, r.Written);
            Assert.Equal(CheckLevel.Pass, r.Report.Results.Single(x => x.Check == "read-back").Level);
            foreach (var x in r.Report.Results) Assert.True(x.Level == CheckLevel.Pass || x.Check == ClipImport.GraphCheckName, $"{name}: {x.Line}");
        }
    }

    [GameDataFact]
    public void A_changed_import_decodes_back_within_the_writers_tolerance_and_keeps_every_other_track()
    {
        var c = G.FindClip("mca_emote_Hello")!;
        var r = ClipImport.Run(G, c, Export(c, sc => { Turn(sc, "Spine1", 20); Turn(sc, "Head", -25); }));
        Assert.Equal(["Head", "Spine1"], r.Fates.Where(f => f.Written).Select(f => f.Name).Order().ToList());
        Assert.Equal(CheckLevel.Pass, r.Report.Results.Single(x => x.Check == "read-back").Level);
        Assert.Null(PackageEntries.Unchanged(G, c, r.Record));
        NonBoneTracksKept(c, r);
        // the written record, read back as a record file would be, holds the turn
        var back = ClipRecord.Parse(RecordFile.ToRecord(RecordFile.Content(r.Record), c.Name));
        var m = Motion.FromClip(G.Skeleton, back, G.SharedTable);
        int spine = G.Skeleton.IndexOf(Crc32.Of("Spine1"));
        Assert.InRange(GltfAxes.AngleDeg(m.Local(c.LastTick / 2, spine).Rotation, r.Game.Local(c.LastTick / 2, spine).Rotation), 19.5, 20.5);
        // written as a record file with its check report (every check that warned or failed in it), no install note, never into the
        // game's folders
        string outDir = Path.Combine(_dir, "out");
        var files = ImportOutput.Write(G, outDir, [r]);
        Assert.Equal($"{c.Index}_-_{c.Name}.Animation", files.Package.Files[0].FileName);
        Assert.Equal(G.RecordFolderName, files.Package.DatafileFolderName);
        Assert.Equal(Path.Combine(outDir, ImportOutput.ReportFileName(c)), Assert.Single(files.Reports));
        Assert.Equal(r.ReportText(), File.ReadAllText(files.Reports[0]));
        foreach (var w in r.Report.Results.Where(x => x.Level != CheckLevel.Pass)) Assert.Contains(w.Line, File.ReadAllText(files.Reports[0]));
        Assert.Equal(new[] { files.Package.Files[0].FileName, ImportOutput.ReportFileName(c) }.Order(StringComparer.Ordinal),
            Directory.GetFiles(outDir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        // a second write over them is refused without overwrite, reports included, and nothing changes
        var before = Directory.GetFiles(outDir).ToDictionary(f => f, File.ReadAllBytes);
        Assert.Throws<RecordFileException>(() => ImportOutput.Write(G, outDir, [r]));
        Assert.All(before, kv => Assert.Equal(kv.Value, File.ReadAllBytes(kv.Key)));
        ImportOutput.Write(G, outDir, [r], overwrite: true);
        // a report folder of its own: guarded, and written apart
        string reports = Path.Combine(_dir, "reports");
        var apart = ImportOutput.Write(G, Path.Combine(_dir, "out2"), [r], reportFolder: reports);
        Assert.Equal(Path.Combine(reports, ImportOutput.ReportFileName(c)), Assert.Single(apart.Reports));
        Assert.False(File.Exists(Path.Combine(_dir, "out2", ImportOutput.ReportFileName(c))));
        // a protected folder refuses the write at any depth: a fake game folder of the test's own stands in for the install's (no write is
        // ever aimed at the real one), and nothing appears in it
        string fakeGame = Path.Combine(_dir, "fake-game");
        Directory.CreateDirectory(fakeGame);
        var guarded = Assert.Throws<RecordFileException>(() => ImportOutput.Write(G, Path.Combine(fakeGame, "never"), [r], false, null, [fakeGame]));
        Assert.Contains("game's folders", guarded.Message);
        Assert.Throws<RecordFileException>(() => ImportOutput.Write(G, Path.Combine(_dir, "out5"), [r], false, Path.Combine(fakeGame, "reports"), [fakeGame]));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fakeGame));
        Assert.False(Directory.Exists(Path.Combine(_dir, "out5")));
        // a report folder the guard refuses (here one whose name ends in a dot; the protected folders' own refusal is tested on fake
        // folders in the guard's tests) is refused before anything is written
        string out3 = Path.Combine(_dir, "out3");
        var ex = Assert.Throws<RecordFileException>(() => ImportOutput.Write(G, out3, [r], reportFolder: Path.Combine(_dir, "bad.") + Path.DirectorySeparatorChar));
        Assert.Contains("Report folder", ex.Message);
        Assert.False(Directory.Exists(out3));
        // a second import into another folder with the same report folder: its report is there already, so it is refused before the
        // record files are written, and nothing is
        string out4 = Path.Combine(_dir, "out4");
        var taken = Assert.Throws<RecordFileException>(() => ImportOutput.Write(G, out4, [r], reportFolder: reports));
        Assert.False(taken.FilesWritten);
        Assert.Contains("exists already", taken.Message);
        Assert.EndsWith("Nothing was written.", taken.Message);
        Assert.False(Directory.Exists(out4));
        // a report folder that is a file, or lies below one, is refused before the record files are written, and nothing is
        string aFile = Path.Combine(_dir, "report-is-a-file");
        File.WriteAllText(aFile, "a file");
        foreach (var (folder, says) in new[] { (aFile, "is a file, not a folder"), (Path.Combine(aFile, "below"), "is a file, so the folder") })
        {
            string out6 = Path.Combine(_dir, "out6");
            var isFile = Assert.Throws<RecordFileException>(() => ImportOutput.Write(G, out6, [r], reportFolder: folder));
            Assert.False(isFile.FilesWritten);
            Assert.Contains(says, isFile.Message);
            Assert.StartsWith("Report folder", isFile.Message);
            Assert.EndsWith("Nothing was written.", isFile.Message);
            Assert.False(Directory.Exists(out6));
        }
        Assert.Equal("a file", File.ReadAllText(aFile));
        // the plain-file writer names what it writes as files, not as record files
        var plain = Assert.Throws<RecordFileException>(() => RecordFileWriter.WriteFiles(Path.Combine(aFile, "below"), [fakeGame], [("a.checks.txt", [1])]));
        Assert.DoesNotContain("record files", plain.Message);
    }

    [GameDataFact]
    public void A_long_slot_is_keyed_by_the_rule()
    {
        var c = G.FindClip("mcm_nwp_fm_std_V0_idle_03")!;
        Assert.Equal(750, c.LastTick);
        var r = ClipImport.Run(G, c, Export(c, sc => Turn(sc, "Head", 30)));
        Assert.Equal(3, r.KeyStep);
        Assert.Contains(r.Fates, f => f.Written && f.Name == "Head");
        // every curve written stays within 255 keys (the game's own long curves are kept as they are)
        foreach (var f in r.Fates.Where(f => f.Written)) Assert.True(r.Rebuilt.Record.Curves[f.Index].Keys <= 255, $"{f.Name}: a curve of {r.Rebuilt.Record.Curves[f.Index].Keys} keys");
        Assert.NotEqual(CheckLevel.Fail, r.Report.Verdict);
        NonBoneTracksKept(c, r);
    }

    [GameDataFact]
    public void A_hand_moved_in_a_slot_with_an_IK_offset_warns()
    {
        var c = G.FindClip("Mch_M72_Std_Get")!;
        Assert.Contains(0x58A97CAEu, c.Record!.Tracks);
        var r = ClipImport.Run(G, c, Export(c, sc => Turn(sc, "Spine1", 15)));
        var h = r.Report.Results.Single(x => x.Check == "hands on a clip that aims them");
        Assert.Equal(CheckLevel.Warn, h.Level);
        Assert.Contains("58A97CAE", h.Message);
        NonBoneTracksKept(c, r);
    }

    [GameDataFact]
    public void A_motion_field_slot_warns()
    {
        var graph = ClipGraph.Read(G);
        var c = G.Clips.First(x => graph.Verdict(x.Id).IsMotionField && x.Record is { WalkedToEnd: true } && x.LastTick is > 10 and < 255);
        var r = ClipImport.Run(G, c, Export(c), graph);
        Assert.Equal(CheckLevel.Warn, r.Report.Results.Single(x => x.Check == ClipImport.GraphCheckName).Level);
        Assert.True(r.Record.AsSpan().SequenceEqual(c.Bytes.Span));
        // a blend's child: warned, with the blend's own text
        var b = G.Clips.First(x => graph.Verdict(x.Id) is { UnderBlend: true, IsMotionField: false } && x.Record is { WalkedToEnd: true } && x.LastTick is > 10 and < 255);
        var rb = ClipImport.Run(G, b, Export(b), graph);
        var line = rb.Report.Results.Single(x => x.Check == ClipImport.GraphCheckName);
        Assert.Equal(CheckLevel.Warn, line.Level);
        Assert.Contains(ClipGraphVerdict.ReplaceBlendWarning, line.Message);
        // the two slots used here: neither in a motion field nor under a blend
        foreach (var name in new[] { "mcm_nwp_fm_std_V0_idle_03", "Mch_M72_Std_Get" })
            Assert.Null(graph.Verdict(G.FindClip(name)!.Id).ReplaceWarning);
    }

    [GameDataFact]
    public void A_wrong_length_and_unturned_axes_are_refused_on_the_games_skeleton()
    {
        var c = G.FindClip("mca_emote_Hello")!;
        var other = G.Clips.First(x => x.Record is { WalkedToEnd: true } && x.LastTick > 10 && x.LastTick != c.LastTick && x.Name.StartsWith("mca_emote", StringComparison.Ordinal));
        Assert.Equal(ImportRefusal.Length, Assert.Throws<ImportRefusedException>(() => ClipImport.Run(G, c, Export(other))).Reason);
        Directory.CreateDirectory(_dir);
        string axes = Path.Combine(_dir, "axes.glb");
        File.WriteAllBytes(axes, GlbWriter.Clip(G.Skeleton, SampledClip.Of(G.Skeleton, c.Record!, G.SharedTable), c.Name, ExportFault.AxesNotTurned));
        Assert.Equal(ImportRefusal.Axes, Assert.Throws<ImportRefusedException>(() => ClipImport.Run(G, c, axes)).Reason);
        string thigh = Path.Combine(_dir, "thigh.glb");
        File.WriteAllBytes(thigh, GlbWriter.Clip(G.Skeleton, SampledClip.Of(G.Skeleton, c.Record!, G.SharedTable), c.Name, ExportFault.ThighLong));
        Assert.Equal(ImportRefusal.BoneSet, Assert.Throws<ImportRefusedException>(() => ClipImport.Run(G, c, thigh)).Reason);
    }

    /// <summary>The Python Blender runs for a re-export: the default scene cleared, the frame rate 60, the tool's .glb imported with the
    /// default options, the frame range the clip's, then the default glTF export.</summary>
    const string ReExportScript = """
        import sys
        sys.dont_write_bytecode = True
        import bpy
        a = sys.argv[sys.argv.index('--') + 1:]
        bpy.ops.object.select_all(action='SELECT')
        bpy.ops.object.delete()
        s = bpy.context.scene
        s.render.fps = 60
        s.render.fps_base = 1.0
        bpy.ops.import_scene.gltf(filepath=a[0])
        arm = next(o for o in bpy.data.objects if o.type == 'ARMATURE')
        lo, hi = arm.animation_data.action.frame_range
        s.frame_start, s.frame_end = int(lo), int(hi)
        bpy.ops.export_scene.gltf(filepath=a[1])
        print('re-exported ' + a[1], flush=True)
        """;

    /// <summary>Runs Blender (<paramref name="exe"/>) headless on the re-export script: factory settings, its temporary and user folders
    /// in this test's own folder, no Python bytecode written, its console read with a cap and the run stopped past a time limit.</summary>
    void ReExport(string exe, string src, string dst)
    {
        string priv = Path.Combine(_dir, "blender-private");
        Directory.CreateDirectory(priv);
        string script = Path.Combine(_dir, "reexport.py");
        File.WriteAllText(script, ReExportScript);
        var psi = new System.Diagnostics.ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "--background", "--factory-startup", "--python-use-system-env", "--python-exit-code", "9", "--python", script, "--", src, dst }) psi.ArgumentList.Add(a);
        foreach (var v in new[] { "TEMP", "TMP", "BLENDER_USER_RESOURCES", "BLENDER_USER_CONFIG", "BLENDER_USER_SCRIPTS", "BLENDER_USER_DATAFILES", "BLENDER_USER_EXTENSIONS" }) psi.Environment[v] = priv;
        psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        const int Cap = 2_000_000;
        var log = new System.Text.StringBuilder();
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException($"{exe} didn't start");
        bool over = false;
        void Take(object _, System.Diagnostics.DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (log) { if (log.Length < Cap) log.AppendLine(e.Data); else over = true; }
        }
        p.OutputDataReceived += Take;
        p.ErrorDataReceived += Take;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        var deadline = DateTime.UtcNow.AddSeconds(300);
        while (!p.WaitForExit(200))
            if (over || DateTime.UtcNow > deadline)
            {
                p.Kill(true);
                p.WaitForExit(10_000);
                Assert.Fail($"Blender was stopped: {(over ? "its console passed the cap" : "it ran past 300 s")}");
            }
        p.WaitForExit();
        string text; lock (log) text = log.ToString();
        Assert.True(p.ExitCode == 0 && File.Exists(dst), $"Blender's re-export of {Path.GetFileName(src)} failed (exit code {p.ExitCode}):\n{text[Math.Max(0, text.Length - 4000)..]}");
    }

    /// <summary>The tool's export of two slots re-exported by Blender with its default settings (GRBANIM_BLENDER: the path of
    /// <c>blender.exe</c>; skipped, and shown as skipped, without it), imported back: the game's own record each time.</summary>
    [BlenderFact]
    public void Blenders_default_re_export_imports_back()
    {
        string exe = Environment.GetEnvironmentVariable("GRBANIM_BLENDER")!;
        Assert.True(File.Exists(exe), $"GRBANIM_BLENDER is set to {exe}, which isn't a file: set it to the path of blender.exe");
        var files = new List<string>();
        foreach (var name in new[] { "mcm_nwp_fm_std_V0_idle_03", "Mch_M72_Std_Get" })
        {
            string src = Export(G.FindClip(name)!);
            string dst = Path.Combine(_dir, "blender", name + ".glb");
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            ReExport(exe, src, dst);
            files.Add(dst);
        }
        foreach (var f in files)
        {
            var c = G.FindClip(Path.GetFileNameWithoutExtension(f)) ?? throw new InvalidOperationException($"{f} isn't named after a clip");
            var scene = GlbScene.Read(f);
            // Blender's defaults: channels of two keys and STEP channels beside full ones, and scale channels
            Assert.Contains(scene.Animations[0].Channels, ch => ch.Interpolation == GltfInterpolation.Step || ch.Times.Length != scene.Animations[0].Channels[0].Times.Length);
            var r = ClipImport.Run(G, c, f);
            Assert.Equal(CheckLevel.Pass, r.Report.Results.Single(x => x.Check == "bone match").Level);
            Assert.Equal(CheckLevel.Pass, r.Report.Results.Single(x => x.Check == "read-back").Level);
            Assert.NotEqual(CheckLevel.Fail, r.Report.Verdict);
            Assert.True(r.Record.AsSpan().SequenceEqual(c.Bytes.Span), $"{c.Name}: Blender's unchanged re-export didn't give the game's record back");
            Assert.Equal(PackageEntries.GamesOwnUnchanged, PackageEntries.Unchanged(G, c, r.Record));
        }
    }
}
