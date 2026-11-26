using System.Xml.Linq;
using ChartProject = DotCharter.Core.Domain.Project.ChartProject;
using IgnoredFinding = DotCharter.Core.Domain.Validation.IgnoredFinding;

namespace DotCharter.Services.Parsing.Rscp;

/// <summary>
///     The <c>.dcproj</c> spelling of <see cref="ChartProject.IgnoredFindings" />: a root
///     <c>&lt;dcIgnoredFindings&gt;</c> holding one <c>&lt;dcIgnoredFinding code message subject /&gt;</c> per
///     ignored warning, absent when nothing is ignored so every other chart stays byte-identical. A child missing
///     any of the three values is dropped on read: it could never match a finding.
/// </summary>
internal static class RscpIgnoredFindings
{
    public const string RootElement = "dcIgnoredFindings";
    public const string ItemElement = "dcIgnoredFinding";

    public static void Write(XElement root, ChartProject project)
    {
        if (project.IgnoredFindings.Count == 0) return;
        var node = new XElement(RootElement);
        foreach (var finding in project.IgnoredFindings)
        {
            node.Add(new XElement(ItemElement,
                new XAttribute("code", finding.Code),
                new XAttribute("message", finding.Message),
                new XAttribute("subject", finding.Subject)));
        }

        root.Add(node);
    }

    public static void Parse(XElement? node, ChartProject project)
    {
        if (node == null) return;
        foreach (var item in node.Elements(ItemElement))
        {
            var code = item.Attribute("code")?.Value;
            var message = item.Attribute("message")?.Value;
            var subject = item.Attribute("subject")?.Value;
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(message) || string.IsNullOrEmpty(subject)) continue;
            var finding = new IgnoredFinding(code, message, subject);
            if (!project.IgnoredFindings.Contains(finding)) project.IgnoredFindings.Add(finding);
        }
    }
}
