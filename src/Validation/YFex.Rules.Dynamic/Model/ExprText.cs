using System.Globalization;
using System.Text;

namespace YFex.Rules.Dynamic;

/// <summary>
/// Readable one-line text of an expression — <c>Total ≥ 500 and Payment = "Pix"</c> — for traces ("why not"), audit
/// diffs and AI prompts. Not parsed back (the JSON is the source of truth).
/// </summary>
public static class ExprText
{
    public static string Render(Expr e)
    {
        var sb = new StringBuilder();
        Write(sb, e, 0);
        return sb.ToString();
    }

    // Precedence: or 1, and 2, not 3, comparison 4, + - 5, * / 6, unary 7, atoms 8.
    private static int Precedence(ExprKind k) => k switch
    {
        ExprKind.Or => 1,
        ExprKind.And => 2,
        ExprKind.Not => 3,
        ExprKind.Equal or ExprKind.NotEqual or ExprKind.Less or ExprKind.LessOrEqual or ExprKind.Greater or ExprKind.GreaterOrEqual
            or ExprKind.In or ExprKind.Between => 4,
        ExprKind.Add or ExprKind.Subtract => 5,
        ExprKind.Multiply or ExprKind.Divide => 6,
        ExprKind.Negate => 7,
        _ => 8,
    };

    private static void Write(StringBuilder sb, Expr e, int parent)
    {
        int p = Precedence(e.Kind);
        bool paren = p < parent;
        if (paren) sb.Append('(');
        switch (e.Kind)
        {
            case ExprKind.Literal: Literal(sb, e.Literal); break;
            case ExprKind.Var: sb.Append(e.Name); break;
            case ExprKind.Line: sb.Append("line.").Append(e.Name); break;
            case ExprKind.Not: sb.Append("not "); Write(sb, e.Args[0], p + 1); break;
            case ExprKind.Negate: sb.Append('-'); Write(sb, e.Args[0], p + 1); break;
            case ExprKind.IsSet: Write(sb, e.Args[0], 8); sb.Append(" is set"); break;
            case ExprKind.In:
                Write(sb, e.Args[0], p + 1);
                sb.Append(" in {");
                for (int i = 0; i < e.List.Length; i++) { if (i > 0) sb.Append(", "); Literal(sb, e.List[i]); }
                sb.Append('}');
                break;
            case ExprKind.Between:
                Write(sb, e.Args[0], p + 1); sb.Append(" between "); Write(sb, e.Args[1], p + 1); sb.Append(" and "); Write(sb, e.Args[2], p + 1);
                break;
            case ExprKind.Call or ExprKind.If or ExprKind.Coalesce or ExprKind.Sum or ExprKind.Count or ExprKind.Any or ExprKind.All
                or ExprKind.MinOf or ExprKind.MaxOf:
                sb.Append(e.Kind == ExprKind.Call ? e.Name : RuleSpecJson.OperatorName(e.Kind)).Append('(');
                for (int i = 0; i < e.Args.Length; i++) { if (i > 0) sb.Append(", "); Write(sb, e.Args[i], 0); }
                sb.Append(')');
                break;
            default:
                string op = e.Kind switch
                {
                    ExprKind.Add => " + ", ExprKind.Subtract => " - ", ExprKind.Multiply => " × ", ExprKind.Divide => " ÷ ",
                    ExprKind.Equal => " = ", ExprKind.NotEqual => " ≠ ", ExprKind.Less => " < ", ExprKind.LessOrEqual => " ≤ ",
                    ExprKind.Greater => " > ", ExprKind.GreaterOrEqual => " ≥ ", ExprKind.And => " and ", ExprKind.Or => " or ",
                    _ => " ? ",
                };
                for (int i = 0; i < e.Args.Length; i++)
                {
                    if (i > 0) sb.Append(op);
                    Write(sb, e.Args[i], i == 0 ? p : p + 1);           // left-assoc
                }
                break;
        }
        if (paren) sb.Append(')');
    }

    private static void Literal(StringBuilder sb, in RawValue v)
    {
        if (v.Kind == ValueKind.Text && v.TryGet(out string? s)) sb.Append('"').Append(s).Append('"');
        else if (v.Kind == ValueKind.None) sb.Append("null");
        else if (v.Kind == ValueKind.Decimal && v.TryGet(out decimal d)) sb.Append(d.ToString(CultureInfo.InvariantCulture));
        else sb.Append(v.ToString());
    }
}
