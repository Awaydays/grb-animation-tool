using System.Reflection;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using GrbAnimTool.Core.Install;
using GrbAnimTool.Core.Library;
using Transform = GrbAnimTool.Core.Skeletons.Transform;

namespace GrbAnimTool.App.Tests;

/// <summary>The ghost never fights the solid skeleton over which is drawn in front: it is see-through, thinner (so where the two stand in
/// the same place it lies inside the solid one), its floor arrow under the solid one's, and drawn after the solid one (WPF draws a
/// viewport's models in order, a see-through model blending over what is drawn already). Checked on the scene's models and the screens'
/// viewports, as a picture of the 3D view isn't a test a computer without 3D drawing can run.</summary>
[Collection("wpf")]
public sealed class GhostTests
{
    static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;

    static byte Alpha(Material m) => m switch
    {
        MaterialGroup g => g.Children.Max(Alpha),
        DiffuseMaterial d => ((SolidColorBrush)d.Brush).Color.A,
        EmissiveMaterial e => ((SolidColorBrush)e.Brush).Color.A,
        SpecularMaterial s => ((SolidColorBrush)s.Brush).Color.A,
        _ => 255,
    };

    [GameDataFact]
    public void TheGhostIsSeeThroughThinnerAndDrawnAfterTheSolidSkeleton()
    {
        using var g = GameInstall.Open(GameDataFactAttribute.Game!, GameDataFactAttribute.Forge!);
        Wpf.Run(() =>
        {
            var solid = new SkeletonScene(g.Skeleton);
            var ghost = new SkeletonScene(g.Skeleton, ghost: true);
            Assert.True(ghost.IsGhost);
            // thinner everywhere: every joint and stick at GhostThickness of the solid one's
            for (int i = 0; i < g.Skeleton.Bones.Count; i++)
            {
                Assert.Equal(solid.JointRadius(i) * SkeletonScene.GhostThickness, ghost.JointRadius(i), 9);
                Assert.Equal(solid.StickRadius(i) * SkeletonScene.GhostThickness, ghost.StickRadius(i), 9);
            }
            Assert.True(SkeletonScene.GhostThickness < 1);
            // see-through: every material of the ghost lets what is behind show; the solid one's are opaque
            Assert.All(ghost.Materials(), m => Assert.True(Alpha(m) < 0xFF));
            Assert.All(solid.Materials(), m => Assert.Equal(0xFF, Alpha(m)));
            // the same pose: the ghost's floor arrow under the solid one's
            var pose = new Transform[g.Skeleton.Bones.Count];
            Array.Fill(pose, Transform.Identity);
            solid.Update(pose, Transform.Identity, true);
            ghost.Update(pose, Transform.Identity, true);
            Assert.True(ghost.ArrowHeight < solid.ArrowHeight);

            // the screens' viewports: the solid result (and the import) before the ghost holder
            var edit = new EditView();
            var children = Field<Viewport3D>(edit, "Viewport").Children;
            Assert.True(children.IndexOf(Field<ModelVisual3D>(edit, "_sceneHolder")) < children.IndexOf(Field<ModelVisual3D>(edit, "_ghostHolder")));
            var import = new ImportView();
            children = Field<Viewport3D>(import, "Viewport").Children;
            Assert.True(children.IndexOf(Field<ModelVisual3D>(import, "_importHolder")) < children.IndexOf(Field<ModelVisual3D>(import, "_gameHolder")));
            // the layouts each edit starts in, and the Import screen's
            Assert.Equal(CompareLayout.Overlay, EditView.DefaultLayout(EditKind.Scale));
            foreach (var k in new[] { EditKind.Speed, EditKind.Trim, EditKind.Swap, EditKind.Mirror }) Assert.Equal(CompareLayout.SideBySide, EditView.DefaultLayout(k));
            Assert.Equal(CompareLayout.Overlay, import.Layout);
            // a choice in the box holds for the screen
            import.LayoutBox.SelectedIndex = (int)CompareLayout.SideBySide;
            Assert.Equal(CompareLayout.SideBySide, import.Layout);
            Assert.Equal(Compare.Apart, Field<ModelVisual3D>(import, "_gameHolder").Transform.Value.OffsetX);
            Assert.Equal(-Compare.Apart, Field<ModelVisual3D>(import, "_importHolder").Transform.Value.OffsetX);
        });
    }
}
