using System.Text.Json;

namespace VoipNet.IvrStudio.Services;

/// <summary>One saved state of a flow, kept so an edit can be undone or tried against another.</summary>
/// <param name="Id">Sortable identifier, which is also the file name.</param>
/// <param name="SavedAt">When it was saved.</param>
/// <param name="Name">The flow's name at the time.</param>
/// <param name="Menus">How many menus it had.</param>
public sealed record FlowVersion(string Id, DateTime SavedAt, string Name, int Menus);

/// <summary>Which version a share of callers hears instead of the current flow.</summary>
public sealed class Experiment
{
    /// <summary>The saved version callers in the test group hear.</summary>
    public string? VariantVersionId { get; set; }

    /// <summary>Share of calls that get the variant, from 0 to 100.</summary>
    public int Percent { get; set; } = 50;

    public bool Enabled { get; set; }
}

/// <summary>
/// Saves the flow as JSON next to the app, so edits survive restarts, and keeps the last twenty
/// saves as versions — which is what makes both undo and an A/B test possible.
/// </summary>
public sealed class FlowStore(IWebHostEnvironment environment)
{
    /// <summary>How many saved versions to keep. Old ones are deleted oldest first.</summary>
    private const int KeepVersions = 20;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Lock _gate = new();

    private string Root => System.IO.Path.Combine(environment.ContentRootPath, "App_Data");

    private string Path => System.IO.Path.Combine(Root, "flow.json");

    private string VersionsDir => System.IO.Path.Combine(Root, "versions");

    private string ExperimentPath => System.IO.Path.Combine(Root, "experiment.json");

    public FlowModel Load()
    {
        lock (_gate)
        {
            var flow = File.Exists(Path) && FlowModel.FromJson(File.ReadAllText(Path)) is { } saved ? saved : FlowModel.Sample();
            flow.Layout();
            return flow;
        }
    }

    /// <summary>Saves the flow and keeps the previous state as a version.</summary>
    /// <param name="model">The flow to save.</param>
    public void Save(FlowModel model)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(VersionsDir);
            File.WriteAllText(Path, model.ToJson());
            File.WriteAllText(System.IO.Path.Combine(VersionsDir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.json"), model.ToJson());
            foreach (var old in VersionFiles().Skip(KeepVersions))
            {
                File.Delete(old.FullName);
            }
        }
    }

    /// <summary>Saved versions, newest first.</summary>
    public IReadOnlyList<FlowVersion> Versions()
    {
        lock (_gate)
        {
            return
            [
                .. VersionFiles().Select(file =>
                {
                    var flow = FlowModel.FromJson(File.ReadAllText(file.FullName));
                    return new FlowVersion(
                        System.IO.Path.GetFileNameWithoutExtension(file.Name),
                        file.LastWriteTime,
                        flow?.Name ?? "(unreadable)",
                        flow?.Menus.Count ?? 0);
                }),
            ];
        }
    }

    /// <summary>Reads one saved version, or null when it has been deleted.</summary>
    /// <param name="id">The version's identifier.</param>
    public FlowModel? Version(string id)
    {
        lock (_gate)
        {
            var path = System.IO.Path.Combine(VersionsDir, $"{SafeId(id)}.json");
            if (!File.Exists(path) || FlowModel.FromJson(File.ReadAllText(path)) is not { } flow)
            {
                return null;
            }

            flow.Layout();
            return flow;
        }
    }

    /// <summary>Makes a saved version current again, keeping what it replaced as a version of its own.</summary>
    /// <param name="id">The version to restore.</param>
    public FlowModel? Restore(string id)
    {
        var flow = Version(id);
        if (flow is not null)
        {
            Save(flow);
        }

        return flow;
    }

    public Experiment LoadExperiment()
    {
        lock (_gate)
        {
            if (File.Exists(ExperimentPath) && JsonSerializer.Deserialize<Experiment>(File.ReadAllText(ExperimentPath), Json) is { } saved)
            {
                return saved;
            }

            return new Experiment();
        }
    }

    public void SaveExperiment(Experiment experiment)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(ExperimentPath, JsonSerializer.Serialize(experiment, Json));
        }
    }

    public FlowModel Reset()
    {
        lock (_gate)
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }

            var flow = FlowModel.Sample();
            flow.Layout();
            return flow;
        }
    }

    private IEnumerable<FileInfo> VersionFiles() =>
        Directory.Exists(VersionsDir)
            ? new DirectoryInfo(VersionsDir).GetFiles("*.json").OrderByDescending(f => f.Name)
            : [];

    /// <summary>Keeps a version id to the shape this store writes, so it can only name a file here.</summary>
    private static string SafeId(string id) => new([.. id.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-')]);
}
