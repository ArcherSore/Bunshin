using System.Text.Json;
using Gui.Infrastructure;

namespace Gui.Settings;

// Saved layouts store these numbers, so a new section goes last here whatever its place on screen.
internal enum RuntimePanelSection { Desktop, Schedule, Activity, RunResult }

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

        // Keep valid saved entries in order. A new or missing section goes at the end with its default visibility,
        // except the run result, which goes right after the desktop: below the activity log it would be out of sight.
        var layout = saved.Where(state => state is not null && Enum.IsDefined(state.Id)).DistinctBy(state => state.Id)
            .ToList();
        foreach (var id in Enum.GetValues<RuntimePanelSection>().Where(id => layout.All(state => state.Id != id))) {
            var index = id == RuntimePanelSection.RunResult
                ? layout.FindIndex(state => state.Id == RuntimePanelSection.Desktop) + 1 : layout.Count;
            layout.Insert(index, new RuntimePanelSectionState(id, false));
        }
        return layout;
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
