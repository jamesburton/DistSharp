using DistSharp.Roslyn.Internal;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace DistSharp.Roslyn.Tests;

public sealed class CyclomaticComplexityCalculatorTests
{
    [Theory]
    [InlineData("void M() { }", 1)]
    [InlineData("void M() { if (true) { } }", 2)]
    [InlineData("void M() { if (true) { } else { } }", 2)]
    [InlineData("void M() { if (true && false) { } }", 3)]
    [InlineData("void M() { if (true || false) { } }", 3)]
    [InlineData("void M() { while (true) { } }", 2)]
    [InlineData("void M() { for (var i = 0; i < 10; i++) { } }", 2)]
    [InlineData("void M() { foreach (var x in new int[0]) { } }", 2)]
    [InlineData("void M() { do { } while (true); }", 2)]
    [InlineData("void M() { try { } catch (System.Exception) { } }", 2)]
    [InlineData("void M() { try { } catch (System.Exception) { } catch (System.ArgumentException) { } }", 3)]
    [InlineData("int M(int x) => x switch { 1 => 1, 2 => 2, _ => 0 };", 4)]
    [InlineData("void M(int x) { switch (x) { case 1: break; case 2: break; default: break; } }", 3)]
    [InlineData("int M(int? x) => x ?? 0;", 2)]
    [InlineData("int M(bool b, int x) => b ? x : 0;", 2)]
    [InlineData("void M() { if (true) if (false) { } }", 3)]
    public void Compute_ReturnsExpectedComplexity(string methodSource, int expected)
    {
        var source = $"class C {{ {methodSource} }}";
        var tree = CSharpSyntaxTree.ParseText(source);
        var method = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax>().First();

        var result = CyclomaticComplexityCalculator.Compute(method);

        result.Should().Be(expected);
    }
}
