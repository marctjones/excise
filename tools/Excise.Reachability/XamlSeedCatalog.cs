using System.Text.RegularExpressions;
using System.Xml.Linq;

internal sealed class XamlSeedCatalog
{
    private static readonly Regex BindingPattern = new(
        @"\{(?:Compiled)?Binding\s+(?<path>[^,}]+)",
        RegexOptions.CultureInvariant);
    private static readonly Regex ExplicitDataContextPattern = new(
        @"\(\((?<type>[A-Za-z_][A-Za-z0-9_]*:[A-Za-z_][A-Za-z0-9_.]*)\)DataContext\)\.(?<path>[A-Za-z_][A-Za-z0-9_.]*)",
        RegexOptions.CultureInvariant);
    private static readonly Regex StaticMemberPattern = new(
        @"\{x:Static\s+(?<type>[A-Za-z_][A-Za-z0-9_]*:[A-Za-z_][A-Za-z0-9_.]*)\.(?<member>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.CultureInvariant);
    private static readonly Regex IdentifierPattern = new(
        @"^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex PathSegmentPattern = new(
        @"[A-Za-z_][A-Za-z0-9_]*",
        RegexOptions.CultureInvariant);

    private readonly HashSet<string> _types = new(StringComparer.Ordinal);
    private readonly HashSet<XamlQualifiedMember> _qualifiedMembers = [];
    private readonly HashSet<XamlBindingPath> _bindingPaths = [];

    public IReadOnlySet<string> Types => _types;
    public IReadOnlySet<XamlQualifiedMember> QualifiedMembers => _qualifiedMembers;
    public IReadOnlySet<XamlBindingPath> BindingPaths => _bindingPaths;
    public int UntypedBindingPaths => _bindingPaths.Count(path => path.ContextType is null);
    public IReadOnlyList<string> UntypedBindingPathNames => _bindingPaths
        .Where(path => path.ContextType is null)
        .Select(path => string.Join('.', path.Segments))
        .Order(StringComparer.Ordinal)
        .ToArray();
    public int Observations => _types.Count + _qualifiedMembers.Count + _bindingPaths.Count;

    public void Add(string text)
    {
        var document = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        if (document.Root is null)
        {
            return;
        }

        var namespaces = document.Root.Attributes()
            .Where(attribute => attribute.IsNamespaceDeclaration)
            .ToDictionary(
                attribute => attribute.Name.LocalName == "xmlns" ? "" : attribute.Name.LocalName,
                attribute => attribute.Value,
                StringComparer.Ordinal);
        var rootClass = document.Root.Attributes()
            .Where(attribute => attribute.Name.LocalName == "Class")
            .Select(attribute => ResolveTypeName(attribute.Value, namespaces))
            .FirstOrDefault(type => type is not null);
        if (rootClass is not null)
        {
            _types.Add(rootClass);
        }

        Visit(document.Root, rootClass, inheritedContext: null, namespaces);
    }

    private void Visit(
        XElement element,
        string? rootClass,
        string? inheritedContext,
        IReadOnlyDictionary<string, string> namespaces)
    {
        var elementType = ResolveElementType(element);
        if (elementType is not null)
        {
            _types.Add(elementType);
        }

        var declaredContext = element.Attributes()
            .Where(attribute => attribute.Name.LocalName == "DataType")
            .Select(attribute => ResolveTypeName(attribute.Value, namespaces))
            .FirstOrDefault(type => type is not null);
        var isTemplate = element.Name.LocalName.EndsWith("Template", StringComparison.Ordinal);
        var context = declaredContext ?? (isTemplate ? null : inheritedContext);
        if (declaredContext is not null)
        {
            _types.Add(declaredContext);
        }

        foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
        {
            CollectAttributeReference(attribute, elementType, rootClass, context, namespaces);
        }

        foreach (var child in element.Elements())
        {
            Visit(child, rootClass, context, namespaces);
        }
    }

    private void CollectAttributeReference(
        XAttribute attribute,
        string? elementType,
        string? rootClass,
        string? context,
        IReadOnlyDictionary<string, string> namespaces)
    {
        var localName = attribute.Name.LocalName;
        if (elementType is not null
            && attribute.Name.NamespaceName.Length == 0
            && localName is not "Class" and not "DataType" and not "Name" and not "Key")
        {
            _qualifiedMembers.Add(new XamlQualifiedMember(
                elementType,
                MemberName(localName),
                "custom-control property"));
        }

        var attachedMemberSeparator = localName.LastIndexOf('.');
        var attachedType = ResolveNamespaceType(
            attribute.Name.NamespaceName,
            attachedMemberSeparator > 0 ? localName[..attachedMemberSeparator] : null);
        if (attachedType is not null)
        {
            _types.Add(attachedType);
            _qualifiedMembers.Add(new XamlQualifiedMember(
                attachedType,
                MemberName(localName),
                "attached property"));
        }

        if (rootClass is not null && IdentifierPattern.IsMatch(attribute.Value))
        {
            _qualifiedMembers.Add(new XamlQualifiedMember(
                rootClass,
                attribute.Value,
                "code-behind handler candidate"));
        }

        foreach (Match match in BindingPattern.Matches(attribute.Value))
        {
            CollectBindingPath(match.Groups["path"].Value, context, namespaces);
        }

        foreach (Match match in StaticMemberPattern.Matches(attribute.Value))
        {
            var type = ResolveTypeName(match.Groups["type"].Value, namespaces);
            if (type is null)
            {
                continue;
            }

            _types.Add(type);
            _qualifiedMembers.Add(new XamlQualifiedMember(
                type,
                match.Groups["member"].Value,
                "static member"));
        }
    }

    private void CollectBindingPath(
        string rawPath,
        string? inheritedContext,
        IReadOnlyDictionary<string, string> namespaces)
    {
        var path = rawPath.Trim();
        if (path.StartsWith("Path=", StringComparison.Ordinal))
        {
            path = path[5..].Trim();
        }
        path = path.TrimStart('!', '^');

        var explicitContext = ExplicitDataContextPattern.Match(path);
        var context = inheritedContext;
        if (explicitContext.Success)
        {
            context = ResolveTypeName(explicitContext.Groups["type"].Value, namespaces);
            path = explicitContext.Groups["path"].Value;
        }

        var segments = PathSegmentPattern.Matches(path)
            .Select(match => match.Value)
            .Where(segment => segment is not "parent" and not "DataContext")
            .ToArray();
        if (segments.Length == 0)
        {
            return;
        }

        _bindingPaths.Add(new XamlBindingPath(context, segments));
    }

    private static string MemberName(string localName)
    {
        var separator = localName.LastIndexOf('.');
        return separator >= 0 ? localName[(separator + 1)..] : localName;
    }

    private static string? ResolveElementType(XElement element) =>
        ResolveNamespaceType(element.Name.NamespaceName, element.Name.LocalName);

    private static string? ResolveTypeName(
        string rawName,
        IReadOnlyDictionary<string, string> namespaces)
    {
        var name = rawName.Trim();
        var separator = name.IndexOf(':');
        if (separator > 0)
        {
            return namespaces.TryGetValue(name[..separator], out var namespaceName)
                ? ResolveNamespaceType(namespaceName, name[(separator + 1)..])
                : null;
        }

        return name.Contains('.', StringComparison.Ordinal) ? name : null;
    }

    private static string? ResolveNamespaceType(string namespaceName, string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return null;
        }

        const string usingPrefix = "using:";
        const string clrPrefix = "clr-namespace:";
        string? resolvedNamespace = null;
        if (namespaceName.StartsWith(usingPrefix, StringComparison.Ordinal))
        {
            resolvedNamespace = namespaceName[usingPrefix.Length..];
        }
        else if (namespaceName.StartsWith(clrPrefix, StringComparison.Ordinal))
        {
            resolvedNamespace = namespaceName[clrPrefix.Length..].Split(';', 2)[0];
        }

        return resolvedNamespace?.StartsWith("Excise", StringComparison.Ordinal) == true
            ? $"{resolvedNamespace}.{typeName}"
            : null;
    }

    internal static bool RunSelfTest()
    {
        const string fixture = """
                               <Window xmlns="https://github.com/avaloniaui"
                                       xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                       xmlns:vm="using:Excise.App.ViewModels"
                                       xmlns:controls="using:Excise.Avalonia.Controls"
                                       x:Class="Excise.App.Views.MainWindow"
                                       x:DataType="vm:MainWindowViewModel">
                                 <controls:PdfViewerControl Document="{Binding Document}" Click="OnClick" />
                                 <TextBlock Text="{x:Static vm:MainWindowViewModel.StaticLabel}" />
                                 <DataTemplate x:DataType="vm:ItemViewModel">
                                   <TextBlock Text="{Binding Child.Name}" />
                                 </DataTemplate>
                                 <DataTemplate>
                                   <TextBlock Text="{Binding ReflectionOnly}" />
                                 </DataTemplate>
                               </Window>
                               """;
        var catalog = new XamlSeedCatalog();
        catalog.Add(fixture);
        var requiredTypes = new[]
        {
            "Excise.App.Views.MainWindow",
            "Excise.App.ViewModels.MainWindowViewModel",
            "Excise.App.ViewModels.ItemViewModel",
            "Excise.Avalonia.Controls.PdfViewerControl"
        };
        if (requiredTypes.Any(type => !catalog.Types.Contains(type))
            || !catalog.QualifiedMembers.Contains(new XamlQualifiedMember(
                "Excise.App.Views.MainWindow", "OnClick", "code-behind handler candidate"))
            || !catalog.QualifiedMembers.Contains(new XamlQualifiedMember(
                "Excise.Avalonia.Controls.PdfViewerControl", "Document", "custom-control property"))
            || !catalog.QualifiedMembers.Contains(new XamlQualifiedMember(
                "Excise.App.ViewModels.MainWindowViewModel", "StaticLabel", "static member"))
            || !catalog.BindingPaths.Contains(new XamlBindingPath(
                "Excise.App.ViewModels.ItemViewModel", ["Child", "Name"]))
            || !catalog.BindingPaths.Contains(new XamlBindingPath(null, ["ReflectionOnly"]))
            || catalog.UntypedBindingPaths != 1)
        {
            Console.Error.WriteLine("FAIL: XAML seed catalog self-test missed a structural contract.");
            return false;
        }

        return true;
    }
}

internal sealed record XamlQualifiedMember(
    string ContainingType,
    string Member,
    string Reason);

internal sealed class XamlBindingPath(
    string? contextType,
    IReadOnlyList<string> segments) : IEquatable<XamlBindingPath>
{
    public string? ContextType { get; } = contextType;
    public IReadOnlyList<string> Segments { get; } = segments;

    public bool Equals(XamlBindingPath? other) =>
        other is not null
        && string.Equals(ContextType, other.ContextType, StringComparison.Ordinal)
        && Segments.SequenceEqual(other.Segments, StringComparer.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as XamlBindingPath);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ContextType, StringComparer.Ordinal);
        foreach (var segment in Segments)
        {
            hash.Add(segment, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }
}
