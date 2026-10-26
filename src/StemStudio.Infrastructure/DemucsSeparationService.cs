using System.Diagnostics;
using System.Text;
using StemStudio.Core;

namespace StemStudio.Infrastructure;

public sealed class DemucsSeparationService(StemStudioSettings settings) : ISeparationService, IModelSetupService
{
    private static readonly IReadOnlyDictionary<SeparationStem, string> StemFileNames =
        new Dictionary<SeparationStem, string>
        {
            [SeparationStem.Vocals] = "vocals.wav",
            [SeparationStem.Guitar] = "guitar.wav",
            [SeparationStem.Bass] = "bass.wav",
            [SeparationStem.Drums] = "drums.wav"
        };

    public async Task<bool> IsModelReadyAsync(CancellationToken cancellationToken)
    {
        var output = await RunWorkerAsync(["model-status"], null, cancellationToken);
        return output.Any(line => line.Equals("STATUS|MODEL_READY|true", StringComparison.OrdinalIgnoreCase));
    }

    public async Task PrepareModelAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await RunWorkerAsync(["prepare-model"], progress, cancellationToken);
    }

    public async Task<SeparationResult> SeparateAsync(
        SeparationRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!File.Exists(request.InputPath))
        {
            throw new FileNotFoundException("找不到要分離的音訊檔。", request.InputPath);
        }

        if (request.Stems.Count == 0)
        {
            throw new ArgumentException("請至少選擇一種要輸出的音軌。", nameof(request));
        }

        var selectedStems = request.Stems.Distinct().ToArray();
        if (selectedStems.Any(stem => !StemFileNames.ContainsKey(stem)))
        {
            throw new ArgumentException("要求中包含不支援的音軌類型。", nameof(request));
        }

        var trackName = Path.GetFileNameWithoutExtension(request.InputPath);
        var outputDirectory = CreateOutputDirectory(trackName);
        var arguments = new List<string>
        {
            "separate",
            "--input", request.InputPath,
            "--output", outputDirectory,
            "--stems", string.Join(",", selectedStems.Select(stem => StemFileNames[stem][..^4]))
        };
        try
        {
            await RunWorkerAsync(arguments, progress, cancellationToken);
        }
        catch
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }

            throw;
        }

        var outputFiles = selectedStems
            .Select(stem => Path.Combine(outputDirectory, StemFileNames[stem]))
            .ToList();
        var missingFile = outputFiles.FirstOrDefault(path => !File.Exists(path));
        if (missingFile is not null)
        {
            throw new FileNotFoundException($"Worker 沒有產生預期的音軌：{Path.GetFileName(missingFile)}", missingFile);
        }

        return new SeparationResult(outputDirectory, outputFiles);
    }

    private async Task<IReadOnlyList<string>> RunWorkerAsync(
        IReadOnlyList<string> arguments,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var appDirectory = AppContext.BaseDirectory;
        var workerExecutable = Path.Combine(appDirectory, "StemStudio.Worker.exe");
        var workerDll = Path.Combine(appDirectory, "StemStudio.Worker.dll");
        var pythonExecutable = Path.Combine(appDirectory, "python", "python.exe");
#if DEBUG
        var developmentPython = Environment.GetEnvironmentVariable("STEMSTUDIO_PYTHON");
        if (!string.IsNullOrWhiteSpace(developmentPython))
        {
            pythonExecutable = developmentPython;
        }
#endif
        var scriptPath = Path.Combine(appDirectory, "backend", "demucs_worker.py");
        var modelCacheDirectory = settings.ModelCacheDirectory;

        if (!File.Exists(pythonExecutable))
        {
            throw new FileNotFoundException(
                "找不到 StemStudio 隨附的 Python 執行環境。請重新執行安裝程式修復安裝。",
                pythonExecutable);
        }

        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException(
                "找不到 StemStudio Python worker。請重新執行安裝程式修復安裝。",
                scriptPath);
        }

        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (File.Exists(workerExecutable))
        {
            startInfo.FileName = workerExecutable;
        }
        else if (File.Exists(workerDll))
        {
            startInfo.FileName = "dotnet";
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("--runtimeconfig");
            startInfo.ArgumentList.Add(Path.Combine(appDirectory, "StemStudio.App.runtimeconfig.json"));
            startInfo.ArgumentList.Add(workerDll);
        }
        else
        {
            throw new FileNotFoundException(
                "找不到 StemStudio worker。請重新執行安裝程式修復安裝。",
                workerExecutable);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("--python");
        startInfo.ArgumentList.Add(pythonExecutable);
        startInfo.ArgumentList.Add("--script");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("--model-cache");
        startInfo.ArgumentList.Add(modelCacheDirectory);

        using var process = new Process { StartInfo = startInfo };
        var output = new List<string>();
        var errors = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                return;
            }

            lock (output)
            {
                output.Add(eventArgs.Data);
                if (output.Count > 200)
                {
                    output.RemoveAt(0);
                }
            }

            if (eventArgs.Data.StartsWith("STATUS|", StringComparison.Ordinal))
            {
                progress?.Report(eventArgs.Data["STATUS|".Length..]);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                return;
            }

            lock (errors)
            {
                errors.AppendLine(eventArgs.Data);
                if (errors.Length > 12_000)
                {
                    errors.Remove(0, errors.Length - 12_000);
                }
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("無法啟動 StemStudio worker。");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            throw;
        }

        if (process.ExitCode != 0)
        {
            string details;
            lock (output)
            {
                details = string.Join(
                    Environment.NewLine,
                    output.Select(line => line.StartsWith("STATUS|", StringComparison.Ordinal)
                        ? line["STATUS|".Length..]
                        : line));
            }

            throw new InvalidOperationException(
                $"StemStudio worker 失敗（結束碼 {process.ExitCode}）。{Environment.NewLine}{details}{Environment.NewLine}{errors}");
        }

        return output;
    }

    private string CreateOutputDirectory(string trackName)
    {
        Directory.CreateDirectory(settings.OutputDirectory);

        var safeTrackName = string.Join(
            "_",
            trackName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(safeTrackName))
        {
            safeTrackName = "Audio";
        }

        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var outputDirectory = Path.Combine(
            settings.OutputDirectory,
            $"{safeTrackName}_{DateTime.Now:yyyyMMdd_HHmmss}_{uniqueSuffix}");
        Directory.CreateDirectory(outputDirectory);
        return outputDirectory;
    }
}
