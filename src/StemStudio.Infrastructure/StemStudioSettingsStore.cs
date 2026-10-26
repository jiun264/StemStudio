using System.Text.Json;
using StemStudio.Core;

namespace StemStudio.Infrastructure;

public sealed class StemStudioSettingsStore : IStemStudioSettingsStore
{
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StemStudio",
        "settings.json");

    public StemStudioSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return StemStudioSettings.CreateDefault();
        }

        var json = File.ReadAllText(_settingsPath);
        var settings = JsonSerializer.Deserialize<StemStudioSettings>(json);
        if (settings is null ||
            string.IsNullOrWhiteSpace(settings.OutputDirectory) ||
            string.IsNullOrWhiteSpace(settings.ModelCacheDirectory))
        {
            throw new InvalidDataException($"設定檔格式無效：{_settingsPath}");
        }

        return settings;
    }

    public void Save(StemStudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.OutputDirectory) ||
            string.IsNullOrWhiteSpace(settings.ModelCacheDirectory))
        {
            throw new ArgumentException("輸出資料夾與模型資料夾不可為空白。", nameof(settings));
        }

        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("無法取得設定資料夾。");
        Directory.CreateDirectory(directory);
        var temporaryPath = _settingsPath + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, _settingsPath, overwrite: true);
    }
}
