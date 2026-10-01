using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace YFex.Rules.Generator;

/// <summary>
/// Splits an <c>Applies</c> body made of an <c>&amp;&amp;</c>-chain into conditions (06 §4). Every operand is rewritten
/// with fully-qualified names (types, enum members, static members), so the generated explain method compiles in
/// another file without the rule's <c>using</c>s. An operand touching anything else (the RuleContext, locals, private
/// members) makes the whole rule non-splittable (a single custom condition).
/// </summary>
internal static class ConditionSplitter
{
    public static bool TrySplit(IMethodSymbol applies, SemanticModel model, string code, out List<ConditionModel> conditions)
    {
        conditions = new List<ConditionModel>();
        var body = Body(applies);
        if (body is null) return false;

        var operands = new List<ExpressionSyntax>();
        Flatten(body, operands);
        var allowed = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        IParameterSymbol? situation = null, line = null;
        foreach (var p in applies.Parameters)
        {
            if (Names.Is(p.Type, "YFex.Rules.RuleContext")) continue;
            if (situation is null) situation = p; else line = p;
            allowed.Add(p);
        }

        for (int i = 0; i < operands.Count; i++)
        {
            var op = operands[i];
            var rewriter = new Qualifier(model, allowed);
            var rewritten = rewriter.Visit(op);
            if (rewriter.Failed || rewritten is null) { conditions.Clear(); return false; }
            string check = rewritten.NormalizeWhitespace().ToFullString();
            conditions.Add(Describe(op, (ExpressionSyntax)rewritten, model, code, i, check, situation, line));
        }
        return conditions.Count > 0;
    }

    private static ExpressionSyntax? Body(IMethodSymbol m)
    {
        foreach (var r in m.DeclaringSyntaxReferences)
        {
            if (r.GetSyntax() is not MethodDeclarationSyntax md) continue;
            if (md.ExpressionBody is { } eb) return eb.Expression;
            if (md.Body is { Statements.Count: 1 } b && b.Statements[0] is ReturnStatementSyntax { Expression: { } e }) return e;
        }
        return null;
    }

    private static void Flatten(ExpressionSyntax e, List<ExpressionSyntax> into)
    {
        while (e is ParenthesizedExpressionSyntax p) e = p.Expression;
        if (e is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.LogicalAndExpression))
        {
            Flatten(b.Left, into);
            Flatten(b.Right, into);
        }
        else into.Add(e);
    }

    private static ConditionModel Describe(ExpressionSyntax original, ExpressionSyntax rewritten, SemanticModel model, string code, int index,
        string check, IParameterSymbol? situation, IParameterSymbol? line)
    {
        string text = original.NormalizeWhitespace().ToFullString();
        // bool member: s.IsBirthdayMonth / !s.IsBirthdayMonth
        if (FieldOf(original, model, situation, line) is { } f && IsBool(original, model))
            return new(text, "rules.cond.true", f, "default", check);
        if (original is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } not
            && FieldOf(not.Operand, model, situation, line) is { } nf)
            return new(text, "rules.cond.false", nf, "default", check);

        if (original is BinaryExpressionSyntax b && rewritten is BinaryExpressionSyntax rb && OpKey(b.Kind()) is { } key)
        {
            if (FieldOf(b.Left, model, situation, line) is { } lf && !Touches(b.Right, model, situation, line))
                return new(text, key, lf, ValueExpr(rb.Right, model.GetTypeInfo(b.Right).Type), check);
            if (FieldOf(b.Right, model, situation, line) is { } rf && !Touches(b.Left, model, situation, line))
                return new(text, Mirror(key), rf, ValueExpr(rb.Left, model.GetTypeInfo(b.Left).Type), check);
        }
        if (original is IsPatternExpressionSyntax ip && FieldOf(ip.Expression, model, situation, line) is { } pf
            && ip.Pattern is ConstantPatternSyntax cp && rewritten is IsPatternExpressionSyntax { Pattern: ConstantPatternSyntax rcp })
            return new(text, "rules.cond.eq", pf, ValueExpr(rcp.Expression, model.GetTypeInfo(cp.Expression).Type), check);

        return new(text, "rules." + code + ".cond" + index, null, "default", check);
    }

    private static string? OpKey(SyntaxKind k) => k switch
    {
        SyntaxKind.EqualsExpression => "rules.cond.eq",
        SyntaxKind.NotEqualsExpression => "rules.cond.neq",
        SyntaxKind.LessThanExpression => "rules.cond.lt",
        SyntaxKind.LessThanOrEqualExpression => "rules.cond.lte",
        SyntaxKind.GreaterThanExpression => "rules.cond.gt",
        SyntaxKind.GreaterThanOrEqualExpression => "rules.cond.gte",
        _ => null,
    };

    private static string Mirror(string key) => key switch
    {
        "rules.cond.lt" => "rules.cond.gt",
        "rules.cond.lte" => "rules.cond.gte",
        "rules.cond.gt" => "rules.cond.lt",
        "rules.cond.gte" => "rules.cond.lte",
        _ => key,
    };

    private static string ValueExpr(ExpressionSyntax rewritten, ITypeSymbol? type)
    {
        if (type is null) return "default";
        string e = rewritten.NormalizeWhitespace().ToFullString();
        if (type.TypeKind == TypeKind.Enum) return $"global::YFex.RawValue.FromEnum({e})";
        return Shapes.ScalarKind(type) is not null ? $"global::YFex.RawValueConvert.From({e})" : "default";
    }

    /// <summary><c>s.Member</c> / <c>line.Member</c> (one level) → the member name.</summary>
    private static string? FieldOf(ExpressionSyntax e, SemanticModel model, IParameterSymbol? s, IParameterSymbol? line)
    {
        while (e is ParenthesizedExpressionSyntax p) e = p.Expression;
        if (e is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax id } ma
            && model.GetSymbolInfo(id).Symbol is IParameterSymbol p2
            && (SymbolEqualityComparer.Default.Equals(p2, s) || SymbolEqualityComparer.Default.Equals(p2, line)))
            return ma.Name.Identifier.ValueText;
        return null;
    }

    private static bool IsBool(ExpressionSyntax e, SemanticModel model) => model.GetTypeInfo(e).Type?.SpecialType == SpecialType.System_Boolean;

    private static bool Touches(ExpressionSyntax e, SemanticModel model, IParameterSymbol? s, IParameterSymbol? line)
    {
        foreach (var id in e.DescendantNodesAndSelf())
            if (id is IdentifierNameSyntax n && model.GetSymbolInfo(n).Symbol is IParameterSymbol p
                && (SymbolEqualityComparer.Default.Equals(p, s) || SymbolEqualityComparer.Default.Equals(p, line)))
                return true;
        return false;
    }

    /// <summary>Rewrites identifiers bound to types/static members/enum fields to <c>global::</c> names.</summary>
    private sealed class Qualifier(SemanticModel model, HashSet<ISymbol> allowedParameters) : CSharpSyntaxRewriter
    {
        public bool Failed { get; private set; }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (node.IsVar) return node;
            var symbol = model.GetSymbolInfo(node).Symbol ?? (model.GetSymbolInfo(node).CandidateSymbols.Length == 1 ? model.GetSymbolInfo(node).CandidateSymbols[0] : null);
            // Right side of a member access (s.Payment / X.Member) keeps its simple name.
            if (node.Parent is MemberAccessExpressionSyntax ma && ma.Name == node) return node;
            switch (symbol)
            {
                case IParameterSymbol p when allowedParameters.Contains(p):
                    return node;
                case ITypeSymbol t:
                    return Qualified(t.ToDisplayString(Names.FqnNoNullable), node);
                case IFieldSymbol { IsStatic: true } f when Accessible(f):
                    return Qualified(f.ContainingType.ToDisplayString(Names.FqnNoNullable) + "." + f.Name, node);
                case IPropertySymbol { IsStatic: true } pr when Accessible(pr):
                    return Qualified(pr.ContainingType.ToDisplayString(Names.FqnNoNullable) + "." + pr.Name, node);
                case IMethodSymbol { IsStatic: true } m when Accessible(m):
                    return Qualified(m.ContainingType.ToDisplayString(Names.FqnNoNullable) + "." + m.Name, node);
                case INamespaceSymbol:
                    return node;
                default:
                    Failed = true;
                    return node;
            }
        }

        public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
        {
            if (node.Parent is MemberAccessExpressionSyntax ma && ma.Name == node) return base.VisitGenericName(node);
            if (model.GetSymbolInfo(node).Symbol is ITypeSymbol t) return Qualified(t.ToDisplayString(Names.FqnNoNullable), node);
            return base.VisitGenericName(node);
        }

        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            // Ns.Type.Member → qualify the whole left side when it is a type or namespace-qualified type.
            if (model.GetSymbolInfo(node.Expression).Symbol is ITypeSymbol t)
                return node.WithExpression(Qualified(t.ToDisplayString(Names.FqnNoNullable), node.Expression)).WithName(node.Name);
            return base.VisitMemberAccessExpression(node);
        }

        public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) { Failed = true; return node; }
        public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) { Failed = true; return node; }

        private static bool Accessible(ISymbol s)
            => s.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal
               && (s.ContainingType is null || Names.IsAccessible(s.ContainingType));

        private static ExpressionSyntax Qualified(string text, SyntaxNode original)
            => SyntaxFactory.ParseExpression(text).WithTriviaFrom(original);
    }
}
