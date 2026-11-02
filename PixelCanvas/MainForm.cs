using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace PixelCanvas;

/// <summary>主窗口：界面布局与交互编排（工具 / 调色板 / 尺寸 / 工程 / 导出）。</summary>
public sealed class MainForm : Form
{
    private PixelGrid _grid = new(16, 16);
    private readonly History _history = new();
    private List<string> _palette;
    private int _currentColor;
    private string? _projectPath;
    private bool _dirty;

    private CanvasControl _canvas = null!;
    private Panel _canvasPanel = null!;
    private ComboBox _sizeCombo = null!;
    private Label _sizeLabel = null!;
    private NumericUpDown _brushSize = null!;
    private CheckBox _mirrorHCheck = null!, _mirrorVCheck = null!, _gridCheck = null!;
    private Button _undoButton = null!, _redoButton = null!;
    private readonly ToolTip _toolTip = new();
    private readonly Dictionary<ToolKind, Button> _toolButtons = new();
    private FlowLayoutPanel _paletteFlow = null!;
    private Panel _colorPreview = null!;
    private readonly List<Button> _swatches = new();
    private ComboBox _scaleCombo = null!;

    private static readonly int[] PresetSizes = { 8, 16, 32, 64, 128 };
    private static readonly int[] ExportScales = { 1, 2, 4, 8, 16 };

    public MainForm()
    {
        _palette = new List<string>(Palettes.Db32);
        _currentColor = Argb.FromRgb(0x000000);

        Text = TitleText();
        ClientSize = new Size(1280, 720);
        MinimumSize = new Size(960, 600);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildUi();
        RebuildPaletteUi();
        UpdateUndoButtons();
        UpdateSizeLabel();
        SetTool(ToolKind.Pencil);
    }

    private string TitleText() =>
        $"PixelCanvas - {(_projectPath is null ? "未命名" : Path.GetFileName(_projectPath))}{(_dirty ? "*" : "")}";

    private void UpdateTitle() => Text = TitleText();

    private void SetDirty()
    {
        if (_dirty) return;
        _dirty = true;
        UpdateTitle();
    }

    /// <summary>统一入口：设置当前颜色（画布 + 界面同步）。</summary>
    private void SetCurrentColor(int argb)
    {
        _currentColor = argb;
        _canvas.CurrentColor = argb;
        SyncSwatchSelection();
    }

    // ---------- 布局 ----------

    private void BuildUi()
    {
        // 中间画布（Fill 停靠必须最先加入，布局引擎最后处理它）
        _canvasPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(0x33, 0x33, 0x33),
        };
        _canvas = new CanvasControl(_grid);
        _canvasPanel.Controls.Add(_canvas);
        _canvasPanel.Resize += (_, _) => _canvas.UpdateZoom(CanvasAvailable());
        _canvas.ColorPicked += argb => { _currentColor = argb; SyncSwatchSelection(); };
        _canvas.PixelsChanged += () => SetDirty();
        _canvas.StrokeCommitted += before =>
        {
            if (_canvas.LastStrokeChanged)
            {
                _history.Commit(before);
                UpdateUndoButtons();
            }
        };

        var left = BuildLeftPanel();
        var right = BuildRightPanel();

        Controls.Add(_canvasPanel);
        Controls.Add(left);
        Controls.Add(right);
    }

    private Size CanvasAvailable() => new(
        Math.Max(1, _canvasPanel.ClientSize.Width - 2),
        Math.Max(1, _canvasPanel.ClientSize.Height - 2));

    private Button MakeButton(string text, int x, int y, int width) => new()
    {
        Text = text,
        Location = new Point(x, y),
        Size = new Size(width, 26),
    };

    private Panel BuildLeftPanel()
    {
        var panel = new Panel { Dock = DockStyle.Left, Width = 180 };
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(8, 8, 4, 8),
        };
        panel.Controls.Add(flow);

        // 画布尺寸
        var sizeGroup = new GroupBox { Text = "画布尺寸", Width = 164, Height = 74, Margin = new Padding(0, 0, 0, 6) };
        _sizeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 10, Top = 24, Width = 144 };
        foreach (int s in PresetSizes) _sizeCombo.Items.Add($"{s}×{s}");
        _sizeCombo.Items.Add("自定义…");
        _sizeCombo.SelectedIndex = 1;   // 16×16
        _sizeCombo.SelectedIndexChanged += OnSizeComboChanged;
        _sizeLabel = new Label { Text = "当前：16×16", Left = 10, Top = 50, AutoSize = true, ForeColor = Color.DimGray };
        sizeGroup.Controls.Add(_sizeCombo);
        sizeGroup.Controls.Add(_sizeLabel);
        flow.Controls.Add(sizeGroup);

        // 工具
        var toolGroup = new GroupBox { Text = "工具", Width = 164, Height = 158, Margin = new Padding(0, 0, 0, 6) };
        AddToolButton(toolGroup, ToolKind.Pencil, "画笔 (B)", 0);
        AddToolButton(toolGroup, ToolKind.Eraser, "橡皮 (E)", 1);
        AddToolButton(toolGroup, ToolKind.Eyedropper, "取色器 (I)", 2);
        AddToolButton(toolGroup, ToolKind.Fill, "油漆桶 (G)", 3);
        flow.Controls.Add(toolGroup);

        // 画笔大小
        var brushGroup = new GroupBox { Text = "画笔大小", Width = 164, Height = 62, Margin = new Padding(0, 0, 0, 6) };
        _brushSize = new NumericUpDown { Left = 8, Top = 24, Width = 44, Minimum = 1, Maximum = 4, Value = 1 };
        _brushSize.ValueChanged += (_, _) =>
        {
            _canvas.BrushSize = (int)_brushSize.Value;
            _canvas.Invalidate();   // 刷新悬停高亮
        };
        var brushTip = new Label { Text = "1–4（左上角）", Left = 52, Top = 27, AutoSize = true, ForeColor = Color.DimGray };
        brushGroup.Controls.Add(_brushSize);
        brushGroup.Controls.Add(brushTip);
        flow.Controls.Add(brushGroup);

        // 对称与显示
        var mirrorGroup = new GroupBox { Text = "对称 / 显示", Width = 164, Height = 108, Margin = new Padding(0, 0, 0, 6) };
        _mirrorHCheck = new CheckBox { Text = "水平镜像", Left = 10, Top = 22, AutoSize = true };
        _mirrorVCheck = new CheckBox { Text = "垂直镜像", Left = 10, Top = 46, AutoSize = true };
        _gridCheck = new CheckBox { Text = "网格线", Left = 10, Top = 70, AutoSize = true, Checked = true };
        _mirrorHCheck.CheckedChanged += (_, _) => _canvas.MirrorH = _mirrorHCheck.Checked;
        _mirrorVCheck.CheckedChanged += (_, _) => _canvas.MirrorV = _mirrorVCheck.Checked;
        _gridCheck.CheckedChanged += (_, _) => { _canvas.ShowGrid = _gridCheck.Checked; _canvas.Invalidate(); };
        mirrorGroup.Controls.AddRange(new Control[] { _mirrorHCheck, _mirrorVCheck, _gridCheck });
        flow.Controls.Add(mirrorGroup);

        // 编辑
        var editGroup = new GroupBox { Text = "编辑", Width = 164, Height = 100, Margin = new Padding(0, 0, 0, 6) };
        _undoButton = MakeButton("撤销", 10, 22, 68);
        _redoButton = MakeButton("重做", 84, 22, 68);
        _toolTip.SetToolTip(_undoButton, "撤销 (Ctrl+Z)");
        _toolTip.SetToolTip(_redoButton, "重做 (Ctrl+Y)");
        _undoButton.Click += (_, _) => DoUndo();
        _redoButton.Click += (_, _) => DoRedo();
        var clearButton = MakeButton("清空画布", 10, 56, 142);
        clearButton.Click += (_, _) => ClearCanvas();
        editGroup.Controls.Add(_undoButton);
        editGroup.Controls.Add(_redoButton);
        editGroup.Controls.Add(clearButton);
        flow.Controls.Add(editGroup);

        // 工程
        var fileGroup = new GroupBox { Text = "工程", Width = 164, Height = 100, Margin = new Padding(0, 0, 0, 6) };
        var saveButton = MakeButton("保存工程 (Ctrl+S)", 10, 22, 142);
        var openButton = MakeButton("打开工程 (Ctrl+O)", 10, 56, 142);
        saveButton.Click += (_, _) => SaveProject();
        openButton.Click += (_, _) => OpenProject();
        fileGroup.Controls.Add(saveButton);
        fileGroup.Controls.Add(openButton);
        flow.Controls.Add(fileGroup);

        return panel;
    }

    private void AddToolButton(GroupBox group, ToolKind tool, string text, int row)
    {
        var b = new Button
        {
            Text = text,
            Location = new Point(10, 20 + row * 32),
            Size = new Size(144, 26),
            TextAlign = ContentAlignment.MiddleCenter,
        };
        b.Click += (_, _) => SetTool(tool);
        _toolButtons[tool] = b;
        group.Controls.Add(b);
    }

    private void SetTool(ToolKind tool)
    {
        _canvas.Tool = tool;
        foreach (var (kind, button) in _toolButtons)
        {
            bool active = kind == tool;
            button.BackColor = active ? Color.FromArgb(0, 120, 215) : SystemColors.Control;
            button.ForeColor = active ? Color.White : SystemColors.ControlText;
        }
    }

    private Panel BuildRightPanel()
    {
        var panel = new Panel { Dock = DockStyle.Right, Width = 208 };
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(4, 8, 8, 8),
        };
        panel.Controls.Add(flow);

        // 调色板
        var paletteGroup = new GroupBox { Text = "调色板", Width = 192, Height = 252, Margin = new Padding(0, 0, 0, 6) };
        _paletteFlow = new FlowLayoutPanel
        {
            Left = 8, Top = 20, Width = 176, Height = 150,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
        };
        var customButton = MakeButton("自定义颜色…", 8, 176, 176);
        customButton.Click += (_, _) => AddCustomColor();
        var previewLabel = new Label { Text = "当前颜色：", Left = 8, Top = 206, AutoSize = true };
        _colorPreview = new Panel { Left = 8, Top = 226, Size = new Size(176, 20), BorderStyle = BorderStyle.FixedSingle };
        paletteGroup.Controls.Add(_paletteFlow);
        paletteGroup.Controls.Add(customButton);
        paletteGroup.Controls.Add(previewLabel);
        paletteGroup.Controls.Add(_colorPreview);
        flow.Controls.Add(paletteGroup);

        // 导出
        var exportGroup = new GroupBox { Text = "导出 PNG", Width = 192, Height = 96, Margin = new Padding(0, 0, 0, 6) };
        var scaleLabel = new Label { Text = "放大倍数：", Left = 8, Top = 18, AutoSize = true };
        _scaleCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 8, Top = 34, Width = 176 };
        foreach (int s in ExportScales) _scaleCombo.Items.Add($"{s}×");
        _scaleCombo.SelectedIndex = 3;   // 默认 8×
        var exportButton = MakeButton("导出 PNG…", 8, 62, 170);
        exportButton.Click += (_, _) => ExportPng();
        exportGroup.Controls.Add(scaleLabel);
        exportGroup.Controls.Add(_scaleCombo);
        exportGroup.Controls.Add(exportButton);
        flow.Controls.Add(exportGroup);

        return panel;
    }

    // ---------- 调色板 ----------

    private void RebuildPaletteUi()
    {
        _paletteFlow.SuspendLayout();
        _paletteFlow.Controls.Clear();
        _swatches.Clear();
        foreach (string hex in _palette)
        {
            var swatch = new Button
            {
                Size = new Size(24, 24),
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(1),
                BackColor = Color.FromArgb(Argb.FromRgbHex(hex)),
                Tag = hex,
            };
            swatch.FlatAppearance.BorderSize = 1;
            swatch.Click += SwatchClick;
            _paletteFlow.Controls.Add(swatch);
            _swatches.Add(swatch);
        }
        _paletteFlow.ResumeLayout();
        SyncSwatchSelection();
    }

    private void SwatchClick(object? sender, EventArgs e)
    {
        if (sender is Button { Tag: string hex })
            SetCurrentColor(Argb.FromRgbHex(hex));
    }

    /// <summary>同步色块高亮与当前颜色预览（取色到的颜色可能不在调色板中，则不高亮任何色块）。</summary>
    private void SyncSwatchSelection()
    {
        string currentHex = Argb.ToRgbHex(_currentColor);
        for (int i = 0; i < _swatches.Count; i++)
        {
            var swatch = _swatches[i];
            bool selected = swatch.Tag is string tagHex && string.Equals(tagHex, currentHex, StringComparison.OrdinalIgnoreCase);
            swatch.FlatAppearance.BorderColor = selected ? Color.FromArgb(0, 120, 215) : Color.FromArgb(150, 150, 150);
            swatch.FlatAppearance.BorderSize = selected ? 3 : 1;
        }
        // 预览：0（透明）显示为白色底，其余直接显示当前颜色
        _colorPreview.BackColor = _currentColor == 0 ? Color.White : Color.FromArgb(_currentColor);
    }

    private void AddCustomColor()
    {
        using var dlg = new ColorDialog
        {
            FullOpen = true,
            Color = Color.FromArgb(Argb.FromRgb(Convert.ToInt32(Argb.ToRgbHex(_currentColor), 16))),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        string hex = $"{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
        int idx = _palette.FindIndex(p => string.Equals(p, hex, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            _palette.Add(hex);       // 追加到调色板末尾
            RebuildPaletteUi();
            idx = _palette.Count - 1;
        }
        SetCurrentColor(Argb.FromRgbHex(hex));
        SetDirty();                  // 调色板属于工程内容
    }

    // ---------- 画布尺寸 ----------

    private void OnSizeComboChanged(object? sender, EventArgs e)
    {
        int index = _sizeCombo.SelectedIndex;
        if (index < 0) return;

        if (index < PresetSizes.Length)
        {
            int s = PresetSizes[index];
            if (s == _grid.Width && s == _grid.Height) return;
            if (!ChangeSize(s, s))
                RestoreSizeSelection();          // 用户取消：恢复下拉框到当前实际尺寸
        }
        else
        {
            using var dlg = new SizeDialog(_grid.Width, _grid.Height);
            if (dlg.ShowDialog(this) == DialogResult.OK)
                ChangeSize(dlg.ValueWidth, dlg.ValueHeight);
            RestoreSizeSelection();
        }
    }

    /// <summary>切换画布尺寸（确认 → 清空画布与撤销历史）。返回 false 表示用户取消。</summary>
    private bool ChangeSize(int w, int h)
    {
        if (w == _grid.Width && h == _grid.Height) return true;
        var r = MessageBox.Show(this,
            $"切换为 {w}×{h} 将清空当前画布并清空撤销历史，确定继续吗？",
            "切换画布尺寸", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (r != DialogResult.OK) return false;

        _grid = new PixelGrid(w, h);
        _history.Clear();
        _canvas.SetGrid(_grid);
        _canvas.UpdateZoom(CanvasAvailable());
        UpdateUndoButtons();
        UpdateSizeLabel();
        SetDirty();
        return true;
    }

    /// <summary>让尺寸下拉框反映当前实际尺寸（预设方形则选中，否则清空选择）。</summary>
    private void RestoreSizeSelection()
    {
        int idx = Array.IndexOf(PresetSizes, _grid.Width);
        _sizeCombo.SelectedIndex = (_grid.Height == _grid.Width && idx >= 0) ? idx : -1;
        UpdateSizeLabel();
    }

    private void UpdateSizeLabel() => _sizeLabel.Text = $"当前：{_grid.Width}×{_grid.Height}";

    /// <summary>自定义尺寸对话框：宽/高文本框 + 校验（4–256，非法弹错误且不应用）。</summary>
    private sealed class SizeDialog : Form
    {
        private readonly TextBox _w = new();
        private readonly TextBox _h = new();
        public int ValueWidth { get; private set; }
        public int ValueHeight { get; private set; }

        public SizeDialog(int curW, int curH)
        {
            Text = "自定义画布尺寸";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = false;
            ClientSize = new Size(300, 130);
            Font = new Font("Microsoft YaHei UI", 9F);

            var lblW = new Label { Text = "宽（4–256）：", Location = new Point(16, 18), AutoSize = true };
            _w.SetBounds(120, 14, 140, 23);
            _w.Text = curW.ToString();
            var lblH = new Label { Text = "高（4–256）：", Location = new Point(16, 52), AutoSize = true };
            _h.SetBounds(120, 48, 140, 23);
            _h.Text = curH.ToString();
            var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(120, 88), Size = new Size(70, 26) };
            ok.Click += OnOk;
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(200, 88), Size = new Size(70, 26) };
            Controls.AddRange(new Control[] { lblW, _w, lblH, _h, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void OnOk(object? sender, EventArgs e)
        {
            if (!int.TryParse(_w.Text.Trim(), out int w) || !int.TryParse(_h.Text.Trim(), out int h)
                || w < 4 || w > 256 || h < 4 || h > 256)
            {
                MessageBox.Show(this, "宽和高必须是 4–256 之间的整数。", "输入有误",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;   // 校验不通过：不关闭对话框
                return;
            }
            ValueWidth = w;
            ValueHeight = h;
        }
    }

    // ---------- 工具动作 ----------

    private void DoUndo()
    {
        if (!_history.CanUndo) return;
        int[] current = _grid.Snapshot();
        int[] before = _history.Undo(current);
        _grid.Restore(before);
        _canvas.RefreshFromData();
        UpdateUndoButtons();
        SetDirty();
    }

    private void DoRedo()
    {
        if (!_history.CanRedo) return;
        int[] current = _grid.Snapshot();
        int[] next = _history.Redo(current);
        _grid.Restore(next);
        _canvas.RefreshFromData();
        UpdateUndoButtons();
        SetDirty();
    }

    private void UpdateUndoButtons()
    {
        _undoButton.Enabled = _history.CanUndo;
        _redoButton.Enabled = _history.CanRedo;
    }

    private void ClearCanvas()
    {
        if (MessageBox.Show(this, "确定要清空整个画布吗？（清空后可通过撤销恢复）", "清空画布",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        if (!HasAnyPixel(_grid)) return;         // 已是空画布：不产生历史步骤
        int[] before = _grid.Snapshot();
        _grid.Clear();
        _canvas.RefreshFromData();
        _history.Commit(before);
        UpdateUndoButtons();
        SetDirty();
    }

    private static bool HasAnyPixel(PixelGrid grid)
    {
        foreach (int p in grid.RawPixels)
            if (p != 0) return true;
        return false;
    }

    // ---------- 工程保存 / 载入 ----------

    private void SaveProject()
    {
        if (!EnsureProjectPath()) return;
        if (!TryWriteProject(_projectPath!)) return;
        _dirty = false;
        UpdateTitle();
    }

    /// <summary>确保已有工程路径；没有则弹出保存对话框并记录（用户取消返回 false）。</summary>
    private bool EnsureProjectPath()
    {
        if (_projectPath != null) return true;
        using var dlg = new SaveFileDialog
        {
            Filter = "像素画布工程 (*.pxproj)|*.pxproj|所有文件 (*.*)|*.*",
            FileName = "未命名.pxproj",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return false;
        _projectPath = dlg.FileName;
        return true;
    }

    /// <summary>写入工程文件；失败弹错误提示并返回 false（保留路径便于重试）。</summary>
    private bool TryWriteProject(string path)
    {
        try
        {
            ProjectFile.Save(path, _grid, _palette);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存失败：" + ex.Message, "保存工程",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void OpenProject()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "像素画布工程 (*.pxproj)|*.pxproj|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var result = ProjectFile.TryLoad(dlg.FileName, out string error);
        if (result is null)
        {
            MessageBox.Show(this, "无法打开工程：" + error, "打开工程",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;                              // 校验失败：当前画布保持不变
        }

        _grid = result.Grid;
        _palette = result.Palette;
        _history.Clear();
        _projectPath = dlg.FileName;
        _dirty = false;

        _canvas.SetGrid(_grid);
        _canvas.UpdateZoom(CanvasAvailable());
        SetCurrentColor(Argb.FromRgbHex(_palette[0]));
        RebuildPaletteUi();
        RestoreSizeSelection();
        UpdateUndoButtons();
        UpdateTitle();
    }

    // ---------- 导出 PNG ----------

    private void ExportPng()
    {
        int scale = _scaleCombo.SelectedIndex >= 0 ? ExportScales[_scaleCombo.SelectedIndex] : 1;
        using var dlg = new SaveFileDialog
        {
            Filter = "PNG 图片 (*.png)|*.png",
            FileName = $"pixel-art-{_grid.Width}x{_grid.Height}.png",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            using var bmp = new Bitmap(_grid.Width * scale, _grid.Height * scale, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;   // 最近邻放大
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(_canvas.NativeBitmap,
                    new Rectangle(0, 0, bmp.Width, bmp.Height),
                    new Rectangle(0, 0, _grid.Width, _grid.Height),
                    GraphicsUnit.Pixel);
            }
            bmp.Save(dlg.FileName, ImageFormat.Png);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导出失败：" + ex.Message, "导出 PNG",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------- 快捷键 / 关闭 ----------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (ActiveControl is TextBoxBase or NumericUpDown or ComboBox) return;   // 文本输入时不抢按键

        if (e.Control && !e.Shift && e.KeyCode == Keys.Z) { DoUndo(); e.Handled = true; return; }
        if (e.Control && e.Shift && e.KeyCode == Keys.Z) { DoRedo(); e.Handled = true; return; }
        if (e.Control && e.KeyCode == Keys.Y) { DoRedo(); e.Handled = true; return; }
        if (e.Control && e.KeyCode == Keys.S) { SaveProject(); e.Handled = true; return; }
        if (e.Control && e.KeyCode == Keys.O) { OpenProject(); e.Handled = true; return; }
        if (e.Control || e.Alt) return;

        switch (e.KeyCode)
        {
            case Keys.B: SetTool(ToolKind.Pencil); break;
            case Keys.E: SetTool(ToolKind.Eraser); break;
            case Keys.I: SetTool(ToolKind.Eyedropper); break;
            case Keys.G: SetTool(ToolKind.Fill); break;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel || !_dirty) return;

        var choice = MessageBox.Show(this, "当前工程有未保存的修改，是否保存？", "关闭 PixelCanvas",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        if (choice == DialogResult.Cancel)
        {
            e.Cancel = true;
        }
        else if (choice == DialogResult.Yes)
        {
            if (!EnsureProjectPath()) { e.Cancel = true; return; }   // 用户在保存对话框取消
            if (!TryWriteProject(_projectPath!)) e.Cancel = true;    // 保存失败：不丢失数据
            else _dirty = false;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _canvas.UpdateZoom(CanvasAvailable());
    }
}
