using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using RonCUT;

internal static class LibraryTests
{
    public static void Run(Action<bool, string> check)
    {
        const string legacy = """
            {
              "Tools": [{ "Name": "Fresa A", "Diameter": 3 }, { "Name": "Fresa B", "Diameter": 1 }],
              "Materials": [{ "Name": "Supporto A" }, { "Name": "Supporto B" }],
              "Profiles": [
                { "ToolName": " Fresa A ", "MaterialName": "supporto a", "Parameters": { "FeedXY": 432 } },
                { "ToolName": "Fresa A", "MaterialName": "Supporto B", "Parameters": { "FeedXY": 321 } }
              ]
            }
            """;
        var library = Library.Read(legacy);
        var tool = library.Tools[0];
        var material = library.Materials[0];
        check(tool.Id != Guid.Empty && material.Id != Guid.Empty && Library.Read(legacy).Tools[0].Id == tool.Id
            && library.FindProfile(tool.Id, material.Id)?.Parameters.FeedXY == 432,
            "archivio precedente: migrazione stabile di utensili, supporti e parametri");

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var editor = new LibraryWindow(library);
        var grid = (DataGrid)typeof(LibraryWindow).GetField("_profiles", flags)!.GetValue(editor)!;
        check(grid.Columns[0] is DataGridComboBoxColumn tools && tools.ItemsSource == editor.Value.Tools
            && tools.SelectedValueBinding is System.Windows.Data.Binding toolBinding && toolBinding.Path.Path == nameof(Profile.ToolId)
            && grid.Columns[1] is DataGridComboBoxColumn materials && materials.ItemsSource == editor.Value.Materials,
            "editor archivio: le coppie usano selettori per ID con le voci correnti");
        editor.Value.Tools[0].Name = "Fresa rinominata";
        editor.Value.Materials[0].Name = "Supporto rinominato";
        var renamed = Library.Read(JsonSerializer.Serialize(editor.Value));
        check(renamed.Tools[0].Id == tool.Id && renamed.Materials[0].Id == material.Id
            && renamed.FindProfile(tool.Id, material.Id)?.Parameters.FeedXY == 432
            && renamed.FindProfile(tool.Id, renamed.Materials[1].Id)?.Parameters.FeedXY == 321
            && !JsonSerializer.Serialize(renamed).Contains("\"ToolName\""),
            "archivio: una rinomina conserva tutti i profili e i loro parametri dopo la riapertura");
        editor.Value.Materials.RemoveAt(1);
        check(editor.Value.Profiles.Count == 1 && library.Profiles.Count == 2,
            "editor archivio: l'eliminazione rimuove solo i profili collegati e l'originale resta intatto fino al salvataggio");
        editor.Close();

        var window = new MainWindow();
        var job = new Job
        {
            Tool = new Tool { Id = tool.Id, Name = tool.Name, Diameter = tool.Diameter },
            Material = new Material { Id = material.Id, Name = material.Name },
            Parameters = new CuttingParameters { FeedXY = 444 },
            Operations = new List<Operation>
            {
                new Operation
                {
                    Kind = Operation.ToolChangeKind,
                    NextTool = new ToolChange
                    {
                        Tool = new Tool { Id = tool.Id, Name = tool.Name, Diameter = tool.Diameter },
                        MaterialName = material.Name,
                        MaterialId = material.Id
                    }
                }
            }
        };
        var windowType = typeof(MainWindow);
        windowType.GetField("_job", flags)!.SetValue(window, job);
        windowType.GetField("_library", flags)!.SetValue(window, renamed);
        windowType.GetField("_loading", flags)!.SetValue(window, true);
        windowType.GetMethod("UpdateProjectLibraryNames", flags)!.Invoke(window, null);
        windowType.GetMethod("RefreshCombos", flags)!.Invoke(window, null);
        var picker = (ComboBox)window.FindName("ToolCombo");
        check(picker.Items.Count == 2 && picker.SelectedItem == renamed.Tools[0]
            && job.Tool.Name == renamed.Tools[0].Name && job.Parameters.FeedXY == 444
            && job.Operations[0].NextTool?.MaterialName == renamed.Materials[0].Name
            && job.Operations[0].NextTool?.Tool.Name == renamed.Tools[0].Name,
            "DESIGN: le rinomine raggiungono il progetto e le pause lasciando intatti i parametri del progetto");
        renamed.Tools[0].Diameter = 6;
        windowType.GetMethod("RefreshCombos", flags)!.Invoke(window, null);
        windowType.GetMethod("ShowProjectProfile", flags)!.Invoke(window, null);
        check(picker.Items.Count == 2 && picker.SelectedItem == null && job.Tool.Diameter == 3
            && !((Button)window.FindName("SaveProfileButton")).IsEnabled,
            "DESIGN: la fresa del vecchio progetto resta utilizzabile senza creare una terza voce o salvare sulla geometria aggiornata");
        window.Close();
    }
}
