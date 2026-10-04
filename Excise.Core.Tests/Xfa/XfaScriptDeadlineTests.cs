using System.Collections;
using System.Reflection;
using System.Xml.Linq;
using AwesomeAssertions;
using Excise.Core.Xfa;
using Xunit;

namespace Excise.Core.Tests.Xfa;

public class XfaScriptDeadlineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterruptedScript_RollsBackValueRichTextAndPresence(bool cancellation)
    {
        var root = new XfaFormNode(XfaNodeKind.Subform, new XElement("subform", new XAttribute("name", "form1")));
        var rich = new XElement("body", "original rich text");
        var field = new XfaFormNode(XfaNodeKind.Field, new XElement("field", new XAttribute("name", "target")))
        {
            Value = "original", RichValue = rich, PresenceOverride = "visible"
        };
        root.Children.Add(field);
        var report = new XfaReport();
        // Exercise the transaction boundary with an exactly timed interruption,
        // without exporting a test-only model or relying on thread scheduling (#1923).
        var modelType = typeof(XfaScripts).GetNestedType("ScriptModel", BindingFlags.NonPublic)!;
        var model = Activator.CreateInstance(modelType, root, report)!;
        var nodes = (IList)modelType.GetProperty("Nodes")!.GetValue(model)!;
        Action deadline = () =>
        {
            if (field.Value != "changed" || field.PresenceOverride != "hidden") return;
            if (cancellation) throw new OperationCanceledException();
            throw new XfaLayoutException("layout deadline");
        };
        var method = typeof(XfaScripts).GetMethod("RunOne", BindingFlags.Static | BindingFlags.NonPublic)!;
        object?[] arguments = [model, nodes[1],
            "$.rawValue = \"changed\"\n $.presence = \"hidden\"\n At(\"x\", \"x\")",
            "initialize", CancellationToken.None, report, deadline, false, false];
        var act = () => method.Invoke(null, arguments);
        var error = act.Should().Throw<TargetInvocationException>().Which.InnerException;
        if (cancellation) error.Should().BeOfType<OperationCanceledException>();
        else error.Should().BeOfType<XfaLayoutException>();
        report.ScriptWrites.Should().Contain("target", "the interruption must occur after a real write");
        field.Value.Should().Be("original");
        field.RichValue.Should().BeSameAs(rich);
        field.PresenceOverride.Should().Be("visible");
        report.ScriptsRun.Should().BeEmpty();
    }

    [Fact]
    public void ShortScript_ObservesWholeLayoutDeadline()
    {
        var root = new XfaFormNode(XfaNodeKind.Subform, new XElement("subform", new XAttribute("name", "form1")));
        var field = new XfaFormNode(XfaNodeKind.Field, new XElement("field", new XAttribute("name", "target"),
            new XElement("event", new XAttribute("activity", "initialize"), new XElement("script", "$.rawValue = 123"))))
        { Value = "original" };
        root.Children.Add(field);
        var budget = new XfaBudget(TimeSpan.FromTicks(-1), CancellationToken.None);
        var act = () => XfaScripts.Run(root, budget, new XfaReport(), CancellationToken.None);
        act.Should().Throw<XfaLayoutException>().WithMessage("*time limit*");
        field.Value.Should().Be("original");
    }
}
