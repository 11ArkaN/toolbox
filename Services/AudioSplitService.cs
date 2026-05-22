using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Toolbox.Models;

namespace Toolbox.Services;

public sealed class AudioSplitService
{
    public async Task<AudioSplitResult> SplitInHalfAsync(
        string inputPath,
        IProgress<AudioSplitProgress> progress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            throw new ArgumentException("Audio file path is empty.", nameof(inputPath));
        }

        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Audio file does not exist.", inputPath);
        }

        progress.Report(new AudioSplitProgress(2, "Checking ffmpeg..."));
        await EnsureToolAvailableAsync("ffmpeg", cancellationToken);
        await EnsureToolAvailableAsync("ffprobe", cancellationToken);

        progress.Report(new AudioSplitProgress(8, "Reading duration..."));
        double duration = await GetDurationAsync(inputPath, cancellationToken);
        if (duration <= 0)
        {
            throw new InvalidOperationException("Could not determine a valid audio duration.");
        }

        double midpoint = duration / 2.0;
        string directory = Path.GetDirectoryName(inputPath) ?? Environment.CurrentDirectory;
        string name = Path.GetFileNameWithoutExtension(inputPath);
        string extension = Path.GetExtension(inputPath);
        string firstOutput = Path.Combine(directory, $"{name}_part1{extension}");
        string secondOutput = Path.Combine(directory, $"{name}_part2{extension}");

        progress.Report(new AudioSplitProgress(12, "Creating first part..."));
        await RunFfmpegSegmentAsync(
            inputPath,
            firstOutput,
            startSeconds: null,
            durationSeconds: midpoint,
            totalSegmentSeconds: midpoint,
            basePercent: 12,
            spanPercent: 40,
            progress,
            cancellationToken);

        progress.Report(new AudioSplitProgress(55, "Creating second part..."));
        await RunFfmpegSegmentAsync(
            inputPath,
            secondOutput,
            startSeconds: midpoint,
            durationSeconds: null,
            totalSegmentSeconds: midpoint,
            basePercent: 55,
            spanPercent: 40,
            progress,
            cancellationToken);

        progress.Report(new AudioSplitProgress(100, "Audio split completed."));
        return new AudioSplitResult(firstOutput, secondOutput);
    }

    private static async Task EnsureToolAvailableAsync(string toolName, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunProcessAsync(
            toolName,
            ["-version"],
            progress: null,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{toolName} is not installed or is not available in PATH.");
        }
    }

    private static async Task<double> GetDurationAsync(string inputPath, CancellationToken cancellationToken)
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
            throw new InvalidOperationException(BuildProcessError("ffprobe failed to read the file.", result.Error));
        }

        string rawDuration = result.Output.Trim();
        if (!double.TryParse(rawDuration, NumberStyles.Float, CultureInfo.InvariantCulture, out double duration))
        {
            throw new InvalidOperationException("ffprobe returned an invalid duration.");
        }

        return duration;
    }

    private static async Task RunFfmpegSegmentAsync(
        string inputPath,
        string outputPath,
        double? startSeconds,
        double? durationSeconds,
        double totalSegmentSeconds,
        double basePercent,
        double spanPercent,
        IProgress<AudioSplitProgress> progress,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-y"
        };

        if (startSeconds is not null)
        {
            arguments.Add("-ss");
            arguments.Add(startSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture));
        }

        arguments.Add("-i");
        arguments.Add(inputPath);

        if (durationSeconds is not null)
        {
            arguments.Add("-t");
            arguments.Add(durationSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture));
        }

        arguments.AddRange([
            "-c", "copy",
            "-avoid_negative_ts", "make_zero",
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
                if (!line.StartsWith(outTimePrefix, StringComparison.Ordinal))
                {
                    return;
                }

                string raw = line[outTimePrefix.Length..].Trim();
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double microseconds))
                {
                    return;
                }

                double seconds = Math.Max(0, microseconds / 1_000_000.0);
                double segmentRatio = totalSegmentSeconds <= 0 ? 0 : Math.Min(1.0, seconds / totalSegmentSeconds);
                double percent = basePercent + (segmentRatio * spanPercent);
                progress.Report(new AudioSplitProgress(percent, $"Processing {Path.GetFileName(outputPath)}..."));
            },
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(BuildProcessError($"ffmpeg failed while creating {Path.GetFileName(outputPath)}.", result.Error));
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

    private static string BuildProcessError(string message, string stderr)
    {
        string details = string.IsNullOrWhiteSpace(stderr) ? "No diagnostic output was returned." : stderr.Trim();
        return $"{message}{Environment.NewLine}{details}";
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
