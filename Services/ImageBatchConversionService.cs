using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Toolbox.Models;

namespace Toolbox.Services;

using ImageSharpRectangle = SixLabors.ImageSharp.Rectangle;
using ImageSharpSize = SixLabors.ImageSharp.Size;

public sealed class ImageBatchConversionService
{
    public IReadOnlyList<string> SupportedExtensions { get; } =
    [
        ".jpg",
        ".jpeg",
        ".png",
        ".webp",
        ".bmp",
        ".gif",
        ".tif",
        ".tiff"
    ];

    public Task<IReadOnlyList<string>> ConvertAsync(
        IReadOnlyList<string> imagePaths,
        string outputDirectory,
        ImageConversionOptions options,
        IProgress<ToolProgress> progress,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            if (imagePaths.Count == 0)
            {
                throw new InvalidOperationException("Select at least one image.");
            }

            Directory.CreateDirectory(outputDirectory);
            var outputs = new List<string>();

            for (int index = 0; index < imagePaths.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string inputPath = imagePaths[index];

                using Image<Rgba32> image = await Image.LoadAsync<Rgba32>(inputPath, cancellationToken);
                ProcessImage(image, options);

                string outputPath = BuildOutputPath(inputPath, outputDirectory, options);
                await SaveImageAsync(image, outputPath, options, cancellationToken);
                outputs.Add(outputPath);

                progress.Report(new ToolProgress(
                    Percent(index + 1, imagePaths.Count),
                    $"Converted {Path.GetFileName(inputPath)}."));
            }

            progress.Report(new ToolProgress(100, "Image conversion completed."));
            return (IReadOnlyList<string>)outputs;
        }, cancellationToken);
    }

    public string PreviewOutputPath(string inputPath, string outputDirectory, ImageConversionOptions options)
    {
        return BuildOutputPath(inputPath, outputDirectory, options);
    }

    private static void ProcessImage(Image<Rgba32> image, ImageConversionOptions options)
    {
        if (options.CropToAspectRatio && options.AspectRatio > 0)
        {
            ImageSharpRectangle crop = CenterCropForAspectRatio(image.Width, image.Height, options.AspectRatio);
            image.Mutate(context => context.Crop(crop));
        }

        if (options.ResizeMode != ImageResizeMode.None && (options.Width > 0 || options.Height > 0))
        {
            ResizeOptions resizeOptions = BuildResizeOptions(image.Width, image.Height, options);
            image.Mutate(context => context.Resize(resizeOptions));
        }
    }

    private static ResizeOptions BuildResizeOptions(int currentWidth, int currentHeight, ImageConversionOptions options)
    {
        int width = options.Width > 0 ? options.Width : CalculateWidth(currentWidth, currentHeight, options.Height);
        int height = options.Height > 0 ? options.Height : CalculateHeight(currentWidth, currentHeight, options.Width);

        return new ResizeOptions
        {
            Size = new ImageSharpSize(Math.Max(1, width), Math.Max(1, height)),
            Mode = options.ResizeMode switch
            {
                ImageResizeMode.Fill => ResizeMode.Crop,
                ImageResizeMode.Stretch => ResizeMode.Stretch,
                _ => ResizeMode.Max
            }
        };
    }

    private static int CalculateWidth(int currentWidth, int currentHeight, int targetHeight)
    {
        if (targetHeight <= 0 || currentHeight <= 0)
        {
            return currentWidth;
        }

        return (int)Math.Round(currentWidth * targetHeight / (double)currentHeight);
    }

    private static int CalculateHeight(int currentWidth, int currentHeight, int targetWidth)
    {
        if (targetWidth <= 0 || currentWidth <= 0)
        {
            return currentHeight;
        }

        return (int)Math.Round(currentHeight * targetWidth / (double)currentWidth);
    }

    private static ImageSharpRectangle CenterCropForAspectRatio(int width, int height, double aspectRatio)
    {
        double currentRatio = width / (double)height;
        if (currentRatio > aspectRatio)
        {
            int cropWidth = (int)Math.Round(height * aspectRatio);
            int x = (width - cropWidth) / 2;
            return new ImageSharpRectangle(x, 0, cropWidth, height);
        }

        int cropHeight = (int)Math.Round(width / aspectRatio);
        int y = (height - cropHeight) / 2;
        return new ImageSharpRectangle(0, y, width, cropHeight);
    }

    private static async Task SaveImageAsync(
        Image<Rgba32> image,
        string outputPath,
        ImageConversionOptions options,
        CancellationToken cancellationToken)
    {
        switch (options.Format)
        {
            case ImageOutputFormat.Png:
                await image.SaveAsPngAsync(outputPath, new PngEncoder(), cancellationToken);
                break;
            case ImageOutputFormat.Webp:
                await image.SaveAsWebpAsync(outputPath, new WebpEncoder { Quality = Math.Clamp(options.Quality, 1, 100) }, cancellationToken);
                break;
            default:
                await image.SaveAsJpegAsync(outputPath, new JpegEncoder { Quality = Math.Clamp(options.Quality, 1, 100) }, cancellationToken);
                break;
        }
    }

    private static string BuildOutputPath(string inputPath, string outputDirectory, ImageConversionOptions options)
    {
        string baseName = Path.GetFileNameWithoutExtension(inputPath);
        string extension = options.Format switch
        {
            ImageOutputFormat.Png => ".png",
            ImageOutputFormat.Webp => ".webp",
            _ => ".jpg"
        };

        string suffix = string.IsNullOrWhiteSpace(options.Suffix) ? "_converted" : options.Suffix.Trim();
        string candidate = Path.Combine(outputDirectory, baseName + suffix + extension);
        return UniquePath(candidate);
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

    private static int Percent(int done, int total)
    {
        if (total <= 0)
        {
            return 0;
        }

        return Math.Clamp((int)Math.Round(done * 100.0 / total), 0, 100);
    }
}
