using System.Windows;

namespace RonCUT;

public partial class MainWindow
{
    private void AddToolChange_Click(object sender, RoutedEventArgs e) => EditToolChange(null);

    private void EditToolChange_Click(object sender, RoutedEventArgs e)
    {
        if (_selected?.IsToolChange == true && SelectedOperations().Count == 1)
        {
            EditToolChange(_selected);
        }
    }

    private void EditToolChange(Operation? marker)
    {
        try
        {
            SyncFields();
            if (!_job.Operations.Any(operation => !operation.IsToolChange))
            {
                throw new InvalidOperationException("Aggiungi almeno una lavorazione prima di inserire una pausa cambio utensile.");
            }
            var materialId = _library.FindMaterial(_job.Material.Id, _job.Material.Name)?.Id ?? Guid.Empty;
            var choices = _library.Tools
                .Select(tool => (Tool: tool, Profile: _library.FindProfile(tool.Id, materialId)))
                .Where(choice => choice.Profile != null).ToList();
            if (choices.Count == 0)
            {
                throw new InvalidOperationException("Salva prima un profilo utensile per il materiale «" + _job.Material.Name + "».");
            }
            var previousChoice = _library.FindTool(marker?.NextTool?.Tool ?? _job.Tool);
            int initial = choices.FindIndex(choice => choice.Tool == previousChoice);
            int index = Choose(Operation.ToolChangeKind, "Scegli la fresa da montare dopo la pausa. Le lavorazioni successive useranno il suo profilo per «" + _job.Material.Name + "».",
                choices.Select(choice => choice.Tool.DisplayName).ToArray(), Math.Max(0, initial), marker == null ? "Aggiungi pausa" : "Salva utensile");
            if (index < 0)
            {
                return;
            }
            var choice = choices[index];
            SaveToolChange(marker, new ToolChange
            {
                Tool = Clone(choice.Tool),
                Parameters = Clone(choice.Profile!.Parameters),
                MaterialId = materialId,
                MaterialName = _job.Material.Name
            });
        }
        catch (Exception ex)
        {
            Error(ex);
        }
    }

    private void SaveToolChange(Operation? marker, ToolChange change)
    {
        if (marker == null)
        {
            int index = SelectedOperations().Select(operation => _job.Operations.IndexOf(operation)).DefaultIfEmpty(_job.Operations.Count - 1).Max();
            marker = new Operation { Kind = Operation.ToolChangeKind };
            _job.Operations.Insert(index + 1, marker);
        }
        marker.NextTool = change;
        _dirty = true;
        SelectOnly(marker);
        RefreshOperations();
        OperationsList.ScrollIntoView(OperationsList.SelectedItem);
        WriteFields();
        Draw();
        StatusText.Text = "Pausa cambio utensile impostata. Le lavorazioni successive useranno «" + change.Tool.Name + "».";
    }
}
