namespace Excise.Core.Xfa.FormCalc;

/// <summary>
/// One object of the form model as a script sees it (a subform, field, draw or exclusion group).
/// This is the whole of what a script can reach: there is no way from here to a file, the network,
/// the clipboard or the host application.
/// </summary>
internal interface IFcObject
{
    /// <summary>The object's <c>name</c>, or the empty string.</summary>
    string Name { get; }

    /// <summary>The XFA class: "field", "subform", "draw", "exclGroup", "area"...</summary>
    string ClassName { get; }

    IFcObject? Parent { get; }

    IReadOnlyList<IFcObject> Children { get; }

    /// <summary>A property such as <c>rawValue</c> or <c>presence</c>; false when the object has no such property.</summary>
    bool TryGetProperty(string name, out object? value);

    /// <summary>Set a property. False when the property does not exist or cannot be written by a script.</summary>
    bool TrySetProperty(string name, object? value);
}

/// <summary>What the interpreter needs from its surroundings for one script.</summary>
internal interface IFcHost
{
    /// <summary>The object the script belongs to (<c>this</c>, <c>$</c>).</summary>
    IFcObject Context { get; }

    /// <summary>
    /// A SOM root such as <c>$record</c>, <c>$form</c>, <c>$data</c>, <c>$template</c>, <c>$host</c>,
    /// <c>!</c> or <c>xfa</c>: an <see cref="IFcObject"/>, a list of them, or null when it does not exist.
    /// </summary>
    object? ResolveRoot(string name);
}

/// <summary>An ordered set of objects: the value of <c>a.b[*]</c> or of a name that matched several.</summary>
internal sealed class FcNodeList : List<IFcObject>
{
    public FcNodeList() { }
    public FcNodeList(IEnumerable<IFcObject> items) : base(items) { }
}

/// <summary>Bounds for one script run. Every loop and call ticks these.</summary>
internal sealed class FcLimits
{
    public long MaxSteps { get; init; } = 2_000_000;
    public int MaxCallDepth { get; init; } = 64;
    public int MaxStringLength { get; init; } = 1_000_000;
    public int MaxListItems { get; init; } = 100_000;
    public int MaxEvalDepth { get; init; } = 4;
    public TimeSpan TimeLimit { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>A script error: the whole script is abandoned and none of its effects are kept.</summary>
internal sealed class FormCalcRuntimeException : Exception
{
    public FormCalcRuntimeException(string message) : base(message) { }
}
