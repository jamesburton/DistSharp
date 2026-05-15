using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DistSharp.Roslyn.Internal;

/// <summary>Computes cyclomatic complexity for a method or member node.</summary>
internal static class CyclomaticComplexityCalculator
{
    /// <summary>Returns the cyclomatic complexity of <paramref name="node"/>. Base score is 1; each branching construct adds 1.</summary>
    /// <param name="node">The method, property, or constructor node to analyze.</param>
    /// <returns>Cyclomatic complexity, minimum 1.</returns>
    public static int Compute(SyntaxNode node)
    {
        var walker = new ComplexityWalker();
        walker.Visit(node);
        return walker.Complexity;
    }

    private sealed class ComplexityWalker : CSharpSyntaxWalker
    {
        public ComplexityWalker()
            : base(SyntaxWalkerDepth.Node)
        {
        }

        public int Complexity { get; private set; } = 1;

        public override void VisitIfStatement(IfStatementSyntax node)
        {
            this.Complexity++;
            base.VisitIfStatement(node);
        }

        public override void VisitWhileStatement(WhileStatementSyntax node)
        {
            this.Complexity++;
            base.VisitWhileStatement(node);
        }

        public override void VisitDoStatement(DoStatementSyntax node)
        {
            this.Complexity++;
            base.VisitDoStatement(node);
        }

        public override void VisitForStatement(ForStatementSyntax node)
        {
            this.Complexity++;
            base.VisitForStatement(node);
        }

        public override void VisitForEachStatement(ForEachStatementSyntax node)
        {
            this.Complexity++;
            base.VisitForEachStatement(node);
        }

        public override void VisitCatchClause(CatchClauseSyntax node)
        {
            this.Complexity++;
            base.VisitCatchClause(node);
        }

        public override void VisitSwitchSection(SwitchSectionSyntax node)
        {
            // Each non-default case label adds 1.
            foreach (var label in node.Labels)
            {
                if (label is CaseSwitchLabelSyntax or CasePatternSwitchLabelSyntax)
                {
                    this.Complexity++;
                }
            }

            base.VisitSwitchSection(node);
        }

        public override void VisitSwitchExpressionArm(SwitchExpressionArmSyntax node)
        {
            // Every arm (including the discard/default arm) adds 1.
            this.Complexity++;
            base.VisitSwitchExpressionArm(node);
        }

        public override void VisitConditionalExpression(ConditionalExpressionSyntax node)
        {
            this.Complexity++;
            base.VisitConditionalExpression(node);
        }

        public override void VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            if (node.IsKind(SyntaxKind.LogicalAndExpression) ||
                node.IsKind(SyntaxKind.LogicalOrExpression) ||
                node.IsKind(SyntaxKind.CoalesceExpression))
            {
                this.Complexity++;
            }

            base.VisitBinaryExpression(node);
        }
    }
}
