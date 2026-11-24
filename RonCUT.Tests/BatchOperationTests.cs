using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RonCUT;

internal static class BatchOperationTests
{
    public static void Run(string directory, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(MainWindow);
        var window = new MainWindow(Path.Combine(directory, "batch-settings.json"))
        {
            Width = 1450,
            Height = 950,
            Left = -5000,
            Top = -5000,
            ShowInTaskbar = false
        };
        void Set(string name, object? value) => type.GetField(name, flags)!.SetValue(window, value);
        object? Call(string name, params object?[] args) => type.GetMethod(name, flags)!.Invoke(window, args);
        try
        {
            window.Show();
            string svg = "<svg>" + string.Join("", Enumerable.Range(0, 20).Select(i => $"<rect x='{i % 5 * 40}' y='{i / 5 * 30}' width='30' height='20'/>")) + "</svg>";
            var job = new Job { SvgText = svg, Contours = SvgImporter.Parse(svg, 1), ProfileConfirmed = true };
            job.Operations = job.Contours.Select((contour, i) => new Operation
            {
                ContourId = contour.Id,
                Kind = i == 2 ? "Incisione" : i % 2 == 0 ? "Taglio esterno" : "Taglio interno",
                Depth = 4.123456 + i % 3,
                TabHeight = i == 2 ? 3 : 1 + i % 2
            }).ToList();
            new GeneralSettings().Apply(job);
            Set("_job", job);
            Call("SelectOnly", (object?)null);
            Call("RefreshOperations");
            Call("WriteFields");
            Call("UpdateLabels");
            Call("Fit");
            Call("Draw");
            var list = (ListBox)window.FindName("OperationsList");
            var depth = (TextBox)window.FindName("DepthBox");
            var height = (TextBox)window.FindName("TabHeightBox");
            check(list.SelectionMode == SelectionMode.Extended && list.Items.Count == 20, "selezione multipla: lista con Ctrl/Shift e 20 lavorazioni");
            for (int i = 0; i < 3; i++)
            {
                ((ListBoxItem)list.Items[i]).IsSelected = true;
            }
            var originalDepths = job.Operations.Select(op => op.Depth).ToArray();
            check(list.SelectedItems.Count == 3 && depth.Text == "" && height.Text == "", "selezione multipla: valori diversi mostrati vuoti senza sceglierne uno arbitrario");
            Call("SyncFields");
            check(job.Operations.Select(op => op.Depth).SequenceEqual(originalDepths), "selezione multipla: selezionare o salvare campi misti non sovrascrive i valori");
            depth.Text = "19";
            height.Text = "4.5";
            check(job.Operations.Take(3).All(op => op.Depth == 19) && job.Operations.Skip(3).Select(op => op.Depth).SequenceEqual(originalDepths.Skip(3)), "selezione multipla: profondità 19 mm applicata soltanto alle tre selezionate");
            check(job.Operations[0].TabHeight == 4.5 && job.Operations[1].TabHeight == 4.5 && job.Operations[2].TabHeight == 3 && job.Operations[3].TabHeight == 2,
                "selezione multipla: altezza ponticelli applicata soltanto ai tagli selezionati");
            string project = Path.Combine(directory, "lavorazioni-multiple.roncut");
            Set("_filePath", project);
            bool saved = (bool)Call("Save", false)!;
            var restored = JsonSerializer.Deserialize<Job>(File.ReadAllText(project))!;
            check(saved && restored.Operations.Count == 20 && restored.Operations.Take(3).All(op => op.Depth == 19) && restored.Operations[3].Depth == originalDepths[3],
                "selezione multipla: modifiche del gruppo conservate nel progetto salvato");
            Call("SetPreviewSelection", job.Operations[3], ModifierKeys.Control);
            Call("SetPreviewSelection", job.Operations[0], ModifierKeys.Control);
            check(list.SelectedItems.Count == 3 && !((ListBoxItem)list.Items[0]).IsSelected && ((ListBoxItem)list.Items[3]).IsSelected,
                "selezione multipla: Ctrl nel disegno aggiunge e rimuove lavorazioni");
            Call("SetPreviewSelection", null, ModifierKeys.Control);
            check(list.SelectedItems.Count == 0, "selezione multipla: il clic nel vuoto deseleziona tutto");
            ((Button)window.FindName("SelectAllOperationsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            depth.Text = "19";
            height.Text = "7.25";
            Call("PreviewTimer_Tick", null, EventArgs.Empty);
            check(list.SelectedItems.Count == 20 && job.Operations.All(op => op.Depth == 19), "selezione multipla: Seleziona tutte modifica le 20 lavorazioni in una volta");
            check(job.Operations.Where(op => op.Kind != "Incisione").All(op => op.TabHeight == 7.25) && job.Operations[2].TabHeight == 3,
                "selezione multipla: modifica globale dei ponticelli senza alterare l'incisione");
            var drawing = (Canvas)window.FindName("Drawing");
            check(drawing.Children.OfType<System.Windows.Shapes.Polyline>().Count(path => path.Stroke is SolidColorBrush brush && brush.Color.R == 255 && brush.Color.G == 183) >= 20,
                "selezione multipla: evidenziati tutti i percorsi selezionati nell'anteprima");
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(directory, "RonCUT-selezione-multipla.png")))
            {
                encoder.Save(file);
            }
            depth.Text = "";
            bool rejected = false;
            try
            {
                Call("SyncFields");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
            {
                rejected = true;
            }
            check(rejected && job.Operations.All(op => op.Depth == 19), "selezione multipla: un valore incompleto non diventa profondità zero");
            depth.Text = "18.1256789";
            check(job.Operations.All(op => Math.Abs(op.Depth - 18.1256789) < 1e-10), "selezione multipla: mantenuta la precisione dei valori inseriti");
            list.UnselectAll();
            for (int i = 5; i <= 7; i++)
            {
                ((ListBoxItem)list.Items[i]).IsSelected = true;
            }
            var group = job.Operations.Skip(5).Take(3).ToList();
            Call("MoveOperation", -1);
            check(job.Operations.Skip(4).Take(3).SequenceEqual(group) && list.SelectedItems.Count == 3, "selezione multipla: riordinamento del gruppo conserva ordine relativo e selezione");
            Call("Delete_Click", window, new RoutedEventArgs());
            check(job.Operations.Count == 17 && group.All(op => !job.Operations.Contains(op)), "selezione multipla: Elimina agisce sul gruppo selezionato");
        }
        finally
        {
            Set("_dirty", false);
            window.Close();
        }
    }
}
