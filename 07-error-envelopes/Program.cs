// 07 — Fail in 80 tokens: error design for agent-facing tools.
//
// Distilled from DevContext's MCP error envelopes (L5.2/L5.3):
// https://github.com/shaahink/DevContext2/blob/feat/tapestry-t4/src/DevContext.Mcp/DevContextTools.cs
//
// Two laws for tools that agents call:
//   1. Every failure returns {error, hint, example} — and stays under ~80 tokens. The agent
//      learns the NEXT MOVE for the price of a sentence, not a 3,000-token stack trace.
//   2. Never silently pick. Only an exact, unique match resolves; anything ambiguous returns
//      candidates. A wrong guess sends the agent confidently down the wrong path — that costs
//      far more than one extra round-trip.

using System.Text.Json;
using System.Text.Json.Serialization;

var resolver = new SymbolResolver(
[
    "CheckoutController.Post",
    "CheckoutHandler.Handle",
    "CheckoutSaga.Advance",
    "Cart.Add",
    "Cart.Total",
    "PricingService.Price",
]);

foreach (var query in (string[])["Cart.Add", "checkout", "payments", "cart.total"])
{
    Console.WriteLine($"resolve(\"{query}\")");
    var (id, envelope) = resolver.Resolve(query);
    if (id is not null)
    {
        Console.WriteLine($"  -> {id}\n");
        continue;
    }
    Console.WriteLine($"  -> {envelope}");
    Console.WriteLine($"     ({Tokens.Estimate(envelope!)} tokens — budget is 80)\n");
}

public static class Tokens
{
    // Good enough for a budget check: ~4 characters per token for English + JSON.
    public static int Estimate(string s) => (s.Length + 3) / 4;
}

public sealed class SymbolResolver(IReadOnlyList<string> symbols)
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Returns (id, null) on a clean resolve, or (null, envelope) for the caller
    /// to hand straight back to the agent. There is no third path — no guessing.</summary>
    public (string? Id, string? Envelope) Resolve(string query)
    {
        // Exact, unique, case-insensitive: the only silent path.
        var exact = symbols.Where(s => string.Equals(s, query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return (exact[0], null);

        var partial = symbols
            .Where(s => s.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(5)
            .ToList();

        if (partial.Count == 0)
            return (null, Envelope(
                $"No symbol matched '{query}'.",
                "Try a broader term or a class name.",
                "resolve(\"Checkout\")"));

        // Ambiguous: return the candidates and make the agent choose. Law: never silently pick.
        return (null, Envelope(
            $"'{query}' is ambiguous ({partial.Count} matches).",
            "Did you mean one of these? Pass the full id.",
            $"resolve(\"{partial[0]}\")",
            candidates: partial));
    }

    private static string Envelope(string error, string hint, string example, IReadOnlyList<string>? candidates = null)
    {
        var json = JsonSerializer.Serialize(new { error, hint, example, candidates }, Opts);

        // The budget is part of the contract — enforced, not aspirational.
        if (Tokens.Estimate(json) > 80)
            throw new InvalidOperationException($"error envelope exceeds the 80-token budget: {json}");
        return json;
    }
}
