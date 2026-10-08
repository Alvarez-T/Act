using System.Text;
using static YFex.Rules.Dynamic.Tests.Shop;

namespace YFex.Rules.Dynamic.Tests;

/// <summary>
/// Phase X equivalence probe (phase-x-extraction.md §8.2): writes canonical spec bytes, ExprText, the authoring catalog + spec
/// JSON Schema and the "path:code" publish checks of invalid specs to $YFEX_GOLDEN_OUT. Run the same file against the code
/// before and after the extraction and diff the outputs. A no-op when the variable is not set.
/// </summary>
public class GoldenProbe
{
    public static readonly string[] Specs =
    [
        """
        { "code": "g.ops", "group": "Promo", "priority": 7, "description": "all operators",
             "when": { "and": [ { "==": [ { "var": "Payment" }, "Pix" ] }, { "!=": [ { "var": "Member" }, false ] },
               { "<": [ { "var": "Installments" }, 13 ] }, { "<=": [ { "var": "Total" }, 99999.99 ] }, { ">": [ { "var": "Total" }, -1 ] },
               { ">=": [ { "+": [ { "var": "Total" }, 1, 2 ] }, { "-": [ { "var": "Total" }, 0.5 ] } ] },
               { "or": [ { "not": { "var": "Member" } }, { "isSet": { "var": "Store" } } ] },
               { "in": [ { "var": "Payment" }, ["Pix", "Card"] ] }, { "between": [ { "var": "Date" }, "2026-01-01", "2026-12-31" ] },
               { "==": [ { "if": [ { "var": "Member" }, 1, 2 ] }, { "coalesce": [ null, 1 ] } ] } ] },
             "then": [ { "decrease": { "target": "total", "percent": { "neg": { "neg": 3 } }, "max": 50 } } ],
             "messages": { "": { "name": "Ops" }, "pt": { "name": "Operadores" }, "en": { "name": "Operators" } } }
        """,
        """
        { "code": "g.agg", "group": "Promo",
             "when": { "and": [ { ">": [ { "sum": { "*": [ { "line": "UnitPrice" }, { "line": "Quantity" } ] } }, 100 ] },
               { ">=": [ { "count": { ">": [ { "line": "Quantity" }, 0 ] } }, 1 ] }, { "any": { "==": [ { "line": "Category" }, "drinks" ] } },
               { "all": { "isSet": { "line": "Sku" } } }, { ">": [ { "maxOf": { "line": "UnitPrice" } }, { "minOf": { "line": "UnitPrice" } } ] } ] },
             "then": [ { "increase": { "target": "total", "amount": { "min": [ { "percentOf": [ { "var": "$base" }, 5 ] }, 30 ] } } } ] }
        """,
        """
        { "code": "g.line", "group": "LineDisc",
             "when": { "and": [ { "==": [ { "lower": [ { "line": "Category" } ] }, "drinks" ] }, { ">=": [ { "var": "$lineIndex" }, 0 ] },
               { ">=": [ { "year": [ { "var": "$effectiveDate" } ] }, 2026 ] } ] },
             "then": [ { "decrease": { "target": "line", "percent": { "round": [ 2.555, 1 ] } } } ] }
        """,
        """
        { "code": "g.msg", "group": "Promo", "when": { "==": [ { "var": "Payment" }, "Cash" ] },
             "then": [ { "warn": { "path": "Payment", "code": "g.msg.cash", "args": [ { "var": "Total" }, "x" ] } },
                       { "deny": { "code": "g.msg.deny" } } ] }
        """,
        """
        { "code": "g.inst", "group": "Installments",
             "then": [ { "limit": { "field": "Installments", "min": 1, "max": { "+": [ { "count": { ">": [ { "line": "Quantity" }, 0 ] } }, 2 ] },
                         "exceed": "approval", "permission": "sales.installments.override" } },
                       { "grant": { "field": "Payment", "options": ["Pix", "Cash"] } },
                       { "approval": { "permission": "sales.manager", "path": "Total", "code": "g.inst.ok" } } ] }
        """,
    ];

    public static readonly string[] Invalid =
    [
        """{ "code": "x", "group": "Nope", "then": [ { "deny": {} } ] }""",
        """{ "code": "x", "group": "Legal", "then": [ { "deny": {} } ] }""",
        """{ "code": "x", "group": "LineDisc", "then": [ { "deny": {} } ] }""",
        """{ "code": "x", "group": "Promo", "then": [ { "decrease": { "target": "total", "percent": 50 } } ] }""",
        """{ "code": "x", "group": "Promo", "when": { "isSet": { "var": "Nope" } }, "then": [ { "deny": {} } ] }""",
        """{ "code": "x", "group": "Promo", "when": { "==": [ { "var": "Payment" }, "Boleto" ] }, "then": [ { "deny": {} } ] }""",
        """{ "code": "x", "group": "Promo", "when": { ">": [ { "+": [ { "var": "Total" }, { "var": "Installments" } ] }, 1 ] }, "then": [ { "deny": {} } ] }""",
        """{ "code": "x", "group": "Promo", "when": { "gold": [ 1 ] }, "then": [ { "deny": {} } ] }""",
        """{ "code": "x", "group": "Promo", "when": { ">": [ { "abs": [ "t" ] }, 1 ] }, "then": [ { "deny": {} } ] }""",
        """{ "code": "x", "group": "Promo", "when": { ">": [ { "sum": { "sum": { "line": "Quantity" } } }, 1 ] }, "then": [ { "deny": {} } ] }""",
        """{ "code": "x", "group": "Promo", "when": { "isSet": { "var": "Lines" } }, "then": [ { "deny": {} } ] }""",
        """{ "code": "x", "group": "Promo", "when": { "isSet": { "var": "$nope" } }, "then": [ { "deny": {} } ] }""",
        """{ "code": "x", "group": "Promo", "when": { "and": [ { "var": "Total" } ] }, "then": [ { "deny": {} } ] }""",
        """{ "code": "x", "group": "Promo", "then": [ ] }""",
        """{ "code": "x", "group": "Promo", "when": { "==": 1 }, "then": [ { "deny": {} } ] }""",
        // Deliberate Phase X change: a number literal scales an amount (Total × 2) instead of becoming Money 2 (Money × Money).
        """{ "code": "x", "group": "Promo", "when": { ">": [ { "*": [ { "var": "Total" }, 2 ] }, 1000 ] }, "then": [ { "deny": {} } ] }""",
    ];

    [Fact]
    public void Write_golden()
    {
        var path = Environment.GetEnvironmentVariable("YFEX_GOLDEN_OUT");
        if (string.IsNullOrEmpty(path)) return;
        var sb = new StringBuilder();
        foreach (var json in Specs)
        {
            var spec = RuleSpecJson.Read(Json(json));
            sb.AppendLine(Encoding.UTF8.GetString(RuleSpecJson.Write(spec)));
            sb.AppendLine(spec.When is null ? "-" : ExprText.Render(spec.When));
        }
        var (host, dynamic) = Host();
        foreach (var json in Specs)
        {
            var spec = RuleSpecJson.Read(Json(json));
            var r = host.ValidateRule(spec.Code, Tenant, Json(json));
            sb.Append("valid ").Append(spec.Code).Append(": ").AppendLine(r.IsValid ? "yes" : r.ToString());
        }
        sb.AppendLine(dynamic.CatalogJson(ShopRules.Set));
        sb.AppendLine(dynamic.SpecJsonSchema(ShopRules.Set));
        for (int i = 0; i < Invalid.Length; i++)
        {
            sb.Append("invalid[").Append(i).Append("]:");
            foreach (var v in host.ValidateRule("x", Tenant, Json(Invalid[i])).Violations) sb.Append(' ').Append(v.Path).Append(':').Append(v.Code.Value);
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString());
    }
}
