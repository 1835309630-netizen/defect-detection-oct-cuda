namespace 缺陷检测生产端;

internal sealed class MainForm : Form
{
    private const string PreferredCameraSn = "NV0200090083";
    private readonly CameraController _camera = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ComboBox _devices = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Button _refreshButton = new() { Text = "刷新设备", AutoSize = true };
    private readonly Button _connectButton = new() { Text = "连接相机", AutoSize = true };
    private readonly Button _disconnectButton = new() { Text = "断开", AutoSize = true, Enabled = false };
    private readonly Label _cameraDetail = new() { Text = "尚未连接", AutoSize = true, ForeColor = Color.DimGray };
    private readonly NumericUpDown _exposure = new() { DecimalPlaces = 1, Increment = 100, ThousandsSeparator = true, Dock = DockStyle.Fill };
    private readonly Label _exposureUnit = new() { Text = "μs", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _applyExposure = new() { Text = "应用并回读", AutoSize = true, Enabled = false };
    private readonly TextBox _backgroundPath = PathBox();
    private readonly TextBox _flatPath = PathBox();
    private readonly TextBox _capturePath = PathBox();
    private readonly TextBox _outputPath = PathBox();
    private readonly Button _captureBackgroundButton = new() { Text = "一键采集", AutoSize = true, Enabled = false };
    private readonly Button _captureFlatButton = new() { Text = "一键采集", AutoSize = true, Enabled = false };
    private readonly Button _saveFrameButton = new()
    {
        Text = "软触发采集并保存",
        Height = 34,
        Dock = DockStyle.Fill,
        Enabled = false,
    };
    private readonly Button _triggerButton = new()
    {
        Text = "触发采集并检测",
        Height = 44,
        Dock = DockStyle.Fill,
        Enabled = false,
        Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold),
        BackColor = Color.FromArgb(0, 120, 212),
        ForeColor = Color.White,
        FlatStyle = FlatStyle.Flat,
    };
    private readonly Label _lastResult = new() { Text = "等待首次采集", AutoSize = true, AutoEllipsis = true };
    private readonly PictureBox _liveView = ImageBox("实时画面将在连接相机后显示");
    private readonly PictureBox _resultView = ImageBox("检测结果帧将在这里显示");
    private readonly Label _livePlaceholder = Placeholder("连接相机后显示实时 Mono10 画面");
    private readonly Label _resultPlaceholder = Placeholder("触发采集后显示缺陷检测结果帧");
    private readonly Label _grayP95Label = new()
    {
        Text = "灰度 P95（Mono10）：-- / 1023",
        AutoSize = true,
        BackColor = Color.FromArgb(190, 20, 23, 27),
        ForeColor = Color.White,
        Padding = new Padding(8, 5, 8, 5),
        Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
    };
    private readonly ToolStripStatusLabel _statusText = new("就绪") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _cameraStatus = new("相机：未连接");
    private readonly ToolStripProgressBar _busy = new() { Style = ProgressBarStyle.Marquee, Visible = false, Width = 120 };
    private CalibrationInfo? _background;
    private CalibrationInfo? _flat;
    private bool _processing;
    private int _cameraWidth;
    private int _cameraHeight;
    private double _viewAspectRatio = 1.0;
    private readonly List<Action> _viewFitters = new();
    private Image? _resultImage;
    private string _captureRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "相机采集");
    private string _outputRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "缺陷检测结果");

    public MainForm()
    {
        Text = "缺陷检测生产端 V2";
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        MinimumSize = new Size(1180, 760);
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.FromArgb(245, 247, 250);
        _capturePath.Text = _captureRoot;
        _outputPath.Text = _outputRoot;
        BuildLayout();
        WireEvents();
    }

    private void BuildLayout()
    {
        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(10),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        // 连接状态和曝光应用按钮已分别并入同行；保留高 DPI 余量，同时减少顶部空白。
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 310));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        TableLayoutPanel controls = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 8),
        };
        controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31));
        controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31));
        GroupBox cameraGroup = BuildCameraGroup();
        GroupBox calibrationGroup = BuildCalibrationGroup();
        GroupBox acquisitionGroup = BuildAcquisitionGroup();
        cameraGroup.Margin = new Padding(0, 0, 5, 0);
        calibrationGroup.Margin = new Padding(5, 0, 5, 0);
        acquisitionGroup.Margin = new Padding(5, 0, 0, 0);
        controls.Controls.Add(cameraGroup, 0, 0);
        controls.Controls.Add(calibrationGroup, 1, 0);
        controls.Controls.Add(acquisitionGroup, 2, 0);
        root.Controls.Add(controls, 0, 0);

        TableLayoutPanel views = new()
        {
            Dock = DockStyle.Fill,
            RowCount = 1,
            ColumnCount = 2,
            BackColor = Color.FromArgb(220, 224, 230),
        };
        views.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        views.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        views.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        GroupBox liveGroup = BuildViewGroup("实时相机画面 · Mono10", _liveView, _livePlaceholder, _grayP95Label);
        GroupBox resultGroup = BuildViewGroup("检测结果帧", _resultView, _resultPlaceholder);
        liveGroup.Margin = new Padding(0, 0, 5, 0);
        resultGroup.Margin = new Padding(5, 0, 0, 0);
        views.Controls.Add(liveGroup, 0, 0);
        views.Controls.Add(resultGroup, 1, 0);
        root.Controls.Add(views, 0, 1);

        StatusStrip status = new();
        status.Items.Add(_statusText);
        status.Items.Add(_busy);
        status.Items.Add(_cameraStatus);
        Controls.Add(status);
    }

    private GroupBox BuildCameraGroup()
    {
        GroupBox group = NewTopGroup("相机");
        TableLayoutPanel grid = NewGrid(3);
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.Controls.Add(_devices, 0, 0);
        grid.SetColumnSpan(_devices, 2);
        grid.Controls.Add(_refreshButton, 2, 0);
        FlowLayoutPanel connectionLine = new()
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            WrapContents = false,
        };
        connectionLine.Controls.Add(_connectButton);
        connectionLine.Controls.Add(_disconnectButton);
        _cameraDetail.AutoEllipsis = true;
        _cameraDetail.MaximumSize = new Size(360, 38);
        _cameraDetail.Margin = new Padding(10, 7, 3, 3);
        connectionLine.Controls.Add(_cameraDetail);
        grid.Controls.Add(connectionLine, 0, 1);
        grid.SetColumnSpan(connectionLine, 3);

        FlowLayoutPanel exposureLine = new()
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            WrapContents = false,
        };
        Label exposureTitle = new() { Text = "曝光时间", AutoSize = true, Margin = new Padding(3, 7, 8, 3) };
        _exposure.Dock = DockStyle.None;
        _exposure.Width = 150;
        _exposureUnit.Margin = new Padding(3, 7, 8, 3);
        exposureLine.Controls.Add(exposureTitle);
        exposureLine.Controls.Add(_exposure);
        exposureLine.Controls.Add(_exposureUnit);
        exposureLine.Controls.Add(_applyExposure);
        grid.Controls.Add(exposureLine, 0, 2);
        grid.SetColumnSpan(exposureLine, 3);
        Label captureTitle = new() { Text = "采集/标定保存目录", AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
        grid.Controls.Add(captureTitle, 0, 3);
        grid.SetColumnSpan(captureTitle, 3);
        grid.Controls.Add(_capturePath, 0, 4);
        grid.SetColumnSpan(_capturePath, 2);
        Button chooseCapture = new() { Text = "选择…", AutoSize = true };
        grid.Controls.Add(chooseCapture, 2, 4);
        grid.Controls.Add(_saveFrameButton, 0, 5);
        grid.SetColumnSpan(_saveFrameButton, 3);
        chooseCapture.Click += (_, _) => ChooseCaptureDirectory();
        group.Controls.Add(grid);
        return group;
    }

    private GroupBox BuildCalibrationGroup()
    {
        GroupBox group = NewTopGroup("可选预处理");
        TableLayoutPanel grid = NewGrid(4);
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Label backgroundTitle = new() { Text = "背景 TIFF（可选）", AutoSize = true };
        grid.Controls.Add(backgroundTitle, 0, 0);
        grid.SetColumnSpan(backgroundTitle, 4);
        grid.Controls.Add(_backgroundPath, 0, 1);
        Button browseBackground = new() { Text = "选择…", AutoSize = true, Tag = "background" };
        grid.Controls.Add(browseBackground, 1, 1);
        grid.Controls.Add(_captureBackgroundButton, 2, 1);
        Button clearBackground = new() { Text = "清除", AutoSize = true };
        grid.Controls.Add(clearBackground, 3, 1);
        Label flatTitle = new() { Text = "平场 TIFF（可选）", AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
        grid.Controls.Add(flatTitle, 0, 2);
        grid.SetColumnSpan(flatTitle, 4);
        grid.Controls.Add(_flatPath, 0, 3);
        Button browseFlat = new() { Text = "选择…", AutoSize = true, Tag = "flat" };
        grid.Controls.Add(browseFlat, 1, 3);
        grid.Controls.Add(_captureFlatButton, 2, 3);
        Button clearFlat = new() { Text = "清除", AutoSize = true };
        grid.Controls.Add(clearFlat, 3, 3);
        browseBackground.Click += (_, _) => ChooseCalibration(true);
        browseFlat.Click += (_, _) => ChooseCalibration(false);
        clearBackground.Click += (_, _) => ClearCalibration(true);
        clearFlat.Click += (_, _) => ClearCalibration(false);
        group.Controls.Add(grid);
        return group;
    }

    private GroupBox BuildAcquisitionGroup()
    {
        GroupBox group = NewTopGroup("检测");
        TableLayoutPanel panel = NewGrid(2);
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Label outputTitle = new() { Text = "检测结果保存目录", AutoSize = true };
        panel.Controls.Add(outputTitle, 0, 0);
        panel.SetColumnSpan(outputTitle, 2);
        panel.Controls.Add(_outputPath, 0, 1);
        Button chooseOutput = new() { Text = "选择…", AutoSize = true };
        panel.Controls.Add(chooseOutput, 1, 1);
        _triggerButton.FlatAppearance.BorderSize = 0;
        panel.Controls.Add(_triggerButton, 0, 2);
        panel.SetColumnSpan(_triggerButton, 2);
        panel.Controls.Add(_lastResult, 0, 3);
        panel.SetColumnSpan(_lastResult, 2);
        chooseOutput.Click += (_, _) => ChooseOutputDirectory();
        group.Controls.Add(panel);
        return group;
    }

    private void WireEvents()
    {
        Shown += async (_, _) => await InitializeCameraAsync();
        _refreshButton.Click += async (_, _) => await RefreshDevicesAsync();
        _connectButton.Click += async (_, _) => await ConnectAsync();
        _disconnectButton.Click += async (_, _) => await DisconnectAsync();
        _applyExposure.Click += async (_, _) => await ApplyExposureAsync();
        _saveFrameButton.Click += async (_, _) => await CaptureAndSaveAsync();
        _captureBackgroundButton.Click += async (_, _) => await CaptureCalibrationAsync(true);
        _captureFlatButton.Click += async (_, _) => await CaptureCalibrationAsync(false);
        _triggerButton.Click += async (_, _) => await TriggerAndDetectAsync();
        _camera.StatusChanged += message => PostStatus(message);
        _camera.CameraFaulted += error => PostStatus($"相机取流异常：{error.Message}");
        _camera.FramePercentile95Updated += value => PostGrayPercentile(value);
    }

    private async Task InitializeCameraAsync()
    {
        try
        {
            _camera.Initialize();
            await RefreshDevicesAsync();
        }
        catch (Exception error)
        {
            ShowError("GalaxySDK 初始化失败", error);
        }
    }

    private Task RefreshDevicesAsync()
    {
        try
        {
            IReadOnlyList<CameraInfo> devices = _camera.RefreshDevices();
            _devices.DataSource = devices.ToList();
            CameraInfo? preferred = devices.FirstOrDefault(item => item.SerialNumber == PreferredCameraSn);
            if (preferred is not null)
                _devices.SelectedItem = preferred;
            SetStatus(devices.Count == 0 ? "未发现大恒相机" : $"发现 {devices.Count} 台相机");
        }
        catch (Exception error)
        {
            ShowError("刷新设备失败", error);
        }
        return Task.CompletedTask;
    }

    private async Task ConnectAsync()
    {
        if (_devices.SelectedItem is not CameraInfo selected)
        {
            MessageBox.Show(this, "未发现可连接的相机。", "连接相机", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        SetBusy(true, "正在连接相机……");
        try
        {
            var exposure = await _camera.ConnectAsync(selected, _liveView, _lifetime.Token);
            _cameraWidth = exposure.Width;
            _cameraHeight = exposure.Height;
            _viewAspectRatio = (double)_cameraWidth / Math.Max(1, _cameraHeight);
            foreach (Action fitView in _viewFitters)
                fitView();
            decimal min = ClampDecimal(exposure.Minimum);
            decimal max = ClampDecimal(exposure.Maximum);
            _exposure.Minimum = min;
            _exposure.Maximum = Math.Max(min, max);
            _exposure.Value = Math.Clamp(ClampDecimal(exposure.Value), _exposure.Minimum, _exposure.Maximum);
            _exposureUnit.Text = string.IsNullOrWhiteSpace(exposure.Unit) ? "μs" : exposure.Unit;
            _cameraDetail.Text = $"{selected.Model}  |  SN {selected.SerialNumber}  |  {_cameraWidth}×{_cameraHeight}  Mono10";
            _cameraDetail.ForeColor = Color.FromArgb(0, 100, 55);
            _cameraStatus.Text = $"相机：{selected.SerialNumber}";
            _connectButton.Enabled = false;
            _disconnectButton.Enabled = true;
            _applyExposure.Enabled = true;
            await _camera.StartPreviewAsync(_lifetime.Token);
            _livePlaceholder.Visible = false;
            if (!CalibrationDimensionsMatch(out string calibrationMessage))
                SetStatus($"相机已连接，但标定图暂不可用：{calibrationMessage}");
            UpdateTriggerState();
        }
        catch (Exception error)
        {
            ShowError("连接相机失败", error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task DisconnectAsync()
    {
        SetBusy(true, "正在断开相机……");
        try
        {
            await _camera.DisconnectAsync();
            _cameraDetail.Text = "尚未连接";
            _cameraDetail.ForeColor = Color.DimGray;
            _cameraStatus.Text = "相机：未连接";
            _connectButton.Enabled = true;
            _disconnectButton.Enabled = false;
            _applyExposure.Enabled = false;
            _cameraWidth = _cameraHeight = 0;
            _viewAspectRatio = 1.0;
            _grayP95Label.Text = "灰度 P95（Mono10）：-- / 1023";
            foreach (Action fitView in _viewFitters)
                fitView();
            _livePlaceholder.Visible = true;
        }
        catch (Exception error)
        {
            ShowError("断开相机失败", error);
        }
        finally
        {
            SetBusy(false);
            UpdateTriggerState();
        }
    }

    private async Task ApplyExposureAsync()
    {
        try
        {
            double applied = await _camera.SetExposureAsync((double)_exposure.Value, _lifetime.Token);
            _exposure.Value = Math.Clamp(ClampDecimal(applied), _exposure.Minimum, _exposure.Maximum);
            SetStatus($"曝光已应用并从相机回读：{applied:F1} {_exposureUnit.Text}");
        }
        catch (Exception error)
        {
            ShowError("设置曝光失败", error);
        }
    }

    private void ChooseCalibration(bool background)
    {
        using OpenFileDialog dialog = new()
        {
            Title = background ? "选择背景 TIFF" : "选择平场 TIFF",
            Filter = "TIFF 图像|*.tif;*.tiff|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            CalibrationInfo info = ImagePipeline.InspectCalibration(dialog.FileName);
            CalibrationInfo? previous = background ? _background : _flat;
            if (background)
            {
                _background = info;
                _backgroundPath.Text = info.Path;
            }
            else
            {
                _flat = info;
                _flatPath.Text = info.Path;
            }
            if (!CalibrationDimensionsMatch(out string message))
            {
                if (background)
                {
                    _background = previous;
                    _backgroundPath.Text = previous?.Path ?? "";
                }
                else
                {
                    _flat = previous;
                    _flatPath.Text = previous?.Path ?? "";
                }
                throw new InvalidDataException(message);
            }
            UpdateTriggerState();
        }
        catch (Exception error)
        {
            ShowError("标定图读取失败", error);
        }
    }

    private void ClearCalibration(bool background)
    {
        if (background)
        {
            _background = null;
            _backgroundPath.Clear();
        }
        else
        {
            _flat = null;
            _flatPath.Clear();
        }
        UpdateTriggerState();
        SetStatus("预处理文件已清除；未选择时直接检测原始 Mono10 帧");
    }

    private void ChooseOutputDirectory()
    {
        using FolderBrowserDialog dialog = new()
        {
            Description = "选择检测结果保存目录",
            SelectedPath = Directory.Exists(_outputRoot)
                ? _outputRoot
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        _outputRoot = dialog.SelectedPath;
        _outputPath.Text = _outputRoot;
        SetStatus($"结果将保存到：{_outputRoot}");
    }

    private void ChooseCaptureDirectory()
    {
        using FolderBrowserDialog dialog = new()
        {
            Description = "选择普通采集、背景和平场的共用保存目录",
            SelectedPath = Directory.Exists(_captureRoot)
                ? _captureRoot
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        _captureRoot = dialog.SelectedPath;
        _capturePath.Text = _captureRoot;
        SetStatus($"采集和标定帧将保存到：{_captureRoot}");
    }

    private async Task CaptureAndSaveAsync()
    {
        if (_processing)
            return;
        _processing = true;
        SetBusy(true, "正在软触发并保存 Mono10 原始帧……");
        UpdateTriggerState();
        try
        {
            CapturedFrame frame = await _camera.TriggerOnceAsync(_lifetime.Token);
            Directory.CreateDirectory(_captureRoot);
            string path = Path.Combine(_captureRoot, $"采集_{frame.CapturedAt:yyyyMMdd_HHmmss_fff}_F{frame.FrameId}.tif");
            ImagePipeline.SaveMono10Tiff(frame, path);
            _lastResult.Text = $"{frame.CapturedAt:yyyy-MM-dd HH:mm:ss.fff}\n原始 Mono10 帧已保存\n帧号：{frame.FrameId}";
            _lastResult.ForeColor = Color.FromArgb(0, 100, 55);
            SetStatus($"软触发采集完成：{path}");
        }
        catch (OperationCanceledException)
        {
            SetStatus("操作已取消");
        }
        catch (Exception error)
        {
            ShowError("软触发采集失败", error);
        }
        finally
        {
            _processing = false;
            SetBusy(false);
            UpdateTriggerState();
        }
    }

    private async Task CaptureCalibrationAsync(bool background)
    {
        if (_processing)
            return;
        string name = background ? "背景" : "平场";
        _processing = true;
        SetBusy(true, $"正在软触发采集{name}帧……");
        UpdateTriggerState();
        try
        {
            CapturedFrame frame = await _camera.TriggerOnceAsync(_lifetime.Token);
            Directory.CreateDirectory(_captureRoot);
            string path = Path.Combine(_captureRoot, $"{name}_{frame.CapturedAt:yyyyMMdd_HHmmss_fff}_F{frame.FrameId}.tif");
            ImagePipeline.SaveMono10Tiff(frame, path);
            CalibrationInfo info = ImagePipeline.InspectCalibration(path);
            CalibrationInfo? previous = background ? _background : _flat;
            if (background)
            {
                _background = info;
                _backgroundPath.Text = info.Path;
            }
            else
            {
                _flat = info;
                _flatPath.Text = info.Path;
            }
            if (!CalibrationDimensionsMatch(out string message))
            {
                if (background)
                {
                    _background = previous;
                    _backgroundPath.Text = previous?.Path ?? "";
                }
                else
                {
                    _flat = previous;
                    _flatPath.Text = previous?.Path ?? "";
                }
                throw new InvalidDataException(message);
            }
            _lastResult.Text = $"{frame.CapturedAt:yyyy-MM-dd HH:mm:ss.fff}\n{name}帧已采集并自动设定\n帧号：{frame.FrameId}";
            _lastResult.ForeColor = Color.FromArgb(0, 100, 55);
            SetStatus($"{name}帧已采集、保存并设为当前{name}：{path}");
        }
        catch (OperationCanceledException)
        {
            SetStatus("操作已取消");
        }
        catch (Exception error)
        {
            ShowError($"{name}一键采集失败", error);
        }
        finally
        {
            _processing = false;
            SetBusy(false);
            UpdateTriggerState();
        }
    }

    private async Task TriggerAndDetectAsync()
    {
        if (_processing)
            return;
        _processing = true;
        SetBusy(true, "正在独占软触发并采集 Mono10 帧……");
        UpdateTriggerState();
        try
        {
            CapturedFrame frame = await _camera.TriggerOnceAsync(_lifetime.Token);
            string folderName = $"{frame.CapturedAt:HHmmss_fff}_F{frame.FrameId}";
            string output = Path.Combine(_outputRoot, "其他", "原始与校正", frame.CapturedAt.ToString("yyyy-MM-dd"), folderName);
            Directory.CreateDirectory(output);
            string rawPath = Path.Combine(output, "01_原始帧.tif");
            ImagePipeline.SaveMono10Tiff(frame, rawPath);

            string preprocessMode = (_background, _flat) switch
            {
                (not null, not null) => "背景+平场校正",
                (not null, null) => "背景扣除",
                (null, not null) => "平场校正",
                _ => "原始帧直检",
            };
            SetBusy(true, $"已保存原始帧，正在{preprocessMode}并检测……");
            DetectionReport report = await ImagePipeline.RunAsync(
                rawPath, _background?.Path, _flat?.Path, output, _outputRoot, frame.CapturedAt, _lifetime.Token);
            if (!report.Ok)
                throw new InvalidOperationException(report.Error ?? "检测引擎返回失败。");
            ShowResultImage(report.ResultPath);
            string match = string.IsNullOrEmpty(report.TemplateName)
                ? report.RecognitionStatus : $"{report.TemplateName}  IoU={report.TemplateScore:F3}";
            _lastResult.Text = $"{frame.CapturedAt:yyyy-MM-dd HH:mm:ss.fff}\n{report.PartType}  |  {report.Status}\n帧号：{frame.FrameId}  |  {match}";
            _lastResult.ForeColor = report.DefectCount > 0 ? Color.Firebrick : Color.FromArgb(0, 100, 55);
            SetStatus($"检测完成：{report.Status}；{preprocessMode}；结果帧：{report.ResultPath}");
            AppendLog(frame, report, report.ResultPath);
        }
        catch (OperationCanceledException)
        {
            SetStatus("操作已取消");
        }
        catch (Exception error)
        {
            ShowError("采集或检测失败", error);
        }
        finally
        {
            _processing = false;
            SetBusy(false);
            UpdateTriggerState();
        }
    }

    private bool CalibrationDimensionsMatch(out string message)
    {
        if (_background is not null && _flat is not null &&
            (_background.Width != _flat.Width || _background.Height != _flat.Height))
        {
            message = "背景图和平场图尺寸不一致。";
            return false;
        }
        if (_cameraWidth > 0)
        {
            CalibrationInfo? mismatch = new[] { _background, _flat }
                .FirstOrDefault(item => item is not null && (item.Width != _cameraWidth || item.Height != _cameraHeight));
            if (mismatch is not null)
            {
                message = $"标定图尺寸 {mismatch.Width}×{mismatch.Height} 与相机当前尺寸 {_cameraWidth}×{_cameraHeight} 不一致。";
                return false;
            }
        }
        message = "";
        return true;
    }

    private void UpdateTriggerState()
    {
        bool canCapture = _camera.IsPreviewing && !_processing;
        _saveFrameButton.Enabled = canCapture;
        _captureBackgroundButton.Enabled = canCapture;
        _captureFlatButton.Enabled = canCapture;
        _triggerButton.Enabled = canCapture && CalibrationDimensionsMatch(out _);
    }

    private void ShowResultImage(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using Image loaded = Image.FromStream(stream);
        Image clone = new Bitmap(loaded);
        _resultImage?.Dispose();
        _resultImage = clone;
        _resultView.Image = clone;
        _resultPlaceholder.Visible = false;
    }

    private void AppendLog(CapturedFrame frame, DetectionReport report, string output)
    {
        string logDirectory = Path.Combine(AppContext.BaseDirectory, "其他", "日志");
        Directory.CreateDirectory(logDirectory);
        string line = $"{DateTime.Now:O}\tSN={_camera.ConnectedCamera?.SerialNumber}\tFrame={frame.FrameId}\t{report.PartType}\t缺陷={report.DefectCount}\t{report.Status}\t{output}{Environment.NewLine}";
        File.AppendAllText(Path.Combine(logDirectory, $"{DateTime.Today:yyyy-MM-dd}.log"), line, System.Text.Encoding.UTF8);
    }

    private void PostStatus(string message)
    {
        if (IsDisposed || !IsHandleCreated)
            return;
        BeginInvoke(() => SetStatus(message));
    }

    private void PostGrayPercentile(int value)
    {
        if (IsDisposed || !IsHandleCreated)
            return;
        BeginInvoke(() => _grayP95Label.Text = $"灰度 P95（Mono10）：{value} / 1023");
    }

    private void SetStatus(string message) => _statusText.Text = message;

    private void SetBusy(bool busy, string? message = null)
    {
        _busy.Visible = busy;
        if (message is not null)
            SetStatus(message);
    }

    private void ShowError(string title, Exception error)
    {
        SetStatus($"{title}：{error.Message}");
        MessageBox.Show(this, error.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _lifetime.Cancel();
        try { _camera.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        _resultImage?.Dispose();
        _lifetime.Dispose();
        base.OnFormClosing(e);
    }

    private static GroupBox NewTopGroup(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        Padding = new Padding(10),
    };

    private static TableLayoutPanel NewGrid(int columns) => new()
    {
        Dock = DockStyle.Fill,
        AutoSize = true,
        ColumnCount = columns,
        RowCount = 8,
    };

    private GroupBox BuildViewGroup(string title, PictureBox view, Label placeholder, Control? overlay = null)
    {
        GroupBox group = new() { Text = title, Dock = DockStyle.Fill, Padding = new Padding(8) };
        Panel host = new() { BackColor = Color.FromArgb(24, 27, 31) };
        host.Controls.Add(view);
        host.Controls.Add(placeholder);
        if (overlay is not null)
        {
            overlay.Location = new Point(10, 10);
            overlay.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            host.Controls.Add(overlay);
            overlay.BringToFront();
        }
        placeholder.BringToFront();
        overlay?.BringToFront();
        group.Controls.Add(host);
        void FitAspectRatio()
        {
            int availableWidth = Math.Max(1, group.ClientSize.Width - 16);
            int availableHeight = Math.Max(1, group.ClientSize.Height - 28);
            double aspect = Math.Max(0.1, _viewAspectRatio);
            int width = availableWidth;
            int height = Math.Max(1, (int)Math.Round(width / aspect));
            if (height > availableHeight)
            {
                height = availableHeight;
                width = Math.Max(1, (int)Math.Round(height * aspect));
            }
            host.Bounds = new Rectangle(
                (group.ClientSize.Width - width) / 2,
                20 + Math.Max(0, (availableHeight - height) / 2),
                width,
                height);
        }
        _viewFitters.Add(FitAspectRatio);
        group.ClientSizeChanged += (_, _) => FitAspectRatio();
        group.HandleCreated += (_, _) => FitAspectRatio();
        return group;
    }

    private static PictureBox ImageBox(string placeholder) => new()
    {
        Dock = DockStyle.Fill,
        BackColor = Color.FromArgb(24, 27, 31),
        ForeColor = Color.Gainsboro,
        SizeMode = PictureBoxSizeMode.Zoom,
        BorderStyle = BorderStyle.FixedSingle,
        AccessibleDescription = placeholder,
    };

    private static Label Placeholder(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        BackColor = Color.FromArgb(24, 27, 31),
        ForeColor = Color.FromArgb(190, 196, 204),
        Font = new Font("Microsoft YaHei UI", 11F),
    };

    private static TextBox PathBox() => new() { ReadOnly = true, Dock = DockStyle.Fill, BackColor = Color.White };
    private static decimal ClampDecimal(double value) =>
        (decimal)Math.Clamp(value, (double)decimal.MinValue, (double)decimal.MaxValue);
}

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        int probeIndex = Array.IndexOf(args, "--probe-camera");
        if (probeIndex >= 0 && probeIndex + 1 < args.Length)
        {
            ProbeCamera(args[probeIndex + 1]);
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, eventArgs) => WriteFatal(eventArgs.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            WriteFatal(eventArgs.ExceptionObject as Exception ?? new Exception(eventArgs.ExceptionObject?.ToString()));
        try
        {
            bool templates = args.Contains("--templates") ||
                Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "模板生成器";
            Application.Run(templates ? new TemplateForm() : new MainForm());
        }
        catch (Exception error)
        {
            WriteFatal(error);
            MessageBox.Show(error.Message, "V2 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void ProbeCamera(string outputPath)
    {
        CameraController camera = new();
        try
        {
            camera.Initialize();
            var devices = camera.RefreshDevices();
            string json = System.Text.Json.JsonSerializer.Serialize(new
            {
                Ok = true,
                Count = devices.Count,
                Devices = devices,
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(outputPath, json, System.Text.Encoding.UTF8);
        }
        catch (Exception error)
        {
            string json = System.Text.Json.JsonSerializer.Serialize(new
            {
                Ok = false,
                Error = $"{error.GetType().Name}: {error.Message}",
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(outputPath, json, System.Text.Encoding.UTF8);
        }
        finally
        {
            camera.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void WriteFatal(Exception error)
    {
        try
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "其他", "日志");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, $"{DateTime.Today:yyyy-MM-dd}_致命错误.log"),
                $"{DateTime.Now:O}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}",
                System.Text.Encoding.UTF8);
        }
        catch
        {
        }
    }
}
