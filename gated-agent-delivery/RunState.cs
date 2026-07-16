// Crash-safe orchestration with one JSON file: persist the whole state on EVERY transition,
// write atomically (tmp + rename) so a torn write is impossible, and record owed work as
// data. Restart = "look at the state, do whatever is owed" — never "reconstruct what was
// happening".

using System.Text.Json;

public sealed class RunState
{
    public List<string> CompletedStages { get; set; } = [];
    public int SessionsSpawned { get; set; }
    public int LiesCaught { get; set; }
    public int StallsCaught { get; set; }
    public bool ResumedAfterCrash { get; set; }
    public DateTime? UpdatedUtc { get; set; }

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    public static RunState LoadOrNew(string path)
    {
        if (!File.Exists(path)) return new RunState();
        try
        {
            return JsonSerializer.Deserialize<RunState>(File.ReadAllText(path), Opts) ?? new RunState();
        }
        catch (JsonException)
        {
            // A corrupt file must never brick the orchestrator: keep the evidence, start clean.
            File.Move(path, path + ".corrupt", overwrite: true);
            return new RunState();
        }
    }

    public void Save(string path)
    {
        UpdatedUtc = DateTime.UtcNow;
        // Atomic on NTFS/ext4: the file at `path` is always either the old state or the new
        // state, never a torn half-write — even if the process dies mid-write.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
        File.Move(tmp, path, overwrite: true);
    }
}
