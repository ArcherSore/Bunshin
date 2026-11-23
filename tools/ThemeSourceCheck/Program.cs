using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;

internal static partial class Program
{
    private const string Light = "src/Gui/Themes/Colors/Light.xaml";
    private const string Allowlist = "tools/ThemeSourceCheck/allowlist.tsv";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly HashSet<string> NamedColors = typeof(Colors).GetProperties()
        .Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LightKeys = [];
    private static readonly HashSet<string> ColorKeys = [];
    private static readonly List<(string Path, int Line, string Site, string Violation)> Findings = [];

    private static int Main()
    {
        var git = new ProcessStartInfo("git") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in new[] { "ls-files", "-z", "--cached", "--others", "--exclude-standard", "--",
                                    "src/Gui/*.xaml", "src/Gui/*.cs" }) git.ArgumentList.Add(arg);
        using var process = Process.Start(git)!;
        var paths = process.StandardOutput.ReadToEnd().Split('\0', StringSplitOptions.RemoveEmptyEntries);
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("Could not enumerate GUI sources.");

        var documents = paths.Where(p => p.EndsWith(".xaml") && File.Exists(p))
            .ToDictionary(p => p, p => XDocument.Load(p, LoadOptions.SetLineInfo));
        foreach (var element in documents[Light].Root!.Elements())
            if ((string?)element.Attribute(Xaml + "Key") is { } key) LightKeys.Add(key);
        foreach (var document in documents.Values)
            foreach (var element in document.Descendants())
                if (IsColorProperty(element.Name.LocalName) && (string?)element.Attribute(Xaml + "Key") is { } key)
                    ColorKeys.Add(key);
        foreach (var (path, document) in documents) CheckXaml(path, document);
        CheckCSharp(paths.Where(p => p.EndsWith(".cs") && File.Exists(p)));

        // Consume one exception per occurrence. Removing an occurrence also requires removing its exception.
        var remaining = new Dictionary<string, int>();
        foreach (var row in File.ReadLines(Allowlist)) {
            if (row.StartsWith('#') || string.IsNullOrWhiteSpace(row)) continue;
            var columns = row.Split('\t');
            if (columns.Length != 4 || columns.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException($"Expected path, site, violation and reason in {Allowlist}: {row}");
            var id = string.Join('\t', columns[..3]);
            remaining[id] = remaining.GetValueOrDefault(id) + 1;
        }
        var failed = false;
        foreach (var (path, line, site, violation) in Findings) {
            var id = $"{path}\t{site}\t{violation}";
            if (remaining.GetValueOrDefault(id) > 0) { remaining[id]--; continue; }
            Console.WriteLine($"FAIL {path}:{line}\t{site}\t{violation}");
            failed = true;
        }
        foreach (var (id, count) in remaining.Where(pair => pair.Value > 0)) {
            Console.WriteLine($"FAIL {Allowlist}: remove {count} obsolete exception(s): {id}");
            failed = true;
        }
        if (!failed) Console.WriteLine($"PASS theme sources ({Findings.Count} documented exceptions)");
        return failed ? 1 : 0;
    }

    private static bool IsColorProperty(string name) => ColorProperty().IsMatch(name);

    [GeneratedRegex(@"(?:Brush|Color|Background|Foreground|Stroke|Fill|Effect|OpacityMask)$")]
    private static partial Regex ColorProperty();

    private static bool IsThemeKey(string key) => LightKeys.Contains(key) || ColorKeys.Contains(key)
        || key.StartsWith("Brush.", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("Color.", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("Effect.", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\{\s*(StaticResource|DynamicResource)\s+(?:ResourceKey\s*=\s*)?([^\s,{}]+)\s*\}")]
    private static partial Regex ResourceReference();

    [GeneratedRegex(@"(?:^|(?<=[=,\s]))#(?:[\da-f]{3,4}|[\da-f]{6}|[\da-f]{8})(?=$|[,}\s])", RegexOptions.IgnoreCase)]
    private static partial Regex Hex();

    [GeneratedRegex(@"\{x:Static\s+(?:\w+:)?(?:Brushes|Colors)\.(\w+)\s*\}")]
    private static partial Regex NamedMember();

    private static void CheckXaml(string path, XDocument document)
    {
        // Only dictionaries of palette resources may use literals and local static references.
        // A style dictionary under Themes (including DesignSystem) is still a consumer.
        var root = document.Root!;
        var palette = Path.GetDirectoryName(path) == Path.GetDirectoryName(Light)
            && root.Name.LocalName == "ResourceDictionary" && root.Elements().All(e => IsColorProperty(e.Name.LocalName));
        var localKeys = root.Elements().Attributes(Xaml + "Key").Select(a => a.Value).ToHashSet();
        foreach (var element in document.Descendants()) {
            var type = element.Name.LocalName;
            var owner = element.AncestorsAndSelf().Select(e =>
                (string?)e.Attribute(Xaml + "Key") ?? (string?)e.Attribute(Xaml + "Name")).FirstOrDefault(n => n != null);
            foreach (var attr in element.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.Namespace != Xaml))
                CheckValue(attr, attr.Name.LocalName, attr.Value.Trim());
            foreach (var node in element.Nodes().OfType<XText>().Where(t => !string.IsNullOrWhiteSpace(t.Value)))
                CheckValue(node, type, node.Value.Trim());

            void CheckValue(XObject source, string name, string value)
            {
                var property = name switch {
                    "Value" => (string?)element.Attribute("Property") ?? name,
                    "Setter.Value" => (string?)element.Parent?.Attribute("Property") ?? name,
                    _ => name
                };
                var color = IsColorProperty(property) || (type == "ColorAnimation" && name is "From" or "To" or "By")
                    || (type.EndsWith("ColorKeyFrame") && name == "Value");
                var site = $"{owner ?? type}/{property}";
                void Report(string violation) => Findings.Add((path, ((IXmlLineInfo)source).LineNumber, site, violation));
                if (!palette) {
                    if (color && NamedColors.Contains(value)) Report($"literal {value}");
                    foreach (Match hex in Hex().Matches(value)) Report($"literal {hex.Value}");
                    foreach (Match member in NamedMember().Matches(value)) Report($"literal {member.Value}");
                }

                void Reference(string kind, string key, bool colorTarget)
                {
                    if (!colorTarget && !IsThemeKey(key) && !palette) return;
                    if (kind == "StaticResource" && !palette) Report($"{kind} {key}");
                    if (!LightKeys.Contains(key)) Report($"missing {kind} {key}");
                    else if (palette && kind == "StaticResource" && !localKeys.Contains(key)) Report($"nonlocal {kind} {key}");
                }
                foreach (Match reference in ResourceReference().Matches(value))
                    Reference(reference.Groups[1].Value, reference.Groups[2].Value, color && reference.Value == value);
                if (name == "ResourceKey" && type is "StaticResource" or "DynamicResource")
                    Reference(type, value, element.Parent is { } parent && IsColorProperty(parent.Name.LocalName));
            }
        }
    }
}
