namespace StemStudio.Core;

public interface ISeparationService
{
    Task<SeparationResult> SeparateAsync(
        SeparationRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}
