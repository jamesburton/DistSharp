using DistSharp.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DistSharp.Roslyn.Internal;

/// <summary>Walks a single syntax tree and yields <see cref="ExtractedSymbol"/> records for each eligible declaration.</summary>
internal sealed class SymbolExtractor
{
    private static readonly SymbolDisplayFormat FullNameFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private readonly SemanticModel semanticModel;
    private readonly string relativeFilePath;
    private readonly Func<INamespaceSymbol?, bool> namespaceIncluded;

    /// <summary>Initializes a new instance of the <see cref="SymbolExtractor"/> class.</summary>
    /// <param name="semanticModel">The Roslyn semantic model for the syntax tree being walked.</param>
    /// <param name="relativeFilePath">Repository-relative path to the source file, used to populate <see cref="ExtractedSymbol.FilePath"/>.</param>
    /// <param name="namespaceIncluded">Predicate that returns <see langword="true"/> when a namespace should be included.</param>
    public SymbolExtractor(SemanticModel semanticModel, string relativeFilePath, Func<INamespaceSymbol?, bool> namespaceIncluded)
    {
        this.semanticModel = semanticModel;
        this.relativeFilePath = relativeFilePath;
        this.namespaceIncluded = namespaceIncluded;
    }

    /// <summary>Walks the tree and yields one <see cref="ExtractedSymbol"/> per qualifying type or member.</summary>
    /// <param name="root">The root node of the syntax tree to walk.</param>
    /// <param name="cancellationToken">Token to cancel enumeration between nodes.</param>
    /// <returns>A sequence of <see cref="ExtractedSymbol"/> records for each eligible declaration.</returns>
    public IEnumerable<ExtractedSymbol> Extract(SyntaxNode root, CancellationToken cancellationToken)
    {
        foreach (var node in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var symbol = this.semanticModel.GetDeclaredSymbol(node, cancellationToken);
            if (symbol is null)
            {
                continue;
            }

            if (!IsAccessible(symbol))
            {
                continue;
            }

            if (!this.namespaceIncluded(symbol.ContainingNamespace))
            {
                continue;
            }

            var extracted = node switch
            {
                MethodDeclarationSyntax method => this.BuildForMethod(method, symbol, cancellationToken),
                PropertyDeclarationSyntax property => this.BuildForProperty(property, symbol),
                ConstructorDeclarationSyntax ctor => this.BuildForConstructor(ctor, symbol, cancellationToken),
                ClassDeclarationSyntax c => this.BuildForType(c, symbol, "class"),
                StructDeclarationSyntax s => this.BuildForType(s, symbol, "struct"),
                RecordDeclarationSyntax r => this.BuildForType(r, symbol, r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record-struct" : "record"),
                InterfaceDeclarationSyntax i => this.BuildForType(i, symbol, "interface"),
                EnumDeclarationSyntax e => this.BuildForType(e, symbol, "enum"),
                _ => null,
            };

            if (extracted is not null)
            {
                yield return extracted;
            }
        }
    }

    private static bool IsAccessible(ISymbol symbol) =>
        symbol.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal;

    private static string ExtractTypeSignature(BaseTypeDeclarationSyntax node)
    {
        // Modifiers + keyword + identifier + (type params) + (base list)
        var parts = new List<string>();
        if (node.Modifiers.Count > 0)
        {
            parts.Add(node.Modifiers.ToString());
        }

        // TypeDeclarationSyntax (class/struct/record/interface) exposes Keyword;
        // EnumDeclarationSyntax exposes EnumKeyword — extract the token text for each.
        if (node is TypeDeclarationSyntax typed)
        {
            parts.Add(typed.Keyword.ToString());
            var identifier = typed.Identifier.ToString();
            if (typed.TypeParameterList is { } tpl)
            {
                identifier += tpl.ToString();
            }

            parts.Add(identifier);
        }
        else if (node is EnumDeclarationSyntax enumDecl)
        {
            parts.Add(enumDecl.EnumKeyword.ToString());
            parts.Add(enumDecl.Identifier.ToString());
        }
        else
        {
            parts.Add(node.Identifier.ToString());
        }

        if (node.BaseList is not null)
        {
            parts.Add(node.BaseList.ToString());
        }

        return string.Join(' ', parts);
    }

    private static string? ExtractDocComment(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        return string.IsNullOrWhiteSpace(xml) ? null : xml.Trim();
    }

    private ExtractedSymbol BuildForMethod(MethodDeclarationSyntax node, ISymbol symbol, CancellationToken cancellationToken)
    {
        var body = (SyntaxNode?)node.Body ?? node.ExpressionBody;
        var complexity = body is null ? 1 : CyclomaticComplexityCalculator.Compute(body);

        return new ExtractedSymbol
        {
            FullyQualifiedName = symbol.ToDisplayString(FullNameFormat),
            SignatureText = node.WithBody(null).WithExpressionBody(null).ToString().TrimEnd(';', ' ', '\r', '\n'),
            BodyText = body?.ToFullString().Trim() ?? string.Empty,
            XmlDocComment = ExtractDocComment(symbol),
            ContainingType = symbol.ContainingType?.Name ?? string.Empty,
            Namespace = symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty,
            FilePath = this.relativeFilePath,
            Complexity = complexity,
            Kind = "method",
        };
    }

    private ExtractedSymbol BuildForProperty(PropertyDeclarationSyntax node, ISymbol symbol)
    {
        return new ExtractedSymbol
        {
            FullyQualifiedName = symbol.ToDisplayString(FullNameFormat),
            SignatureText = $"{node.Modifiers} {node.Type} {node.Identifier}".Trim(),
            BodyText = node.ToFullString().Trim(),
            XmlDocComment = ExtractDocComment(symbol),
            ContainingType = symbol.ContainingType?.Name ?? string.Empty,
            Namespace = symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty,
            FilePath = this.relativeFilePath,
            Complexity = 1,
            Kind = "property",
        };
    }

    private ExtractedSymbol BuildForConstructor(ConstructorDeclarationSyntax node, ISymbol symbol, CancellationToken cancellationToken)
    {
        var body = (SyntaxNode?)node.Body ?? node.ExpressionBody;
        var complexity = body is null ? 1 : CyclomaticComplexityCalculator.Compute(body);

        return new ExtractedSymbol
        {
            FullyQualifiedName = symbol.ToDisplayString(FullNameFormat),
            SignatureText = node.WithBody(null).WithExpressionBody(null).ToString().TrimEnd(';', ' ', '\r', '\n'),
            BodyText = body?.ToFullString().Trim() ?? string.Empty,
            XmlDocComment = ExtractDocComment(symbol),
            ContainingType = symbol.ContainingType?.Name ?? string.Empty,
            Namespace = symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty,
            FilePath = this.relativeFilePath,
            Complexity = complexity,
            Kind = "constructor",
        };
    }

    private ExtractedSymbol BuildForType(BaseTypeDeclarationSyntax node, ISymbol symbol, string kind)
    {
        return new ExtractedSymbol
        {
            FullyQualifiedName = symbol.ToDisplayString(FullNameFormat),
            SignatureText = ExtractTypeSignature(node),
            BodyText = node.ToFullString().Trim(),
            XmlDocComment = ExtractDocComment(symbol),
            ContainingType = symbol.ContainingType?.Name ?? string.Empty,
            Namespace = symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty,
            FilePath = this.relativeFilePath,
            Complexity = 1,
            Kind = kind,
        };
    }
}
