using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal enum DynamicInvocationKind
{
    None,
    DependencyInjectionRegistration,
    ExternalAssemblyLoad,
    ScriptGlobals
}

internal enum DynamicAttributeKind
{
    None,
    SourceGenerationRoot,
    NativeImport,
    NativeCallback
}

internal static class DynamicMechanismClassifier
{
    public static DynamicInvocationKind Classify(IMethodSymbol method)
    {
        var namespaceName = method.ContainingNamespace?.ToDisplayString() ?? "";
        var typeName = method.ContainingType?.ToDisplayString() ?? "";
        if (method.Name is "AddSingleton" or "AddScoped" or "AddTransient"
            && namespaceName.StartsWith(
                "Microsoft.Extensions.DependencyInjection",
                StringComparison.Ordinal))
        {
            return DynamicInvocationKind.DependencyInjectionRegistration;
        }

        if (typeName == "System.Reflection.Assembly"
            && method.Name is "Load" or "LoadFrom" or "LoadFile")
        {
            return DynamicInvocationKind.ExternalAssemblyLoad;
        }

        if (typeName == "Microsoft.CodeAnalysis.CSharp.Scripting.CSharpScript"
            && method.Name == "Create")
        {
            return DynamicInvocationKind.ScriptGlobals;
        }

        return DynamicInvocationKind.None;
    }

    public static INamedTypeSymbol? ResolveScriptGlobalsType(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel) =>
        invocation.ArgumentList.Arguments
            .Select(argument => argument.Expression)
            .OfType<TypeOfExpressionSyntax>()
            .Select(expression => semanticModel.GetTypeInfo(expression.Type).Type)
            .OfType<INamedTypeSymbol>()
            .LastOrDefault();

    public static INamedTypeSymbol? ResolveDependencyInjectionActivationType(
        IMethodSymbol method)
    {
        if (Classify(method) != DynamicInvocationKind.DependencyInjectionRegistration
            || method.Parameters.Any(parameter => parameter.Type.TypeKind == TypeKind.Delegate))
        {
            return null;
        }

        return method.TypeArguments.OfType<INamedTypeSymbol>().LastOrDefault();
    }

    public static DynamicAttributeKind Classify(INamedTypeSymbol? attributeType)
    {
        return attributeType?.ToDisplayString() switch
        {
            "System.Text.Json.Serialization.JsonSerializableAttribute"
                or "System.Text.RegularExpressions.GeneratedRegexAttribute"
                => DynamicAttributeKind.SourceGenerationRoot,
            "System.Runtime.InteropServices.LibraryImportAttribute"
                or "System.Runtime.InteropServices.DllImportAttribute"
                => DynamicAttributeKind.NativeImport,
            "System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute"
                => DynamicAttributeKind.NativeCallback,
            _ => DynamicAttributeKind.None
        };
    }

    internal static bool RunSelfTest()
    {
        const string source = """
                              using System;
                              using System.Reflection;
                              using System.Runtime.InteropServices;
                              using System.Text.Json.Serialization;

                              namespace Microsoft.Extensions.DependencyInjection
                              {
                                  public static class ServiceCollectionExtensions
                                  {
                                      public static void AddSingleton<T>(object services) { }
                                      public static void AddSingleton<T>(object services, Func<object, T> factory) { }
                                  }
                              }

                              namespace Microsoft.CodeAnalysis.CSharp.Scripting
                              {
                                  public static class CSharpScript
                                  {
                                      public static object Create(string code, object options, Type globalsType) => new();
                                  }
                              }

                              [JsonSerializable(typeof(Globals))]
                              internal partial class JsonContext { }
                              internal sealed class Globals { }

                              internal static class DynamicEntries
                              {
                                  [DllImport("fixture")]
                                  private static extern void NativeImport();

                                  [UnmanagedCallersOnly]
                                  public static void NativeCallback() { }

                                  public static void Exercise()
                                  {
                                      Microsoft.Extensions.DependencyInjection.ServiceCollectionExtensions
                                          .AddSingleton<Globals>(new object());
                                      Microsoft.Extensions.DependencyInjection.ServiceCollectionExtensions
                                          .AddSingleton<Globals>(new object(), _ => new Globals());
                                      Assembly.Load("System.Runtime");
                                      Microsoft.CodeAnalysis.CSharp.Scripting.CSharpScript
                                          .Create("return 1;", new object(), typeof(Globals));
                                  }
                              }
                              """;
        var tree = CSharpSyntaxTree.ParseText(source);
        var references = new[]
        {
            typeof(object).Assembly,
            typeof(System.Runtime.InteropServices.DllImportAttribute).Assembly,
            typeof(System.Text.Json.Serialization.JsonSerializableAttribute).Assembly
        }
            .DistinctBy(assembly => assembly.Location, StringComparer.Ordinal)
            .Select(assembly => MetadataReference.CreateFromFile(assembly.Location));
        var compilation = CSharpCompilation.Create("DynamicFixture", [tree], references);
        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();
        var invocationKinds = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(invocation => model.GetSymbolInfo(invocation).Symbol as IMethodSymbol)
            .Where(method => method is not null)
            .Select(method => Classify(method!))
            .ToHashSet();
        var requiredInvocations = new[]
        {
            DynamicInvocationKind.DependencyInjectionRegistration,
            DynamicInvocationKind.ExternalAssemblyLoad,
            DynamicInvocationKind.ScriptGlobals
        };
        if (requiredInvocations.Any(kind => !invocationKinds.Contains(kind)))
        {
            Console.Error.WriteLine("FAIL: dynamic mechanism self-test missed an invocation contract.");
            return false;
        }

        var dependencyInjectionMethods = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(invocation => model.GetSymbolInfo(invocation).Symbol as IMethodSymbol)
            .Where(method => method is not null
                             && Classify(method) == DynamicInvocationKind.DependencyInjectionRegistration)
            .ToArray();
        if (!dependencyInjectionMethods.Any(method =>
                ResolveDependencyInjectionActivationType(method!)?.Name == "Globals")
            || !dependencyInjectionMethods.Any(method =>
                ResolveDependencyInjectionActivationType(method!) is null))
        {
            Console.Error.WriteLine("FAIL: dynamic mechanism self-test misclassified DI activation.");
            return false;
        }

        var scriptInvocation = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(invocation =>
                model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method
                && Classify(method) == DynamicInvocationKind.ScriptGlobals);
        var globals = ResolveScriptGlobalsType(scriptInvocation, model);
        if (globals?.Name != "Globals")
        {
            Console.Error.WriteLine("FAIL: dynamic mechanism self-test did not resolve script globals.");
            return false;
        }

        var attributeKinds = root.DescendantNodes()
            .OfType<AttributeSyntax>()
            .Select(attribute => Classify(
                model.GetTypeInfo(attribute).Type as INamedTypeSymbol))
            .ToHashSet();
        var requiredAttributes = new[]
        {
            DynamicAttributeKind.SourceGenerationRoot,
            DynamicAttributeKind.NativeImport,
            DynamicAttributeKind.NativeCallback
        };
        if (requiredAttributes.Any(kind => !attributeKinds.Contains(kind)))
        {
            Console.Error.WriteLine("FAIL: dynamic mechanism self-test missed an attribute contract.");
            return false;
        }

        return true;
    }
}
