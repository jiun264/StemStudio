namespace StemStudio.Core;

public interface IModelSetupService
{
    Task<bool> IsModelReadyAsync(CancellationToken cancellationToken);

    Task PrepareModelAsync(IProgress<string>? progress, CancellationToken cancellationToken);
}
