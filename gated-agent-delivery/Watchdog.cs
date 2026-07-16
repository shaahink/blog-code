// The watchdog reduces a session to an explicit outcome — and the outcomes get DIFFERENT
// next moves. A stall is the agent's fault (kill, burn an attempt). A rate limit is the
// world's fault (back off, burn nothing). An exit code alone can't tell you which.

using System.Text.RegularExpressions;

public sealed record Limits(double StallSeconds, double TimeoutSeconds);

public sealed record SessionOutcome(string Kind, string Detail);

public static partial class Watchdog
{
    // The backend's refusal hides in free text, not in a status code.
    [GeneratedRegex("rate.?limit|usage.?limit|quota|overloaded", RegexOptions.IgnoreCase)]
    private static partial Regex LimitRx();

    public static SessionOutcome Supervise(AgentSession session, Limits limits, Action<AgentEvent> onEvent)
    {
        var started = DateTime.UtcNow;
        bool stalled = false, timedOut = false;

        while (!session.HasExited)
        {
            while (session.TryDequeue(out var ev)) onEvent(ev);

            if ((DateTime.UtcNow - session.LastActivityUtc).TotalSeconds > limits.StallSeconds)
            {
                stalled = true;
                session.Kill();
                break;
            }
            if ((DateTime.UtcNow - started).TotalSeconds > limits.TimeoutSeconds)
            {
                timedOut = true;
                session.Kill();
                break;
            }
            Thread.Sleep(50);
        }
        session.WaitForExit();
        while (session.TryDequeue(out var ev)) onEvent(ev);

        // The judgement ladder. Order matters: a killed process also has a nonzero exit
        // code, so the reasons we killed it are checked before the exit code is read.
        if (stalled)
            return new("Stalled", $"no output for {limits.StallSeconds:0.#}s");
        if (timedOut)
            return new("TimedOut", $"exceeded {limits.TimeoutSeconds:0.#}s");
        if (session.ExitCode != 0 && LimitRx().IsMatch(session.Tail))
            return new("LimitBackoff", "backend refused (rate/usage limit)");
        if (session.ExitCode != 0 || session.ResultIsError)
            return new("AgentError", $"exit code {session.ExitCode}");

        return new("Completed", $"claims: \"{session.ResultText}\" — now verify that");
    }
}
