using System.Diagnostics;
using System.Text;

namespace StemStudio.Worker;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            var options = ParseOptions(args);
            var python = RequiredPath(options, "python");
            var script = RequiredPath(options, "script");
            var modelCache = RequiredPath(options, "model-cache");
            var command = options.GetValueOrDefault("command")
                ?? throw new ArgumentException("缺少 worker 指令。");

            Directory.CreateDirectory(modelCache);
            var startInfo = new ProcessStartInfo
            {
                FileName = python,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(script);
            startInfo.ArgumentList.Add(command);
            startInfo.ArgumentList.Add("--model-cache");
            startInfo.ArgumentList.Add(modelCache);

            foreach (var (key, value) in options)
            {
                if (key is "command" or "python" or "script" or "model-cache")
                {
                    continue;
                }

                startInfo.ArgumentList.Add($"--{key}");
                startInfo.ArgumentList.Add(value);
            }

            startInfo.Environment["TORCH_HOME"] = modelCache;
            startInfo.Environment["HF_HOME"] = Path.Combine(modelCache, "huggingface");
            startInfo.Environment["HF_HUB_CACHE"] = Path.Combine(modelCache, "huggingface", "hub");
            startInfo.Environment["PYTHONUTF8"] = "1";
            using var process = new Process { StartInfo = startInfo };
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                {
                    Console.Out.WriteLine($"STATUS|{eventArgs.Data}");
                }
            };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                {
                    Console.Out.WriteLine($"STATUS|{eventArgs.Data}");
                }
            };

            if (!process.Start())
            {
                throw new InvalidOperationException("無法啟動隨附的 Python 執行環境。");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static Dictionary<string, string> ParseOptions(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            throw new ArgumentException("缺少 worker 指令。");
        }

        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["command"] = args[0]
        };
        for (var index = 1; index < args.Count; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Count)
            {
                throw new ArgumentException($"無效的 worker 參數：{argument}");
            }

            options[argument[2..]] = args[++index];
        }

        return options;
    }

    private static string RequiredPath(IReadOnlyDictionary<string, string> options, string name)
    {
        if (!options.TryGetValue(name, out var path) || !File.Exists(path) && name != "model-cache")
        {
            throw new FileNotFoundException($"worker 缺少必要檔案或路徑：{name}", path);
        }

        return path;
    }
}
