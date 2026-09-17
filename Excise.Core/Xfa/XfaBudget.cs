using System.Diagnostics;

namespace Excise.Core.Xfa;

/// <summary>
/// Raised when an XFA form cannot be laid out: malformed, unsupported in a way
/// that would make the rendition misleading, or over a resource bound. The
/// caller turns it into a <see cref="XfaLayoutStatus.Failed"/> result and
/// leaves the document untouched.
/// </summary>
internal sealed class XfaLayoutException : Exception
{
    public XfaLayoutException(string message) : base(message) { }
}

/// <summary>
/// Resource bounds for one layout run (#1547). XFA is untrusted XML: every
/// loop that can be driven by the input ticks this budget, so a hostile form
/// costs at most the limits below, never unbounded time or memory.
/// </summary>
internal sealed class XfaBudget
{
    /// <summary>Largest combined /XFA packet data accepted, in bytes.</summary>
    internal const int MaxPacketBytes = 32 * 1024 * 1024;

    /// <summary>Template or data elements visited, in total.</summary>
    internal const int MaxElements = 250_000;

    /// <summary>Container nesting depth.</summary>
    internal const int MaxDepth = 128;

    /// <summary>Prototype (<c>use</c>/<c>usehref</c>) chain length.</summary>
    internal const int MaxProtoChain = 32;

    /// <summary>Repeated-subform instances created by <c>occur</c>, in total.</summary>
    internal const int MaxInstances = 20_000;

    /// <summary>Instances of one subform when <c>occur max="-1"</c>.</summary>
    internal const int MaxInstancesPerSubform = 2_000;

    /// <summary>Laid-out boxes, in total.</summary>
    internal const int MaxBoxes = 200_000;

    /// <summary>Pages produced.</summary>
    internal const int MaxPages = 1_000;

    private readonly CancellationToken _cancellationToken;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TimeSpan _timeLimit;
    private int _ticks;
    private int _elements;
    private int _instances;
    private int _boxes;

    public XfaBudget(TimeSpan timeLimit, CancellationToken cancellationToken)
    {
        _timeLimit = timeLimit;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Check cancellation and the wall clock. Cheap; call in every loop.</summary>
    public void Tick()
    {
        if ((++_ticks & 0x3F) != 0)
            return;
        _cancellationToken.ThrowIfCancellationRequested();
        if (_clock.Elapsed > _timeLimit)
            throw new XfaLayoutException($"XFA layout exceeded its time limit of {_timeLimit.TotalSeconds:0.#} s.");
    }

    public void CountElement()
    {
        Tick();
        if (++_elements > MaxElements)
            throw new XfaLayoutException($"The XFA form has more than {MaxElements} elements.");
    }

    public void CountInstance()
    {
        Tick();
        if (++_instances > MaxInstances)
            throw new XfaLayoutException($"The XFA form repeats subforms more than {MaxInstances} times.");
    }

    public void CountBox()
    {
        Tick();
        if (++_boxes > MaxBoxes)
            throw new XfaLayoutException($"The XFA layout produced more than {MaxBoxes} boxes.");
    }

    public static void CheckDepth(int depth)
    {
        if (depth > MaxDepth)
            throw new XfaLayoutException($"The XFA form nests containers deeper than {MaxDepth} levels.");
    }
}
