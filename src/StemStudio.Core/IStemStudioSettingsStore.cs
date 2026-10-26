namespace StemStudio.Core;

public interface IStemStudioSettingsStore
{
    StemStudioSettings Load();

    void Save(StemStudioSettings settings);
}
