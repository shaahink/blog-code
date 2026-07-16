// A fake agent CLI that emits the same stream-JSON shape as the real ones — so the whole
// demo runs with nothing installed. Four behaviours, four ways real sessions end:
//
//   ok     does the work (writes the artifact), reports success        -> gates agree
//   lie    does NOTHING, reports the same success                      -> gates disagree
//   stall  starts talking, then goes silent forever                    -> watchdog kills it
//   limit  the backend refuses mid-run                                 -> back off, no blame

using System.Text.Json;

public static class FakeAgent
{
    public static void Run(string behaviour, string artifact, string mustContain)
    {
        Emit("""{"type":"system","subtype":"init"}""");
        Emit(Text($"Working on it — target artifact: {Path.GetFileName(artifact)}"));

        switch (behaviour)
        {
            case "ok":
                Emit(Tool("Write", $$"""{"file":"{{Path.GetFileName(artifact)}}"}"""));
                File.WriteAllText(artifact, $"{mustContain}\nwritten by the fake agent\n");
                Emit(Text("Artifact written and checked."));
                Emit(Result(isError: false, $"Wrote {Path.GetFileName(artifact)} as asked.", cost: 0.0387m));
                break;

            case "lie":
                Emit(Tool("Write", $$"""{"file":"{{Path.GetFileName(artifact)}}"}"""));
                // ...except no write happens. The report below is indistinguishable from `ok`'s:
                // this is exactly why a report is not evidence.
                Emit(Text("Artifact written and checked."));
                Emit(Result(isError: false, $"Wrote {Path.GetFileName(artifact)} as asked.", cost: 0.0402m));
                break;

            case "stall":
                Emit(Tool("Bash", """{"cmd":"run the whole test suite"}"""));
                Thread.Sleep(Timeout.Infinite); // silence — the exit code will never come
                break;

            case "limit":
                Console.WriteLine("Error: usage limit reached, try again at 03:00");
                Environment.Exit(1);
                break;
        }
    }

    private static string Text(string text) =>
        JsonSerializer.Serialize(new { type = "assistant", message = new { content = new object[] { new { type = "text", text } } } });

    private static string Tool(string name, string inputJson) =>
        """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"NAME","input":INPUT}]}}"""
            .Replace("NAME", name).Replace("INPUT", inputJson);

    private static string Result(bool isError, string result, decimal cost) =>
        JsonSerializer.Serialize(new { type = "result", is_error = isError, result, total_cost_usd = cost, num_turns = 4 });

    private static void Emit(string line)
    {
        Console.WriteLine(line);
        Thread.Sleep(120);
    }
}
