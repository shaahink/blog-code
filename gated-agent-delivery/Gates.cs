// The gate battery re-derives the truth from the world: does the artifact exist, does it
// contain what the stage demanded? The agent's opinion is not an input to any gate.
//
// In a real orchestrator the battery is `dotnet build`, the test suite, linters — real
// commands whose REAL exit codes and output are read. Same principle, bigger hammers.

public sealed record GateResult(string Name, bool Passed, string Evidence);

public static class Gates
{
    public static List<GateResult> Run(string workDir, Stage stage)
    {
        var results = new List<GateResult>();
        var path = Path.Combine(workDir, stage.Artifact);

        var exists = File.Exists(path);
        results.Add(new("artifact-exists", exists,
            exists ? stage.Artifact : $"{stage.Artifact} not found"));

        var content = exists ? File.ReadAllText(path).Trim() : "";
        var ok = exists && content.Contains(stage.MustContain, StringComparison.Ordinal);
        results.Add(new("artifact-content", ok,
            ok ? $"contains \"{stage.MustContain}\""
               : $"expected \"{stage.MustContain}\", got \"{(content.Length > 40 ? content[..40] + "…" : content)}\""));

        return results;
    }
}
