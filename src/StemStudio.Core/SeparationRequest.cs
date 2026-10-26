namespace StemStudio.Core;

public sealed record SeparationRequest(
    string InputPath,
    IReadOnlyCollection<SeparationStem> Stems);
