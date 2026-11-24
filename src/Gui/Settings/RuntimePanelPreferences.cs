using System.Text.Json;
using Gui.Infrastructure;

namespace Gui.Settings;

internal enum RuntimePanelSection { Desktop, Schedule, Activity }

internal sealed record RuntimePanelSectionState(RuntimePanelSection Id, bool Collapsed);

// Stored independently of the Settings page, as are the panel's width and visibility.
internal sealed class RuntimePanelPreferences(string path, AppLogger logger)
{
    internal IReadOnlyList<RuntimePanelSectionState> Read()
    {
        IReadOnlyList<RuntimePanelSectionState> saved = [];
        try {
            if (File.Exists(path)) {
                saved = JsonSerializer.Deserialize<RuntimePanelSectionState[]>(File.ReadAllText(path)) ?? [];
            }
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) {
            logger.Warn("读取运行面板区块设置失败，已使用默认值。", exception);
        }

        // Keep valid saved entries in order. A new or missing section goes at the end with its default visibility.
        return saved.Where(state => state is not null && Enum.IsDefined(state.Id)).DistinctBy(state => state.Id)
            .Concat(Enum.GetValues<RuntimePanelSection>().Where(id => saved.All(state => state?.Id != id))
                .Select(id => new RuntimePanelSectionState(id, false))).ToArray();
    }

    internal bool TrySave(IReadOnlyList<RuntimePanelSectionState> layout)
    {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(layout));
            return true;
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            logger.Warn("保存运行面板区块设置失败。", exception);
            return false;
        }
    }
}
