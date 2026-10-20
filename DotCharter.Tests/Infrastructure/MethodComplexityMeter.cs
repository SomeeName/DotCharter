using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Syntax-only method metrics for <see cref="MethodComplexityBudgetTests" />: no
///     compilation, no semantic model, so the whole product tree is measured in a few
///     seconds and the numbers depend on nothing but the source text.
///     <para>
///         <b>Cyclomatic complexity</b> is McCabe's count: 1, plus one for every
///         <c>if</c>, loop, <c>catch</c>, <c>case</c> label (not <c>default</c>),
///         switch-expression arm (not the <c>_</c> discard), <c>when</c> guard,
///         <c>?:</c>, <c>&amp;&amp;</c>, <c>||</c>, <c>??</c>, <c>??=</c> and pattern
///         <c>and</c>/<c>or</c>. Lambdas and local functions count towards the member
///         that declares them: moving a branch into a local function does not make
///         the member any easier to read.
///     </para>
///     <para>
///         <b>Lines</b> are the physical lines of the member's span, attributes and
///         signature included, its leading doc comment not.
///     </para>
/// </summary>
public static class MethodComplexityMeter
{
    public sealed record Measurement(string Key, int Cyclomatic, int Lines, int Nesting);

    public static IReadOnlyList<Measurement> Measure(string relativePath, string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var root = tree.GetRoot();
        var results = new List<Measurement>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var member in root.DescendantNodes().Where(IsMeasuredMember))
        {
            if (IsUnsafe(member))
                continue;

            var name = MemberName(member);
            var key = $"{relativePath}::{name}";
            seen[key] = seen.TryGetValue(key, out var count) ? count + 1 : 1;
            if (seen[key] > 1)
                key = $"{key}#{seen[key]}";

            var lineSpan = tree.GetLineSpan(member.Span);
            var lines = lineSpan.EndLinePosition.Line - lineSpan.StartLinePosition.Line + 1;
            results.Add(new Measurement(key, Cyclomatic(member), lines, MaxNesting(member)));
        }

        return results;
    }

    public static int Cyclomatic(SyntaxNode member) => 1 + member.DescendantNodes().Count(IsDecisionPoint);

    private static bool IsMeasuredMember(SyntaxNode node) => node switch
    {
        BaseMethodDeclarationSyntax method => method.Body is not null || method.ExpressionBody is not null,
        AccessorDeclarationSyntax accessor => accessor.Body is not null || accessor.ExpressionBody is not null,
        PropertyDeclarationSyntax property => property.ExpressionBody is not null,
        IndexerDeclarationSyntax indexer => indexer.ExpressionBody is not null,
        _ => false,
    };

    private static bool IsDecisionPoint(SyntaxNode node) => node switch
    {
        IfStatementSyntax or WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax
            or CommonForEachStatementSyntax or CatchClauseSyntax or ConditionalExpressionSyntax
            or CaseSwitchLabelSyntax or CasePatternSwitchLabelSyntax or WhenClauseSyntax or CatchFilterClauseSyntax => true,
        SwitchExpressionArmSyntax arm => arm.Pattern is not DiscardPatternSyntax,
        BinaryExpressionSyntax binary => binary.Kind() is SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression
            or SyntaxKind.CoalesceExpression,
        AssignmentExpressionSyntax assignment => assignment.Kind() == SyntaxKind.CoalesceAssignmentExpression,
        BinaryPatternSyntax => true,
        _ => false,
    };

    private static int MaxNesting(SyntaxNode member)
    {
        var deepest = 0;
        foreach (var node in member.DescendantNodes().Where(IsNestingStatement))
        {
            var depth = node.Ancestors().TakeWhile(a => a != member).Count(IsNestingStatement) + 1;
            deepest = Math.Max(deepest, depth);
        }

        return deepest;
    }

    private static bool IsNestingStatement(SyntaxNode node) => node is IfStatementSyntax { Parent: not ElseClauseSyntax }
        or WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or CommonForEachStatementSyntax
        or SwitchStatementSyntax or TryStatementSyntax or UsingStatementSyntax or LockStatementSyntax;

    private static bool IsUnsafe(SyntaxNode member) =>
        member.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().Any(m => m.Modifiers.Any(SyntaxKind.UnsafeKeyword))
        || member.DescendantNodes().Any(n => n is UnsafeStatementSyntax or FixedStatementSyntax);

    private static string MemberName(SyntaxNode member)
    {
        var types = member.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.Text);
        var owner = string.Join(".", types);
        var name = member switch
        {
            MethodDeclarationSyntax method => $"{method.Identifier.Text}({method.ParameterList.Parameters.Count})",
            ConstructorDeclarationSyntax ctor => $".ctor({ctor.ParameterList.Parameters.Count})",
            DestructorDeclarationSyntax => "Finalize",
            OperatorDeclarationSyntax op => $"operator {op.OperatorToken.Text}",
            ConversionOperatorDeclarationSyntax conversion => $"operator {conversion.Type}",
            AccessorDeclarationSyntax accessor => $"{AccessorOwner(accessor)}.{accessor.Keyword.Text}",
            PropertyDeclarationSyntax property => property.Identifier.Text,
            IndexerDeclarationSyntax => "this[]",
            _ => member.Kind().ToString(),
        };

        return owner.Length == 0 ? name : $"{owner}.{name}";
    }

    private static string AccessorOwner(AccessorDeclarationSyntax accessor) => accessor.Parent?.Parent switch
    {
        PropertyDeclarationSyntax property => property.Identifier.Text,
        EventDeclarationSyntax @event => @event.Identifier.Text,
        IndexerDeclarationSyntax => "this[]",
        _ => "?",
    };
}
