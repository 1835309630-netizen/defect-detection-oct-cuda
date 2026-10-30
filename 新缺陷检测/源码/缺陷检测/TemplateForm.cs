namespace 缺陷检测生产端;

internal sealed class TemplateForm : Form
{
    private readonly Button _select = new() { Text = "选择同一零件的多张图片", AutoSize = true };
    private readonly Button _export = new() { Text = "生成并导出 PNG", AutoSize = true, Enabled = false };
    private readonly ListBox _files = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    private readonly PictureBox _preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    private readonly Label _status = new() { Text = "尚未选择图片", Dock = DockStyle.Fill, AutoSize = true };
    private readonly CancellationTokenSource _lifetime = new();
    private bool _running;

    public TemplateForm()
    {
        Text = "轮廓模板生成器";
        Font = new Font("Microsoft YaHei UI", 10);
        ClientSize = new Size(880, 600);
        MinimumSize = new Size(720, 500);
        StartPosition = FormStartPosition.CenterScreen;
        TableLayoutPanel root = new() { Dock = DockStyle.Fill, Padding = new Padding(16), RowCount = 4, ColumnCount = 1 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        root.Controls.Add(new Label { Text = "选择同一零件、相同分辨率和倍率的图片，建议 3～9 张。零件应完整入镜，背景较暗。\n程序对齐轮廓后合成一张模板；原始图片保持不变。", Dock = DockStyle.Fill }, 0, 0);
        FlowLayoutPanel actions = new() { Dock = DockStyle.Fill };
        actions.Controls.AddRange([_select, _export]);
        root.Controls.Add(actions, 0, 1);
        TableLayoutPanel body = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        body.Controls.Add(_files, 0, 0);
        body.Controls.Add(_preview, 1, 0);
        root.Controls.Add(body, 0, 2);
        root.Controls.Add(_status, 0, 3);
        Controls.Add(root);
        _select.Click += (_, _) => SelectImages();
        _export.Click += async (_, _) => await ExportAsync();
        FormClosing += (_, e) =>
        {
            _lifetime.Cancel();
            if (_running)
            {
                e.Cancel = true;
                _status.Text = "正在取消生成……";
            }
        };
        FormClosed += (_, _) => { _preview.Image?.Dispose(); _lifetime.Dispose(); };
    }

    private void SelectImages()
    {
        using OpenFileDialog dialog = new() { Title = "选择同一零件的不同图片", Multiselect = true,
            Filter = "图片|*.tif;*.tiff;*.png;*.jpg;*.jpeg;*.bmp" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _files.Items.Clear();
        _files.Items.AddRange(dialog.FileNames);
        _export.Enabled = _files.Items.Count > 0;
        _preview.Image?.Dispose();
        _preview.Image = null;
        _status.Text = $"已选择 {_files.Items.Count} 张。导出后将 PNG 复制到检测程序的“算法引擎\\模板”，下一次检测生效。";
    }

    private async Task ExportAsync()
    {
        using SaveFileDialog dialog = new() { Title = "导出轮廓模板", Filter = "轮廓模板 PNG|*.png",
            DefaultExt = "png", AddExtension = true, FileName = "001.png", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _running = true;
        _select.Enabled = _export.Enabled = false;
        _status.Text = "正在分割、对齐并合成模板，请稍候……";
        try
        {
            TemplateReport report = await ImagePipeline.BuildTemplateAsync(
                _files.Items.Cast<string>().ToArray(), dialog.FileName, _lifetime.Token);
            using Image image = Image.FromFile(report.OutputPath);
            _preview.Image?.Dispose();
            _preview.Image = new Bitmap(image);
            _status.Text = $"已导出 {report.ImageCount} 张合成的模板，最低对齐 IoU：{report.MinimumScore:F3}\n{report.OutputPath}\n复制到“算法引擎\\模板”并命名为 001.png、002.png…即可参与分类匹配。";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { MessageBox.Show(this, error.Message, "生成失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally
        {
            _running = false;
            if (_lifetime.IsCancellationRequested) Close();
            else _select.Enabled = _export.Enabled = true;
        }
    }
}
