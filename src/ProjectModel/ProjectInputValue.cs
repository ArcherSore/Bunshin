using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ProjectModel;

internal static class ProjectInputValue
{
    // PI defaults may leave a required field unfilled. Editing and running both apply the complete check.
    internal static string? Parse(InputDefinition input, string value, out JsonNode? parsed,
        bool allowUnfilledDefault = false)
    {
        parsed = null;
        try {
            if (input.Verify is { } regex && !regex.IsMatch(value)) {
                return allowUnfilledDefault ? null : input.PatternMessage ?? "输入格式不符合要求，请修改后重试。";
            }
        } catch (RegexMatchTimeoutException) {
            return "校验未能完成，请缩短输入后重试。";
        }
        if (input.PipelineKind == PipelineValueKind.String) {
            parsed = JsonValue.Create(value);
        } else if (input.PipelineKind == PipelineValueKind.Int) {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) {
                return "不是合法 int，请输入整数。";
            }
            parsed = JsonValue.Create(number);
        } else {
            if (!bool.TryParse(value, out var boolean)) {
                return "请输入 true 或 false。";
            }
            parsed = JsonValue.Create(boolean);
        }
        return null;
    }
}
