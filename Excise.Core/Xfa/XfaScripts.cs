using System.Xml.Linq;
using Excise.Core.Xfa.FormCalc;

namespace Excise.Core.Xfa;

/// <summary>
/// Runs a dynamic form's FormCalc <c>initialize</c> and <c>calculate</c> scripts against the merged form,
/// before layout (#1570). Called from <see cref="PdfXfaLayout.ApplyXfaLayout"/> at open and from nowhere
/// else: redaction, save, print-copy creation and the command line never run a script.
/// </summary>
/// <remarks>
/// A script can write two things, a field's value and any object's presence, and nothing else. Each script
/// is a transaction: if it fails, whatever it wrote is undone and it is reported, and the rest carry on.
/// JavaScript, validate scripts and every event but initialize and calculate are not run.
/// </remarks>
internal static class XfaScripts
{
    /// <summary>Calculate passes before giving up on a form whose values keep changing.</summary>
    internal const int MaxCalculatePasses = 10;

    /// <summary>Scripts run for one form, in total.</summary>
    internal const int MaxScriptRuns = 20_000;

    /// <summary>Wall-clock time for all scripts of one form.</summary>
    internal static readonly TimeSpan TotalTimeLimit = TimeSpan.FromSeconds(5);

    public static void Run(XfaFormNode root, XfaBudget budget, XfaReport report, CancellationToken cancellation)
    {
        var model = new ScriptModel(root, report);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var runs = 0;

        bool CanRun()
        {
            budget.Tick();
            if (runs >= MaxScriptRuns) { report.Note($"FormCalc: stopped after {MaxScriptRuns} scripts"); return false; }
            if (clock.Elapsed > TotalTimeLimit) { report.Note("FormCalc: stopped at the time limit"); return false; }
            return true;
        }

        // 1. initialize, in document order.
        foreach (var node in model.Nodes)
        {
            foreach (var ev in node.Form!.Element.ChildrenNamed("event"))
            {
                if (ev.AttrOr("activity", "") != "initialize" || FormCalcSource(ev.Child("script")) is not { } source)
                    continue;
                if (!CanRun()) return;
                runs++;
                RunOne(model, node, source, "initialize", cancellation, report, store: false, out _);
            }
        }

        // 2. calculate, until the values settle. A script that failed is not run again.
        var failed = new HashSet<ScriptNode>();
        for (var pass = 1; pass <= MaxCalculatePasses; pass++)
        {
            var changed = false;
            foreach (var node in model.Nodes)
            {
                var calc = node.Form!.Element.Child("calculate");
                if (calc == null || failed.Contains(node) || calc.AttrOr("override", "error") == "disabled"
                    || FormCalcSource(calc.Child("script")) is not { } source)
                    continue;
                if (!CanRun()) return;
                runs++;
                if (RunOne(model, node, source, "calculate", cancellation, report, store: node.Form!.Kind == XfaNodeKind.Field, out var ok))
                    changed = true;
                if (!ok) failed.Add(node);
            }
            if (!changed) return;
            if (pass == MaxCalculatePasses)
                report.Note($"FormCalc: calculations were still changing after {MaxCalculatePasses} passes");
        }
    }

    /// <summary>The script text when it is FormCalc (the default language), else null.</summary>
    private static string? FormCalcSource(XElement? script)
    {
        if (script == null) return null;
        var type = script.AttrOr("contentType", "application/x-formcalc");
        if (!type.Contains("formcalc", StringComparison.OrdinalIgnoreCase)) return null;
        var text = script.Value;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>Run one script; true when it changed the form. Failure undoes the script's writes.</summary>
    private static bool RunOne(ScriptModel model, ScriptNode node, string source, string activity,
        CancellationToken cancellation, XfaReport report, bool store, out bool ok)
    {
        ok = true;
        model.BeginScript();
        try
        {
            var host = new Host(model, node);
            var result = FormCalcInterpreter.Evaluate(source, host, new FcLimits(), cancellation);
            if (store && FormCalcValue.Scalar(result) is { } scalar)
            {
                var text = scalar is double d ? FormCalcValue.NumberToText(d) : (string)scalar;
                model.Write(node, text);
            }
            var changed = model.EndScript();
            report.ScriptRan(activity);
            return changed;
        }
        catch (Exception ex) when (ex is FormCalcSyntaxException or FormCalcRuntimeException)
        {
            model.AbortScript();
            ok = false;
            report.ScriptFailed($"{activity} on '{node.Name}': {ex.Message}");
            return false;
        }
    }

    // The form as a script sees it ---------------------------------------------------------------

    private sealed class Host(ScriptModel model, ScriptNode context) : IFcHost
    {
        public IFcObject Context => context;

        public object? ResolveRoot(string name) => name switch
        {
            "xfa" => model.Xfa,
            "$form" => model.FormRoot,
            _ => null,
        };
    }

    private sealed class ScriptModel
    {
        private readonly XfaReport _report;
        private readonly List<(ScriptNode Node, string? Value, XElement? Rich, string? Presence)> _undo = new();
        private bool _changed;

        public ScriptModel(XfaFormNode root, XfaReport report)
        {
            _report = report;
            var top = Wrap(root, parent: null, Nodes);
            // xfa -> form -> <root subform>: what SOM paths like xfa.form.form1.field expect.
            var form = new ScriptNode(this, null, "form", "form", parent: null);
            var xfa = new ScriptNode(this, null, "xfa", "xfa", parent: null);
            xfa.AddChild(form);
            form.SetParent(xfa);
            form.AddChild(top);
            top.SetParent(form);
            FormRoot = form;
            Xfa = xfa;
        }

        public List<ScriptNode> Nodes { get; } = new();
        public ScriptNode FormRoot { get; }
        public ScriptNode Xfa { get; }

        private ScriptNode Wrap(XfaFormNode node, ScriptNode? parent, List<ScriptNode> all)
        {
            var wrapper = new ScriptNode(this, node, node.Element.AttrOr("name", ""), ClassOf(node.Kind), parent);
            all.Add(wrapper);
            foreach (var child in node.Children)
                wrapper.AddChild(Wrap(child, wrapper, all));
            return wrapper;
        }

        private static string ClassOf(XfaNodeKind kind) => kind switch
        {
            XfaNodeKind.Subform => "subform",
            XfaNodeKind.Area => "area",
            XfaNodeKind.ExclGroup => "exclGroup",
            XfaNodeKind.Field => "field",
            _ => "draw",
        };

        public void BeginScript() { _undo.Clear(); _changed = false; }

        public bool EndScript() => _changed;

        public void AbortScript()
        {
            for (var i = _undo.Count - 1; i >= 0; i--)
            {
                var (node, value, rich, presence) = _undo[i];
                node.Form!.Value = value;
                node.Form.RichValue = rich;
                node.Form.PresenceOverride = presence;
            }
            _undo.Clear();
            _changed = false;
        }

        public bool Write(ScriptNode node, string? value)
        {
            var form = node.Form;
            if (form is not { Kind: XfaNodeKind.Field }) return false;
            if (string.Equals(form.Value, value, StringComparison.Ordinal) && form.RichValue == null) return true;
            _undo.Add((node, form.Value, form.RichValue, form.PresenceOverride));
            form.Value = value;
            form.RichValue = null;
            _changed = true;
            _report.ScriptWrote(node.Name);
            return true;
        }

        public bool WritePresence(ScriptNode node, string presence)
        {
            var form = node.Form;
            if (form == null || presence is not ("visible" or "hidden" or "invisible" or "inactive")) return false;
            if (form.Presence == presence) return true;
            _undo.Add((node, form.Value, form.RichValue, form.PresenceOverride));
            form.PresenceOverride = presence;
            _changed = true;
            return true;
        }
    }

    private sealed class ScriptNode : IFcObject
    {
        private readonly ScriptModel _model;
        private readonly List<IFcObject> _children = new();

        public ScriptNode(ScriptModel model, XfaFormNode? form, string name, string className, ScriptNode? parent)
        {
            _model = model;
            Form = form;
            Name = name;
            ClassName = className;
            Parent = parent;
        }

        public XfaFormNode? Form { get; }
        public string Name { get; }
        public string ClassName { get; }
        public IFcObject? Parent { get; private set; }
        public IReadOnlyList<IFcObject> Children => _children;

        public void AddChild(ScriptNode child) => _children.Add(child);
        public void SetParent(ScriptNode parent) => Parent = parent;

        public bool TryGetProperty(string name, out object? value)
        {
            value = null;
            switch (name)
            {
                case "rawValue":
                    if (Form is not { Kind: XfaNodeKind.Field or XfaNodeKind.Draw }) return false;
                    value = Form.Value;
                    return true;
                case "presence":
                    if (Form == null) return false;
                    value = Form.Presence;
                    return true;
                case "name": value = Name; return true;
                case "className": value = ClassName; return true;
                default: return false;
            }
        }

        public bool TrySetProperty(string name, object? value) => name switch
        {
            "rawValue" => _model.Write(this, value switch
            {
                null => null,
                double d => FormCalcValue.NumberToText(d),
                string s => s,
                _ => null,
            }),
            "presence" => _model.WritePresence(this, FormCalcValue.ToText(value)),
            _ => false,
        };
    }
}
