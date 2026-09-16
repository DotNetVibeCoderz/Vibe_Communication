namespace VoipNet.IvrStudio.Services;

/// <summary>Saves the flow as JSON next to the app, so edits survive restarts.</summary>
public sealed class FlowStore(IWebHostEnvironment environment)
{
    private readonly Lock _gate = new();

    private string Path => System.IO.Path.Combine(environment.ContentRootPath, "App_Data", "flow.json");

    public FlowModel Load()
    {
        lock (_gate)
        {
            if (File.Exists(Path) && FlowModel.FromJson(File.ReadAllText(Path)) is { } saved)
            {
                return saved;
            }

            return FlowModel.Sample();
        }
    }

    public void Save(FlowModel model)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, model.ToJson());
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

            return FlowModel.Sample();
        }
    }
}
