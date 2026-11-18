using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ProjectModel;

internal static class ProjectInputValue
{
    // An untyped value is only verified and stays a string; pretask programs receive inputs as raw text. A default that
    // fails verify marks a field the user must still fill in: it stays raw text, and the caller blocks Start instead.
    internal static JsonNode Parse(InputDefinition input, string value, string context, bool typed,
        bool isDefault = false)
    {
        var result = TryParse(input, value, context, typed, isDefault, out var problem);
        return result ?? throw new InvalidDataException($"{context} {problem}");
    }

    internal static string? CheckValue(InputDefinition input, string value, bool typed)
    {
        try {
            _ = TryParse(input, value, input.Label, typed, isDefault: false, out var problem);
            return problem;
        } catch (InvalidDataException) {
            // A regex validated on load can still time out on particular text.
            return "校验未能完成，请缩短输入后重试。";
        }
    }

    private static JsonNode? TryParse(InputDefinition input, string value, string context, bool typed,
        bool isDefault, out string? problem)
    {
        problem = null;
        if (!Matches(input, value, context)) {
            if (isDefault) {
                return JsonValue.Create(value);
            }
            problem = input.PatternMessage ?? "输入格式不符合要求，请修改后重试。";
            return null;
        }
        if (!typed || input.PipelineKind == PipelineValueKind.String) {
            return JsonValue.Create(value);
        }
        if (input.PipelineKind == PipelineValueKind.Int) {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) {
                return JsonValue.Create(number);
            }
            problem = "不是合法 int，请输入整数。";
        } else if (input.PipelineKind == PipelineValueKind.Bool) {
            if (bool.TryParse(value, out var boolean)) {
                return JsonValue.Create(boolean);
            }
            problem = "请输入 true 或 false。";
        } else {
            throw new InvalidOperationException($"Loader 产生了不支持的 pipeline kind：{input.PipelineKind}。");
        }
        return null;
    }

    internal static bool Matches(InputDefinition input, string value, string context)
    {
        if (input.Verify is null) {
            return true;
        }

        const string defaultSuffix = ".default";
        var verifyContext = context.EndsWith(defaultSuffix, StringComparison.Ordinal)
            ? $"{context[..^defaultSuffix.Length]}.verify"
            : $"{context}的 verify";
        Regex regex;
        try {
            regex = new Regex(input.Verify, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        } catch (ArgumentException exception) {
            throw new InvalidDataException(
                $"{verifyContext} 不是合法正则表达式。",
                exception);
        }

        try {
            return regex.IsMatch(value);
        } catch (RegexMatchTimeoutException exception) {
            throw new InvalidDataException(
                $"{verifyContext} 执行超时。",
                exception);
        }
    }
}
