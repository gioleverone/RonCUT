using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using RonCUT;

internal static class CutPreviewTests
{
    public static void Run(CutWindow window, CutPreview preview, Action<bool, string> check)
    {
        var camera = preview.Camera as OrthographicCamera;
        check(camera != null && camera.LookDirection.X == 0 && camera.LookDirection.Y == 0 && camera.LookDirection.Z < 0,
            "CUT: vista iniziale esattamente dall'alto e senza distorsione prospettica");
        var right = Vector3D.CrossProduct(camera!.LookDirection, camera.UpDirection);
        right.Normalize();
        var up = camera.UpDirection;
        up.Normalize();
        check(Vector3D.DotProduct(right, new Vector3D(1, 0, 0)) > 0.999 && Vector3D.DotProduct(up, new Vector3D(0, 1, 0)) > 0.999,
            "CUT: X a destra e Y in alto, senza rotazione di 90 gradi rispetto a DESIGN");
        var designDown = new Vector3D(0, -1, 0);
        check(Vector3D.DotProduct(up, designDown) < -0.999,
            "CUT: il verso verticale del G-code corrisponde al disegno SVG in DESIGN");
        check(!Buttons(window).Any(button => Equals(button.Tag, "side") || Equals(button.Content, "Lato")),
            "CUT: rimossa la vista laterale dai comandi disponibili");
        var tool = (ModelVisual3D)preview.Children[preview.Children.Count - 1];
        Rect3D bounds = tool.Content.Bounds;
        check(Math.Abs(bounds.X + bounds.SizeX / 2 - 10) < 0.0001 && Math.Abs(bounds.Y + bounds.SizeY / 2 - 10) < 0.0001 && Math.Abs(bounds.Z - 5) < 0.0001,
            "CUT: il nuovo mandrino mantiene la punta sulla posizione macchina indicata");
        preview.SetView("iso");
        check(camera.LookDirection.X != 0 && camera.LookDirection.Y != 0 && camera.LookDirection.Z < 0,
            "CUT: assonometria ancora disponibile");
        preview.SetView("top");
        check(camera.LookDirection.X == 0 && camera.LookDirection.Y == 0 && camera.UpDirection.Y == 1,
            "CUT: Sopra ripristina l'orientamento corretto dopo una rotazione");
    }

    private static IEnumerable<Button> Buttons(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button button)
            {
                yield return button;
            }
            foreach (var descendant in Buttons(child))
            {
                yield return descendant;
            }
        }
    }
}
