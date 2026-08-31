using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

internal static class MemberContractResolver
{
    public static IEnumerable<MemberContractEdge> Resolve(
        IEnumerable<ISymbol> symbols)
    {
        var sourceSymbols = symbols
            .Select(Original)
            .ToHashSet(SymbolEqualityComparer.Default);

        foreach (var symbol in sourceSymbols)
        {
            switch (symbol)
            {
                case IPropertySymbol property:
                    foreach (var edge in SourceEdges(
                                 sourceSymbols,
                                 (property, property.GetMethod),
                                 (property, property.SetMethod),
                                 (property.OverriddenProperty, property)))
                    {
                        yield return edge;
                    }
                    break;
                case IEventSymbol @event:
                    foreach (var edge in SourceEdges(
                                 sourceSymbols,
                                 (@event, @event.AddMethod),
                                 (@event, @event.RemoveMethod),
                                 (@event, @event.RaiseMethod),
                                 (@event.OverriddenEvent, @event)))
                    {
                        yield return edge;
                    }
                    break;
                case IMethodSymbol method:
                    foreach (var edge in SourceEdges(
                                 sourceSymbols,
                                 (method.OverriddenMethod, method)))
                    {
                        yield return edge;
                    }
                    break;
            }
        }

        foreach (var type in sourceSymbols.OfType<INamedTypeSymbol>())
        {
            foreach (var interfaceType in type.AllInterfaces)
            {
                foreach (var interfaceMember in interfaceType.GetMembers())
                {
                    foreach (var edge in SourceEdges(
                                 sourceSymbols,
                                 (interfaceMember,
                                     type.FindImplementationForInterfaceMember(interfaceMember))))
                    {
                        yield return edge;
                    }
                }
            }
        }
    }

    private static IEnumerable<MemberContractEdge> SourceEdges(
        IReadOnlySet<ISymbol> sourceSymbols,
        params (ISymbol? From, ISymbol? To)[] candidates)
    {
        foreach (var (fromCandidate, toCandidate) in candidates)
        {
            if (fromCandidate is null || toCandidate is null)
            {
                continue;
            }

            var from = Original(fromCandidate);
            var to = Original(toCandidate);
            if (sourceSymbols.Contains(from) && sourceSymbols.Contains(to))
            {
                yield return new MemberContractEdge(from, to);
            }
        }
    }

    private static ISymbol Original(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.ReducedFrom?.OriginalDefinition ?? method.OriginalDefinition,
        IPropertySymbol property => property.OriginalDefinition,
        IEventSymbol @event => @event.OriginalDefinition,
        INamedTypeSymbol type => type.OriginalDefinition,
        _ => symbol.OriginalDefinition
    };

    internal static bool RunSelfTest()
    {
        const string source = """
                              using System;

                              namespace Fixture;

                              internal interface IContract
                              {
                                  string Name { get; set; }
                                  event EventHandler Changed;
                                  void Execute();
                              }

                              internal class Base
                              {
                                  public virtual string Value { get; set; } = "base";
                                  public virtual void Run() { }
                              }

                              internal sealed class Implementation : Base, IContract
                              {
                                  private EventHandler? _changed;
                                  public string Name { get; set; } = "fixture";
                                  public event EventHandler Changed
                                  {
                                      add => _changed += value;
                                      remove => _changed -= value;
                                  }
                                  public override string Value { get; set; } = "implementation";
                                  public void Execute() => _changed?.Invoke(this, EventArgs.Empty);
                                  public override void Run() { }
                              }
                              """;
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "MemberContractFixture",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var symbols = SourceSymbols(compilation.GlobalNamespace).ToArray();
        var edges = Resolve(symbols).ToArray();

        var requiredEdges = new[]
        {
            ("Fixture.IContract.Name", "Fixture.Implementation.Name"),
            ("Fixture.IContract.Execute()", "Fixture.Implementation.Execute()"),
            ("Fixture.Base.Value", "Fixture.Implementation.Value"),
            ("Fixture.Base.Run()", "Fixture.Implementation.Run()"),
            ("Fixture.Implementation.Name", "Fixture.Implementation.Name.get"),
            ("Fixture.Implementation.Name", "Fixture.Implementation.Name.set"),
            ("Fixture.Implementation.Changed", "Fixture.Implementation.Changed.add"),
            ("Fixture.Implementation.Changed", "Fixture.Implementation.Changed.remove")
        };
        var actual = edges
            .Select(edge => (Display(edge.From), Display(edge.To)))
            .ToHashSet();
        if (requiredEdges.Any(edge => !actual.Contains(edge)))
        {
            Console.Error.WriteLine("FAIL: member contract self-test missed a dispatch edge.");
            Console.Error.WriteLine(
                $"      edges: {string.Join(", ", actual.OrderBy(edge => edge.Item1).ThenBy(edge => edge.Item2).Select(edge => $"{edge.Item1} -> {edge.Item2}"))}");
            return false;
        }

        return true;
    }

    private static IEnumerable<ISymbol> SourceSymbols(INamespaceSymbol namespaceSymbol)
    {
        foreach (var type in namespaceSymbol.GetTypeMembers()
                     .Where(type => type.Locations.Any(location => location.IsInSource)))
        {
            yield return type;
            foreach (var member in type.GetMembers().Where(member =>
                         !member.IsImplicitlyDeclared
                         && member.Locations.Any(location => location.IsInSource)))
            {
                yield return member;
            }
        }

        foreach (var child in namespaceSymbol.GetNamespaceMembers())
        {
            foreach (var symbol in SourceSymbols(child))
            {
                yield return symbol;
            }
        }
    }

    private static string Display(ISymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
}

internal sealed record MemberContractEdge(ISymbol From, ISymbol To);
