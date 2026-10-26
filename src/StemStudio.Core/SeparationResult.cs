namespace StemStudio.Core;

public sealed record SeparationResult(
    string OutputDirectory,
    IReadOnlyList<string> OutputFiles);
