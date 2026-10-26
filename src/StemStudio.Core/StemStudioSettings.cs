namespace StemStudio.Core;

public sealed record StemStudioSettings(string OutputDirectory, string ModelCacheDirectory)
{
    public static StemStudioSettings CreateDefault()
    {
        var musicDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        var localDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new StemStudioSettings(
            Path.Combine(musicDirectory, "StemStudio"),
            Path.Combine(localDataDirectory, "StemStudio", "models"));
    }
}
