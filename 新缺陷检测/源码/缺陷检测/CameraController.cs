using System.Runtime.InteropServices;
using GxIAPINET;
using GxIAPINET.Sample.Common;

namespace 缺陷检测生产端;

internal sealed class CameraController : IAsyncDisposable
{
    private readonly SemaphoreSlim _sdkGate = new(1, 1);
    private readonly object _requestGate = new();
    private IGXFactory? _factory;
    private IGXDevice? _device;
    private IGXStream? _stream;
    private IGXFeatureControl? _remote;
    private IGXFeatureControl? _streamFeatures;
    private GxBitmap? _bitmap;
    private CancellationTokenSource? _previewCancellation;
    private Task? _previewTask;
    private TaskCompletionSource<CapturedFrame>? _manualRequest;
    private double _exposureMicroseconds = 10000;

    public bool IsInitialized => _factory is not null;
    public bool IsConnected => _device is not null;
    public bool IsPreviewing => _previewTask is { IsCompleted: false };
    public CameraInfo? ConnectedCamera { get; private set; }
    public event Action<string>? StatusChanged;
    public event Action<Exception>? CameraFaulted;
    public event Action<int>? FramePercentile95Updated;

    public void Initialize()
    {
        if (_factory is not null)
            return;
        ConfigureGenTlPath();
        _factory = IGXFactory.GetInstance();
        _factory.Init();
        StatusChanged?.Invoke("GalaxySDK 已初始化");
    }

    private static void ConfigureGenTlPath()
    {
        string bundledGenTl = Path.Combine(AppContext.BaseDirectory, "GenTL");
        if (!Directory.Exists(bundledGenTl))
            return;
        const string variable = "GENICAM_GENTL64_PATH";
        string current = Environment.GetEnvironmentVariable(variable) ?? "";
        string[] paths = current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!paths.Contains(bundledGenTl, StringComparer.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable(variable,
                string.IsNullOrWhiteSpace(current) ? bundledGenTl : bundledGenTl + Path.PathSeparator + current);
    }

    public IReadOnlyList<CameraInfo> RefreshDevices()
    {
        EnsureInitialized();
        List<IGXDeviceInfo> devices = new();
        _factory!.UpdateAllDeviceList(300, devices);
        return devices.Select(info => new CameraInfo(
            info.GetDisplayName(), info.GetVendorName(), info.GetModelName(), info.GetSN())).ToList();
    }

    public async Task<(double Value, double Minimum, double Maximum, string Unit, int Width, int Height)> ConnectAsync(
        CameraInfo info,
        PictureBox liveView,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await DisconnectAsync();
        await _sdkGate.WaitAsync(cancellationToken);
        try
        {
            _device = _factory!.OpenDeviceBySN(info.SerialNumber, GX_ACCESS_MODE.GX_ACCESS_EXCLUSIVE);
            _remote = _device.GetRemoteFeatureControl();

            // 与大恒官方 GxMultiCamForHDR 示例一致：采集前清零 ROI 偏移，
            // 再把宽高设为相机当前配置下允许的最大值，避免继承上电参数组中的 ROI。
            (int fullWidth, int fullHeight) = ConfigureFullResolution(_remote);

            // 官方 GxGetImage 示例：打开设备后先打开流，再初始化采集功能。
            _stream = _device.OpenStream(0);
            _streamFeatures = _stream.GetFeatureControl();

            if (_device.GetDeviceInfo().GetDeviceClass() == GX_DEVICE_CLASS_LIST.GX_DEVICE_CLASS_GEV &&
                _remote.IsImplemented("GevSCPSPacketSize"))
            {
                uint packetSize = _stream.GetOptimalPacketSize();
                _remote.GetIntFeature("GevSCPSPacketSize").SetValue(packetSize);
            }

            // 与官方 GxGetImage 示例一致：连续采集模式、软触发源。
            _remote.GetEnumFeature("AcquisitionMode").SetValue("Continuous");
            _remote.GetEnumFeature("TriggerMode").SetValue("On");
            _remote.GetEnumFeature("TriggerSource").SetValue("Software");

            // 生产数据路径固定为未打包 Mono10，便于无损保存 0..1023 原始值。
            if (!_remote.IsImplemented("PixelFormat"))
                throw new NotSupportedException("相机没有实现 PixelFormat 功能。");
            _remote.GetEnumFeature("PixelFormat").SetValue("Mono10");
            string actualPixelFormat = _remote.GetEnumFeature("PixelFormat").GetValue();
            if (!string.Equals(actualPixelFormat, "Mono10", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"相机未能切换到 Mono10，实际格式：{actualPixelFormat}");

            _bitmap = new GxBitmap(_device, liveView, _stream, _factory);
            var exposure = _remote.GetFloatFeature("ExposureTime");
            _exposureMicroseconds = exposure.GetValue();
            ConnectedCamera = info;
            int width = checked((int)_remote.GetIntFeature("Width").GetValue());
            int height = checked((int)_remote.GetIntFeature("Height").GetValue());
            if (width != fullWidth || height != fullHeight)
                throw new InvalidOperationException(
                    $"相机全分辨率设置回读不一致：期望 {fullWidth}×{fullHeight}，实际 {width}×{height}。");
            StatusChanged?.Invoke(
                $"已连接 {info.Model} / {info.SerialNumber}，{width}×{height}，像素格式 {actualPixelFormat}");
            return (_exposureMicroseconds, exposure.GetMin(), exposure.GetMax(), exposure.GetUnit(), width, height);
        }
        catch
        {
            CloseResourcesWithoutFactory();
            throw;
        }
        finally
        {
            _sdkGate.Release();
        }
    }

    public async Task StartPreviewAsync(CancellationToken cancellationToken)
    {
        if (_stream is null || _remote is null)
            throw new InvalidOperationException("请先连接相机。");
        if (IsPreviewing)
            return;

        await _sdkGate.WaitAsync(cancellationToken);
        try
        {
            if (_streamFeatures is not null)
            {
                try
                {
                    // 与官方示例相同，在 StartGrab 前设置流缓冲处理模式。
                    _streamFeatures.GetEnumFeature("StreamBufferHandlingMode").SetValue("OldestFirst");
                }
                catch
                {
                    // 部分 USB3 固件不暴露此流功能，不影响取流。
                }
            }
            _stream.StartGrab();
            _remote.GetCommandFeature("AcquisitionStart").Execute();
        }
        finally
        {
            _sdkGate.Release();
        }

        _previewCancellation = new CancellationTokenSource();
        _previewTask = Task.Run(() => PreviewLoopAsync(_previewCancellation.Token));
        StatusChanged?.Invoke("实时预览已启动（官方软触发取流）");
    }

    public async Task StopPreviewAsync()
    {
        if (_previewCancellation is not null)
        {
            _previewCancellation.Cancel();
            try
            {
                if (_previewTask is not null)
                    await _previewTask;
            }
            catch (OperationCanceledException)
            {
            }
            _previewCancellation.Dispose();
            _previewCancellation = null;
            _previewTask = null;
        }

        TaskCompletionSource<CapturedFrame>? pending;
        lock (_requestGate)
        {
            pending = _manualRequest;
            _manualRequest = null;
        }
        pending?.TrySetCanceled();

        if (_stream is null || _remote is null)
            return;
        await _sdkGate.WaitAsync();
        try
        {
            try { _remote.GetCommandFeature("AcquisitionStop").Execute(); } catch { }
            try { _stream.StopGrab(); } catch { }
        }
        finally
        {
            _sdkGate.Release();
        }
        StatusChanged?.Invoke("实时预览已停止");
    }

    public async Task<CapturedFrame> TriggerOnceAsync(CancellationToken cancellationToken)
    {
        if (!IsPreviewing)
            throw new InvalidOperationException("预览取流尚未启动。");
        TaskCompletionSource<CapturedFrame> request = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_requestGate)
        {
            if (_manualRequest is not null)
                throw new InvalidOperationException("上一帧仍在采集，请勿重复触发。");
            _manualRequest = request;
        }
        return await request.Task.WaitAsync(cancellationToken);
    }

    public async Task<double> SetExposureAsync(double microseconds, CancellationToken cancellationToken)
    {
        if (_remote is null)
            throw new InvalidOperationException("请先连接相机。");
        await _sdkGate.WaitAsync(cancellationToken);
        try
        {
            var feature = _remote.GetFloatFeature("ExposureTime");
            double applied = Math.Clamp(microseconds, feature.GetMin(), feature.GetMax());
            feature.SetValue(applied);
            _exposureMicroseconds = feature.GetValue(); // 按官方接口回读确认。
            return _exposureMicroseconds;
        }
        finally
        {
            _sdkGate.Release();
        }
    }

    private async Task PreviewLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TaskCompletionSource<CapturedFrame>? manual;
            lock (_requestGate)
                manual = _manualRequest;
            try
            {
                CapturedFrame captured = await AcquireOneAsync(cancellationToken);
                if (manual is not null)
                {
                    lock (_requestGate)
                    {
                        if (ReferenceEquals(_manualRequest, manual))
                            _manualRequest = null;
                    }
                    manual.TrySetResult(captured);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                if (manual is not null)
                {
                    lock (_requestGate)
                    {
                        if (ReferenceEquals(_manualRequest, manual))
                            _manualRequest = null;
                    }
                    manual.TrySetException(error);
                }
                CameraFaulted?.Invoke(error);
                await Task.Delay(300, cancellationToken);
            }
            await Task.Delay(80, cancellationToken);
        }
    }

    private async Task<CapturedFrame> AcquireOneAsync(CancellationToken cancellationToken)
    {
        await _sdkGate.WaitAsync(cancellationToken);
        try
        {
            if (_stream is null || _remote is null || _bitmap is null)
                throw new InvalidOperationException("相机连接已断开。");

            // 官方 GxGetImage 明确要求触发前清队列，防止取到上一次触发的缓存帧。
            _stream.FlushQueue();
            _remote.GetCommandFeature("TriggerSoftware").Execute();
            uint timeout = (uint)Math.Clamp(Math.Ceiling(_exposureMicroseconds / 1000.0) + 1200, 1500, 30000);
            IImageData image = _stream.GetImage(timeout);
            try
            {
                if (image.GetStatus() != GX_FRAME_STATUS_LIST.GX_FRAME_STATUS_SUCCESS)
                    throw new IOException($"相机返回不完整帧，状态：{image.GetStatus()}");
                if (image.GetPixelFormat() != GX_PIXEL_FORMAT_ENTRY.GX_PIXEL_FORMAT_MONO10)
                    throw new InvalidDataException($"期望 Mono10，实际像素格式：{image.GetPixelFormat()}");

                int width = checked((int)image.GetWidth());
                int height = checked((int)image.GetHeight());
                int pixelBytes = checked(width * height * 2);
                if ((ulong)pixelBytes > image.GetPayloadSize())
                    throw new InvalidDataException("Mono10 帧负载小于预期尺寸。");
                byte[] raw = new byte[pixelBytes];
                Marshal.Copy(image.GetBuffer(), raw, 0, raw.Length);
                ushort[] pixels = new ushort[width * height];
                Buffer.BlockCopy(raw, 0, pixels, 0, raw.Length);
                if (pixels.Any(value => value > 1023))
                    throw new InvalidDataException("Mono10 帧中出现大于 1023 的数值，拒绝静默截断原始数据。");

                _bitmap.Show(image); // 官方 GxBitmap：Mono10 取有效位并转 Mono8 后显示。
                FramePercentile95Updated?.Invoke(ComputePercentile95(pixels));
                return new CapturedFrame(pixels, width, height, image.GetFrameID(), DateTime.Now);
            }
            finally
            {
                image.Destroy();
            }
        }
        finally
        {
            _sdkGate.Release();
        }
    }

    private static (int Width, int Height) ConfigureFullResolution(IGXFeatureControl remote)
    {
        string[] requiredFeatures = ["OffsetX", "OffsetY", "Width", "Height", "WidthMax", "HeightMax"];
        string? missingFeature = requiredFeatures.FirstOrDefault(feature => !remote.IsImplemented(feature));
        if (missingFeature is not null)
            throw new NotSupportedException($"相机没有实现全分辨率设置所需功能：{missingFeature}");

        long widthMax = remote.GetIntFeature("WidthMax").GetValue();
        long heightMax = remote.GetIntFeature("HeightMax").GetValue();

        remote.GetIntFeature("OffsetX").SetValue(0);
        remote.GetIntFeature("OffsetY").SetValue(0);
        remote.GetIntFeature("Width").SetValue(widthMax);
        remote.GetIntFeature("Height").SetValue(heightMax);

        long offsetX = remote.GetIntFeature("OffsetX").GetValue();
        long offsetY = remote.GetIntFeature("OffsetY").GetValue();
        long width = remote.GetIntFeature("Width").GetValue();
        long height = remote.GetIntFeature("Height").GetValue();
        if (offsetX != 0 || offsetY != 0 || width != widthMax || height != heightMax)
            throw new InvalidOperationException(
                $"相机 ROI 设置回读失败：Offset=({offsetX},{offsetY})，尺寸={width}×{height}，最大={widthMax}×{heightMax}。");

        return (checked((int)widthMax), checked((int)heightMax));
    }

    private static int ComputePercentile95(ushort[] pixels)
    {
        Span<int> histogram = stackalloc int[1024];
        histogram.Clear(); // stackalloc 不保证初值为零，避免 P95 受到栈中旧值影响。
        foreach (ushort value in pixels)
            histogram[value]++;

        int target = (int)Math.Ceiling(pixels.Length * 0.95);
        int cumulative = 0;
        for (int value = 0; value < histogram.Length; value++)
        {
            cumulative += histogram[value];
            if (cumulative >= target)
                return value;
        }
        return 1023;
    }

    public async Task DisconnectAsync()
    {
        await StopPreviewAsync();
        await _sdkGate.WaitAsync();
        try
        {
            CloseResourcesWithoutFactory();
            ConnectedCamera = null;
        }
        finally
        {
            _sdkGate.Release();
        }
    }

    private void CloseResourcesWithoutFactory()
    {
        try { _bitmap?.ReleaseBuffer(); } catch { }
        _bitmap = null;
        try { _stream?.Close(); } catch { }
        _stream = null;
        _streamFeatures = null;
        try { _device?.Close(); } catch { }
        _device = null;
        _remote = null;
    }

    private void EnsureInitialized()
    {
        if (_factory is null)
            throw new InvalidOperationException("GalaxySDK 尚未初始化。");
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        try { _factory?.Uninit(); } catch { }
        _factory = null;
        _sdkGate.Dispose();
    }
}

internal sealed record CameraInfo(string DisplayName, string Vendor, string Model, string SerialNumber)
{
    public override string ToString() => $"{Model}  |  {SerialNumber}";
}

internal sealed record CapturedFrame(ushort[] Pixels, int Width, int Height, ulong FrameId, DateTime CapturedAt);
