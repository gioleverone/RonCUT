namespace RonCUT;

public static partial class Cam
{
    public static IReadOnlyList<CutPhase> CompilePhases(Job job)
    {
        var phases = PhaseJobs(job, requireComplete: true).ToList();
        var result = new List<CutPhase>(phases.Count);
        for (int i = 0; i < phases.Count; i++)
        {
            var phase = phases[i];
            string code = phase.Operations.Count == 0 ? "" : GeneratePhase(phase, continued: i > 0, final: i == phases.Count - 1);
            result.Add(new CutPhase(code, phase.Tool.DisplayName, phase.Tool.Diameter));
        }
        return result;
    }

    public static Tool ToolFor(Job job, Operation operation)
    {
        var tool = job.Tool;
        foreach (var entry in job.Operations)
        {
            if (entry == operation)
            {
                break;
            }
            if (entry.IsToolChange && entry.NextTool?.Tool is Tool next)
            {
                tool = next;
            }
        }
        return tool;
    }

    private static IEnumerable<Job> PhaseJobs(Job job, bool requireComplete)
    {
        if (!job.Operations.Any(operation => operation.IsToolChange))
        {
            yield return job;
            yield break;
        }

        Tool tool = job.Tool;
        CuttingParameters parameters = job.Parameters;
        var operations = new List<Operation>();
        foreach (var entry in job.Operations)
        {
            if (!entry.IsToolChange)
            {
                operations.Add(entry);
                continue;
            }
            if (operations.Count == 0 && requireComplete)
            {
                throw new InvalidOperationException("Metti almeno una lavorazione prima di ogni pausa cambio utensile.");
            }
            if (operations.Count > 0)
            {
                yield return CreatePhase(job, tool, parameters, operations);
            }
            var change = entry.NextTool;
            if (change?.Tool == null || change.Parameters == null ||
                (requireComplete && !UsesMaterial(change, job.Material)))
            {
                throw new InvalidOperationException("Seleziona un profilo per il materiale corrente in ogni pausa cambio utensile.");
            }
            tool = change.Tool;
            parameters = change.Parameters;
            operations = new List<Operation>();
        }
        if (operations.Count == 0 && requireComplete)
        {
            throw new InvalidOperationException("Aggiungi una lavorazione dopo l'ultima pausa cambio utensile.");
        }
        if (operations.Count > 0)
        {
            yield return CreatePhase(job, tool, parameters, operations);
        }
    }

    private static bool UsesMaterial(ToolChange change, Material material) =>
        change.MaterialId != Guid.Empty && material.Id != Guid.Empty
            ? change.MaterialId == material.Id
            : Library.SameName(change.MaterialName, material.Name);

    private static Job CreatePhase(Job job, Tool tool, CuttingParameters parameters, List<Operation> operations) => new Job
    {
        Contours = job.Contours,
        Tool = tool,
        Material = job.Material,
        Parameters = parameters,
        Operations = operations,
        SafeZ = job.SafeZ,
        ZeroX = job.ZeroX,
        ZeroY = job.ZeroY
    };
}
