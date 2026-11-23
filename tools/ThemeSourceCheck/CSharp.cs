using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static partial class Program
{
    private static void CheckCSharp(IEnumerable<string> paths)
    {
        var trees = paths.Select(p => CSharpSyntaxTree.ParseText(File.ReadAllText(p), path: p)).ToArray();
        // Resolve aliases and static imports to their declaring framework types using the installed desktop runtime.
        var assemblies = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        // Gui.csproj enables the Windows Forms SDK's implicit System.Drawing import.
        var drawingImport = CSharpSyntaxTree.ParseText("global using System.Drawing;");
        var compilation = CSharpCompilation.Create("GuiThemeSources", trees.Append(drawingImport),
            assemblies.Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p)));
        foreach (var tree in trees) {
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes()) {
                if (node is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)) {
                    var key = literal.Token.ValueText;
                    if (PrimitiveKeys.Contains(key)) Report(literal, $"private palette key {key}");
                    else if (!LightKeys.Contains(key) && (IsThemeKey(key) || IsColorLookup(literal, model)))
                        Report(literal, $"missing key {key}");
                }
                if (node is not IdentifierNameSyntax id) continue;
                var name = id.Identifier.ValueText;
                var construct = name is "FromRgb" or "FromArgb" or "FromScRgb" or "ConvertFromString";
                if (!construct && (!NamedColors.Contains(name) || name == "Transparent")) continue;
                var info = model.GetSymbolInfo(id);
                var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                var type = symbol?.ContainingType?.ToDisplayString();
                var expression = id.Parent is MemberAccessExpressionSyntax member && member.Name == id
                    ? (ExpressionSyntax)member : id;
                if (construct && expression.Parent is InvocationExpressionSyntax call
                    && call.Expression == expression) {
                    if (type is "System.Windows.Media.Color" or "System.Drawing.Color"
                        or "System.Windows.Media.ColorConverter" or "System.Drawing.ColorConverter") {
                        if (!ChangesOnlyAlpha(call, name, type))
                            Report(call, call.WithoutTrivia().NormalizeWhitespace(indentation: "", eol: " ")
                                .ToFullString());
                    }
                } else if (symbol is IPropertySymbol && type is ("System.Windows.Media.Colors"
                    or "System.Windows.Media.Brushes" or "System.Drawing.Brushes" or "System.Drawing.Color")) {
                    Report(expression, expression.ToString());
                }
            }

            void Report(SyntaxNode node, string violation)
            {
                var declaration = node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
                var site = declaration switch {
                    MethodDeclarationSyntax m => m.Identifier.ValueText,
                    PropertyDeclarationSyntax p => p.Identifier.ValueText,
                    FieldDeclarationSyntax f => f.Declaration.Variables.First().Identifier.ValueText,
                    _ => declaration?.Kind().ToString() ?? "global"
                };
                var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                Findings.Add((tree.FilePath, line, site, violation));
            }
        }
    }

    private static bool IsColorLookup(LiteralExpressionSyntax literal, SemanticModel model)
    {
        if (literal.Parent is not ArgumentSyntax { Parent: ArgumentListSyntax args }
            || args.Parent is not InvocationExpressionSyntax call) return false;
        var method = call.Expression.GetLastToken().ValueText;
        if (method is "FindResource" or "TryFindResource") {
            var target = call.Parent switch {
                CastExpressionSyntax cast => cast.Type,
                BinaryExpressionSyntax check when check.IsKind(SyntaxKind.AsExpression) => check.Right,
                IsPatternExpressionSyntax { Pattern: DeclarationPatternSyntax pattern } => pattern.Type,
                _ => null
            };
            return target != null && model.GetTypeInfo(target).Type is { } type && IsColorProperty(type.Name);
        }
        if (method != "SetResourceReference" || args.Arguments.Count != 2 || args.Arguments[1].Expression != literal)
            return false;
        var property = args.Arguments[0].Expression.GetLastToken().ValueText;
        return property.EndsWith("Property") && IsColorProperty(property[..^"Property".Length]);
    }

    private static bool ChangesOnlyAlpha(InvocationExpressionSyntax call, string method, string type)
    {
        var args = call.ArgumentList.Arguments;
        if (method is not ("FromArgb" or "FromScRgb")) return false;
        if (type == "System.Drawing.Color" && args.Count == 2) return true; // FromArgb(alpha, baseColor)
        if (args.Count != 4) return false;
        var channels = args.Skip(1).Select(a => a.Expression).OfType<MemberAccessExpressionSyntax>().ToArray();
        string[] rgb = method == "FromScRgb" ? ["ScR", "ScG", "ScB"] : ["R", "G", "B"];
        return channels.Select(c => c.Name.Identifier.ValueText).SequenceEqual(rgb)
            && channels.Skip(1).All(c => c.Expression.IsEquivalentTo(channels[0].Expression));
    }
}
