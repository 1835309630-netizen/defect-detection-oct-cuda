using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenCvSharp;

namespace 缺陷检测生产端;

internal static class ImagePipeline
{
    public static CalibrationInfo InspectCalibration(string path)
    {
        using Mat image = Cv2.ImRead(path, ImreadModes.Unchanged);
        if (image.Empty())
            throw new InvalidDataException("无法读取 TIFF 图像。");
        if (image.Channels() != 1)
            throw new InvalidDataException("标定图必须是单通道灰度 TIFF。");
        Cv2.MinMaxLoc(image, out double minimum, out double maximum);
        return new CalibrationInfo(path, image.Width, image.Height, image.Depth(), minimum, maximum);
    }

    public static void SaveMono10Tiff(CapturedFrame frame, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using Mat image = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_16UC1, frame.Pixels);
        if (!Cv2.ImWrite(path, image, new[] { (int)ImwriteFlags.TiffCompression, 1 }))
            throw new IOException("保存原始 Mono10 TIFF 失败。");
    }

    public static async Task<DetectionReport> RunAsync(
        string rawPath,
        string? backgroundPath,
        string? flatPath,
        string outputDirectory,
        string resultRoot,
        DateTime capturedAt,
        CancellationToken cancellationToken)
    {
        List<string> arguments = ["--raw", rawPath, "--output", outputDirectory];
        arguments.AddRange(["--result-root", resultRoot, "--captured-at", capturedAt.ToString("O")]);
        if (!string.IsNullOrWhiteSpace(backgroundPath))
            arguments.AddRange(["--background", backgroundPath]);
        if (!string.IsNullOrWhiteSpace(flatPath))
            arguments.AddRange(["--flat", flatPath]);
        return await RunEngineAsync<DetectionReport>(arguments, cancellationToken);
    }

    public static Task<TemplateReport> BuildTemplateAsync(string[] paths, string output, CancellationToken token) =>
        RunEngineAsync<TemplateReport>(new[] { "--output", output, "--build-template" }.Concat(paths), token);

    private static async Task<T> RunEngineAsync<T>(IEnumerable<string> arguments, CancellationToken cancellationToken)
        where T : class
    {
        string engineDirectory = Path.Combine(AppContext.BaseDirectory, "算法引擎");
        string bundledEngine = Path.Combine(engineDirectory, "检测引擎.exe");
        if (!File.Exists(bundledEngine))
            throw new FileNotFoundException("发布包缺少算法引擎：算法引擎\\检测引擎.exe", bundledEngine);

        ProcessStartInfo start = new()
        {
            FileName = bundledEngine,
            WorkingDirectory = engineDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        using Process process = new() { StartInfo = start };
        process.Start();
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            await process.WaitForExitAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
            throw;
        }
        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "检测引擎执行失败。" : stderr.Trim());

        T? report = JsonSerializer.Deserialize<T>(stdout,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (report is null)
            throw new InvalidDataException("检测引擎没有返回有效结果。");
        return report;
    }
}

internal sealed record CalibrationInfo(string Path, int Width, int Height, int Depth, double Minimum, double Maximum);

internal sealed class DetectionReport
{
    public bool Ok { get; set; }
    public string Status { get; set; } = "";
    public string PartType { get; set; } = "";
    public int DefectCount { get; set; }
    public string CorrectedPath { get; set; } = "";
    public string ResultPath { get; set; } = "";
    public string? Error { get; set; }
    public string TemplateName { get; set; } = "";
    public double TemplateScore { get; set; }
    public string RecognitionStatus { get; set; } = "";
}

internal sealed class TemplateReport
{
    public bool Ok { get; set; }
    public string OutputPath { get; set; } = "";
    public int ImageCount { get; set; }
    public double MinimumScore { get; set; }
}
