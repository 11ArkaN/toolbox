using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Toolbox.Models;

namespace Toolbox.Services;

public sealed class AudioConversionService
{
    public IReadOnlyList<string> SupportedExtensions { get; } =
    [
        ".mp3",
        ".wav",
        ".flac",
        ".aac",
        ".ogg",
        ".m4a",
        ".wma",
        ".aiff",
        ".opus"
    ];

    public async Task<IReadOnlyList<string>> ConvertToMp3Async(
        IReadOnlyList<string> inputPaths,
        string outputDirectory,
        AudioConversionOptions options,
        IProgress<ToolProgress> progress,
        CancellationToken cancellationToken = default)
    {
        if (inputPaths.Count == 0)
        {
            throw new InvalidOperationException("Select at least one audio file.");
        }

        Directory.CreateDirectory(outputDirectory);
        progress.Report(new ToolProgress(1, "Checking ffmpeg..."));
        await EnsureToolAvailableAsync("ffmpeg", cancellationToken);
        await EnsureToolAvailableAsync("ffprobe", cancellationToken);

        var outputs = new List<string>();
        for (int index = 0; index < inputPaths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string inputPath = inputPaths[index];
            if (!File.Exists(inputPath))
            {
                throw new FileNotFoundException("Audio file does not exist.", inputPath);
            }

            string outputPath = PreviewOutputPath(inputPath, outputDirectory, options);
            double duration = await GetDurationOrDefaultAsync(inputPath, cancellationToken);
            double basePercent = index * 100.0 / inputPaths.Count;
            double spanPercent = 100.0 / inputPaths.Count;

            progress.Report(new ToolProgress(
                (int)Math.Round(basePercent),
                $"Converting {Path.GetFileName(inputPath)}..."));

            await RunFfmpegConversionAsync(
                inputPath,
                outputPath,
                duration,
                options,
                basePercent,
                spanPercent,
                progress,
                cancellationToken);

            outputs.Add(outputPath);
        }

        progress.Report(new ToolProgress(100, "MP3 conversion completed."));
        return outputs;
    }

    public string PreviewOutputPath(string inputPath, string outputDirectory, AudioConversionOptions options)
    {
        string baseName = Path.GetFileNameWithoutExtension(inputPath);
        string suffix = string.IsNullOrWhiteSpace(options.Suffix) ? "_mp3" : options.Suffix.Trim();
        return UniquePath(Path.Combine(outputDirectory, baseName + suffix + ".mp3"));
    }

    private static async Task EnsureToolAvailableAsync(string toolName, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunProcessAsync(toolName, ["-version"], progress: null, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{toolName} is not installed or is not available in PATH.");
        }
    }

    private static async Task<double> GetDurationOrDefaultAsync(string inputPath, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunProcessAsync(
            "ffprobe",
            [
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                inputPath
            ],
            progress: null,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            return 0;
        }

        string rawDuration = result.Output.Trim();
        return double.TryParse(rawDuration, NumberStyles.Float, CultureInfo.InvariantCulture, out double duration)
            ? Math.Max(0, duration)
            : 0;
    }

    private static async Task RunFfmpegConversionAsync(
        string inputPath,
        string outputPath,
        double duration,
        AudioConversionOptions options,
        double basePercent,
        double spanPercent,
        IProgress<ToolProgress> progress,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-y",
            "-i",
            inputPath,
            "-vn",
            "-codec:a",
            "libmp3lame",
            "-b:a",
            $"{Math.Clamp(options.BitrateKbps, 64, 320)}k"
        };

        if (options.SampleRate > 0)
        {
            arguments.Add("-ar");
            arguments.Add(options.SampleRate.ToString(CultureInfo.InvariantCulture));
        }

        if (options.ChannelMode != AudioChannelMode.Original)
        {
            arguments.Add("-ac");
            arguments.Add(options.ChannelMode == AudioChannelMode.Mono ? "1" : "2");
        }

        arguments.AddRange([
            "-progress", "pipe:1",
            "-nostats",
            outputPath
        ]);

        ProcessResult result = await RunProcessAsync(
            "ffmpeg",
            arguments,
            line =>
            {
                const string outTimePrefix = "out_time_ms=";
                if (duration <= 0 || !line.StartsWith(outTimePrefix, StringComparison.Ordinal))
                {
                    return;
                }

                string raw = line[outTimePrefix.Length..].Trim();
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double microseconds))
                {
                    return;
                }

                double seconds = Math.Max(0, microseconds / 1_000_000.0);
                double ratio = Math.Min(1.0, seconds / duration);
                int percent = Math.Clamp((int)Math.Round(basePercent + ratio * spanPercent), 0, 100);
                progress.Report(new ToolProgress(percent, $"Converting {Path.GetFileName(inputPath)}..."));
            },
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(BuildProcessError($"ffmpeg failed while converting {Path.GetFileName(inputPath)}.", result.Error));
        }
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyCollection<string> arguments,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException($"{fileName} is not installed or is not available in PATH.", ex);
        }

        var outputLines = new List<string>();
        Task outputTask = ReadLinesAsync(process.StandardOutput, outputLines, progress, cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        await outputTask;
        string error = await errorTask;

        return new ProcessResult(process.ExitCode, string.Join(Environment.NewLine, outputLines), error);
    }

    private static async Task ReadLinesAsync(
        StreamReader reader,
        List<string> lines,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            lines.Add(line);
            progress?.Invoke(line);
        }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        string directory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
        string baseName = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        int counter = 2;
        string candidate;
        do
        {
            candidate = Path.Combine(directory, $"{baseName}_{counter}{extension}");
            counter++;
        }
        while (File.Exists(candidate));

        return candidate;
    }

    private static string BuildProcessError(string message, string stderr)
    {
        string details = string.IsNullOrWhiteSpace(stderr) ? "No diagnostic output was returned." : stderr.Trim();
        return $"{message}{Environment.NewLine}{details}";
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
