// 08 — A context pack is a knapsack problem.
//
// Distilled from DevContext's ContextPackBuilder:
// https://github.com/shaahink/DevContext2/blob/feat/tapestry-t4/src/DevContext.Core/Graph/ContextPackBuilder.cs
//
// "Give the LLM context about X, within N tokens" is a budgeting problem with rules:
//   - sections get their own budgets, so one greedy section can't starve the others,
//   - fill spine-first (BFS from the focus), so depth never crowds out breadth,
//   - cap any single body, so one god-class can't eat the pack,
//   - and NAME every cut — "+3 more members" — so the reader knows what it didn't get.
//
// The demo builds the same pack at two budgets and prints the per-section attribution.

using ContextPacks;

var graph = ToyGraph.Checkout();

foreach (var budget in (int[])[400, 1600])
{
    Console.WriteLine(new string('=', 68));
    Console.WriteLine($"pack for focus 'CheckoutHandler.Handle', budget {budget} tokens");
    Console.WriteLine(new string('=', 68));

    var pack = PackBuilder.Build(graph, budget);
    Console.WriteLine(pack.Text);
    Console.WriteLine($"-- used {pack.UsedTokens}/{budget} tokens; sections: " +
        string.Join(", ", pack.Sections.Select(s => $"{s.Name}={s.Tokens}")));
    if (pack.Omitted.Count > 0)
        Console.WriteLine($"-- omitted: {string.Join("; ", pack.Omitted)}");
    Console.WriteLine();
}

namespace ContextPacks
{
    public sealed record Node(string Id, string File, int Line, string Body, IReadOnlyList<Node> Children);

    public sealed record Section(string Name, int Tokens);

    public sealed record Pack(string Text, int UsedTokens, IReadOnlyList<Section> Sections, IReadOnlyList<string> Omitted);

    public static class PackBuilder
    {
        public static Pack Build(Node focus, int budget)
        {
            var sb = new System.Text.StringBuilder();
            var sections = new List<Section>();
            var omitted = new List<string>();

            // Section 1 — the skeleton: the shape of the trace, cheap and always present.
            var skeleton = new System.Text.StringBuilder("## trace\n");
            AppendSkeleton(skeleton, focus, 0);
            var skeletonTokens = Tokens(skeleton.ToString());
            sb.Append(skeleton);
            sections.Add(new Section("skeleton", skeletonTokens));

            // Section 2 — signatures, breadth-first, capped at a fifth of the budget so a deep
            // trace can't starve the bodies section.
            var sigBudget = budget / 5;
            var sigs = new System.Text.StringBuilder("## members\n");
            var used = 0;
            var cut = 0;
            foreach (var node in BreadthFirst(focus))
            {
                var line = $"- `{node.Id}` — {node.File}:{node.Line}\n";
                var t = Tokens(line);
                if (used + t > sigBudget) { cut++; continue; }
                sigs.Append(line);
                used += t;
            }
            if (cut > 0) sigs.Append($"- … (+{cut} more members — raise the budget for the full list)\n");
            sb.Append(sigs);
            sections.Add(new Section("members", Tokens(sigs.ToString())));

            // Section 3 — bodies are where the tokens go. Spine-first fill; each body capped
            // so one god-class can't eat the pack; every truncation visibly marked.
            var remaining = budget - skeletonTokens - Tokens(sigs.ToString());
            var perBodyCap = Math.Max(150, remaining * 2 / 5);
            var bodies = new System.Text.StringBuilder("## bodies\n");
            var omittedBodies = 0;

            foreach (var node in BreadthFirst(focus))
            {
                var block = $"### {node.Id} — {node.File}:{node.Line}\n```csharp\n{node.Body.Trim()}\n```\n";
                var t = Tokens(block);

                if (t <= Math.Min(remaining, perBodyCap))
                {
                    bodies.Append(block);
                    remaining -= t;
                    continue;
                }

                // Full body doesn't fit: try the first lines with a named cut.
                var lines = node.Body.Trim().Split('\n');
                var head = string.Join('\n', lines.Take(3));
                var snippet = $"### {node.Id} — {node.File}:{node.Line}\n```csharp\n{head}\n… (+{Math.Max(0, lines.Length - 3)} lines)\n```\n";
                var st = Tokens(snippet);
                if (st <= remaining)
                {
                    bodies.Append(snippet);
                    remaining -= st;
                }
                else
                {
                    omittedBodies++;
                    omitted.Add($"{node.Id} (body, ~{t} tokens)");
                }
            }
            if (omittedBodies > 0) bodies.Append($"… (+{omittedBodies} bodies omitted — raise the budget)\n");
            sb.Append(bodies);
            sections.Add(new Section("bodies", Tokens(bodies.ToString())));

            var text = sb.ToString();
            return new Pack(text, Tokens(text), sections, omitted);
        }

        private static void AppendSkeleton(System.Text.StringBuilder sb, Node node, int depth)
        {
            sb.Append(' ', depth * 2).Append("- ").Append(node.Id).Append('\n');
            foreach (var child in node.Children) AppendSkeleton(sb, child, depth + 1);
        }

        private static IEnumerable<Node> BreadthFirst(Node root)
        {
            var q = new Queue<Node>();
            q.Enqueue(root);
            while (q.TryDequeue(out var n))
            {
                yield return n;
                foreach (var c in n.Children) q.Enqueue(c);
            }
        }

        private static int Tokens(string s) => (s.Length + 3) / 4;
    }

    public static class ToyGraph
    {
        public static Node Checkout()
        {
            var outbox = new Node("OutboxWriter.Add", "Infrastructure/OutboxWriter.cs", 18,
                "public void Add(IEvent evt)\n{\n    _db.Outbox.Add(Envelope.From(evt));\n}", []);

            var orderRepo = new Node("OrderRepo.Save", "Orders/OrderRepo.cs", 41,
                "public async Task Save(Order order)\n{\n    _db.Orders.Add(order);\n    await _db.SaveChangesAsync();\n}", [outbox]);

            var tax = new Node("TaxCalc.Apply", "Pricing/TaxCalc.cs", 12,
                "public Money Apply(Money net, Region region)\n{\n    var rate = _rates[region];\n    return net * (1 + rate);\n}", []);

            // The deliberate god-method: long enough that the per-body cap has to clip it.
            var pricing = new Node("PricingService.Price", "Pricing/PricingService.cs", 55,
                string.Join('\n', Enumerable.Range(1, 40).Select(i => $"    // pricing rule {i}: margin, campaign, region and currency adjustments")) +
                "\npublic Money Price(Cart cart, Region region)\n{\n    var net = cart.Lines.Sum(l => l.UnitPrice * l.Qty);\n    return _tax.Apply(net, region);\n}", [tax]);

            var cartRepo = new Node("CartRepo.Load", "Cart/CartRepo.cs", 27,
                "public Task<Cart> Load(Guid id) =>\n    _db.Carts.Include(c => c.Lines).SingleAsync(c => c.Id == id);", []);

            return new Node("CheckoutHandler.Handle", "Checkout/CheckoutHandler.cs", 33,
                "public async Task<OrderId> Handle(Checkout cmd)\n{\n    var cart = await _carts.Load(cmd.CartId);\n    var total = _pricing.Price(cart, cmd.Region);\n    var order = Order.From(cart, total);\n    await _orders.Save(order);\n    return order.Id;\n}",
                [cartRepo, pricing, orderRepo]);
        }
    }
}
