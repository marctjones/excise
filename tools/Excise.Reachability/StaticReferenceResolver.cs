using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class StaticReferenceResolver
{
    public static ISymbol? Resolve(SemanticModel semanticModel, SyntaxNode node) =>
        node switch
        {
            IdentifierNameSyntax or GenericNameSyntax or MemberAccessExpressionSyntax
                or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax
                or InvocationExpressionSyntax
                => semanticModel.GetSymbolInfo(node).Symbol
                   ?? semanticModel.GetSymbolInfo(node).CandidateSymbols.FirstOrDefault(),
            AttributeSyntax attribute
                => semanticModel.GetSymbolInfo(attribute).Symbol
                   ?? semanticModel.GetTypeInfo(attribute).Type,
            _ => null
        };

    internal static bool RunSelfTest()
    {
        const string source = """
                              namespace Fixture;

                              internal sealed class Target
                              {
                                  public Target() { }
                              }

                              internal static class Factory
                              {
                                  public static Target Create() => new();
                              }
                              """;
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "StaticReferenceFixture",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var model = compilation.GetSemanticModel(tree);
        var creation = tree.GetRoot().DescendantNodes()
            .OfType<ImplicitObjectCreationExpressionSyntax>()
            .Single();
        var resolved = Resolve(model, creation) as IMethodSymbol;
        if (resolved is not
            {
                MethodKind: MethodKind.Constructor,
                ContainingType.Name: "Target"
            })
        {
            Console.Error.WriteLine(
                "FAIL: static reference self-test missed target-typed object creation.");
            return false;
        }

        return true;
    }
}
