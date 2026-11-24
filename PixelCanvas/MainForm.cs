using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace PixelCanvas;

/// <summary>主窗口：界面布局与交互编排（工具 / 调色板 / 尺寸 / 工程 / 导出）。</summary>
public sealed partial class MainForm : Form
{
    // ---------- 界面控件字段 ----------
    private ListBox _layerList = null!;

    private CanvasControl _canvas = null!;
    private Panel _canvasPanel = null!;
    private Panel _leftSidebar = null!, _rightSidebar = null!;
    private Control _leftHandle = null!, _rightHandle = null!, _commandBar = null!, _statusBar = null!;
    private CheckBox _referenceVisible = null!;
    private NumericUpDown _referenceOpacity = null!;
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
    private ComboBox _placementCombo = null!;
    private Label? _statusText, _statusHint;
    private Panel _gradientPreview = null!;

    private static readonly int[] PresetSizes = { 8, 16, 32, 64, 128, 256, 512 };
    private static readonly int[] ExportScales = { 1, 2, 4, 8, 16 };

    public MainForm(int width = 32, int height = 32)
    {
        _grid = new PixelGrid(width, height);
        _palette = new List<string>(Palettes.Db32);
        _currentColor = Argb.FromRgb(0x000000);
        _layers.Add(new Layer("图层 1", _grid));
        _layers[0].SetHistoryBudget(_historyBudget);
        _frames.Add(new AnimationFrame(_layers));

        Text = TitleText();
        ClientSize = new Size(1280, 720);
        MinimumSize = new Size(960, 600);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        try
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch { /* 图标提取失败时保留默认窗体图标 */ }
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.FromArgb(31, 35, 43);
        ForeColor = Color.FromArgb(224, 229, 237);

        BuildUi();
        RefreshLayerList();
        RebuildPaletteUi();
        UpdateUndoButtons();
        UpdateSizeLabel();
        SetTool(ToolKind.Pencil);
        _autosaveTimer.Tick += (_, _) => SaveRecovery();
    }

    private string TitleText() =>
        $"PixelCanvas - {(_projectPath is null ? "未命名" : Path.GetFileName(_projectPath))}{(_dirty ? "*" : "")}";

    private void UpdateTitle() => Text = TitleText();

    private void SetDirty()
    {
        _timeline?.InvalidateSelected();
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
        _gradientPreview?.Invalidate();
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
            BackColor = Color.FromArgb(25, 28, 34),
        };
        _canvas = new CanvasControl(_grid);
        _canvas.Layers = _layers;
        _canvasPanel.Controls.Add(_canvas);
        _canvasPanel.Resize += (_, _) =>
        {
            if (!_manualZoom) _canvas.UpdateZoom(CanvasAvailable());
            UpdateCanvasPlacement();
        };
        _canvas.SizeChanged += (_, _) => UpdateCanvasPlacement();
        _canvas.MouseEnter += (_, _) => _canvas.Focus();
        _canvas.MouseWheel += CanvasMouseWheel;
        _canvas.ZoomChanged += UpdateStatus;
        _canvas.MouseMove += (_, e) =>
        {
            if (_statusHint is null) return;
            int x = Math.Clamp(e.X / _canvas.Zoom, 0, _grid.Width - 1);
            int y = Math.Clamp(e.Y / _canvas.Zoom, 0, _grid.Height - 1);
            _statusHint.Text = $"X {x}   Y {y}     ·     滚轮缩放  /  中键平移";
        };
        _canvas.MouseLeave += (_, _) => { if (_statusHint is not null) _statusHint.Text = "滚轮缩放  /  中键平移"; };
        _toolTip.SetToolTip(_canvasPanel, "在画布上滚动鼠标滚轮可缩放；放大后可使用滚动条移动画布。");
        _canvas.ColorPicked += SetCurrentColor;   // 统一入口：画布 / 调色板 / 预览同步
        _canvas.PixelsChanged += () => SetDirty();
        _canvas.StrokeCommitted += before =>
        {
            if (_canvas.LastStrokeChanged)
            {
                _history.Commit(before);
                UpdateUndoButtons();
            }
        };

        var left = _leftSidebar = BuildLeftPanel();
        var right = _rightSidebar = BuildRightPanel();

        // 控件采用固定坐标布局，给文字和系统控件保留足够空间。
        ExpandSidebar(left, 1.5f);
        ExpandSidebar(right, 1.5f);
        StyleSidebar(left);
        StyleSidebar(right);

        Controls.Add(_canvasPanel);
        Controls.Add(_leftHandle = BuildSidebarHandle(_leftSidebar, true));
        Controls.Add(left);
        Controls.Add(_rightHandle = BuildSidebarHandle(_rightSidebar, false));
        Controls.Add(right);
        Controls.Add(_statusBar = BuildStatusBar());
        Controls.Add(_animationBar = BuildAnimationBar());
        Controls.Add(_commandBar = BuildCommandBar());
    }

    private Control BuildStatusBar()
    {
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 30, BackColor = Color.FromArgb(30, 34, 43), Padding = new Padding(12, 0, 12, 0) };
        _statusHint = new Label { Dock = DockStyle.Right, Width = 270, Text = "滚轮缩放  /  中键平移",
            TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.FromArgb(150, 165, 184) };
        _statusText = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(205, 218, 234), AutoEllipsis = true };
        bar.Controls.Add(_statusText);
        bar.Controls.Add(_statusHint);
        return bar;
    }

    private void UpdateStatus()
    {
        if (_statusText is null || _canvas is null || _layers.Count == 0) return;
        string tool = _canvas.Tool switch
        {
            ToolKind.Pencil => "画笔", ToolKind.Eraser => "橡皮", ToolKind.Eyedropper => "取色",
            ToolKind.Fill => "填充", ToolKind.Line => "直线", ToolKind.Rectangle => "矩形",
            ToolKind.Ellipse => "椭圆", ToolKind.Selection => "选区", ToolKind.Gradient => "渐变", _ => "工具"
        };
        _statusText.Text = $"■  {tool}     ·     {_grid.Width} × {_grid.Height} PX     ·     {_canvas.Zoom}×     ·     {_layers[_activeLayer].Name}";
    }

    private Control BuildSidebarHandle(Panel sidebar, bool isLeft)
    {
        var handle = new Panel
        {
            Dock = isLeft ? DockStyle.Left : DockStyle.Right,
            Width = 24,
            BackColor = Color.FromArgb(31, 35, 43),
        };
        var button = new Button { Size = new Size(24, 54), TabStop = true };
        StyleButton(button);
        button.FlatAppearance.BorderSize = 0;
        void UpdateArrow()
        {
            button.Text = isLeft ? (sidebar.Visible ? "‹" : "›") : (sidebar.Visible ? "›" : "‹");
            string side = isLeft ? "左侧栏" : "右侧栏";
            _toolTip.SetToolTip(button, (sidebar.Visible ? "隐藏" : "显示") + side);
            button.AccessibleName = (sidebar.Visible ? "隐藏" : "显示") + side;
        }
        button.Click += (_, _) => { sidebar.Visible = !sidebar.Visible; UpdateArrow(); };
        sidebar.VisibleChanged += (_, _) => UpdateArrow();
        handle.Resize += (_, _) => button.Location = new Point(0, Math.Max(0, (handle.ClientSize.Height - button.Height) / 2));
        handle.Controls.Add(button);
        UpdateArrow();
        return handle;
    }

    private Control BuildCommandBar()
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 52, BackColor = Color.FromArgb(38, 43, 53),
            Padding = new Padding(10, 7, 8, 5), WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
        };
        var brand = new Label { Text = "▦  PIXEL", AutoSize = false, Width = 115, Height = 32,
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(129, 201, 239),
            Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 12, 0) };
        bar.Controls.Add(brand);
        Button AddCommand(string text, string tip, Action action, int width = 72)
        {
            var button = new Button { Text = text, Width = width, Height = 32, Margin = new Padding(0, 0, 6, 0) };
            StyleButton(button);
            _toolTip.SetToolTip(button, tip);
            button.Click += (_, _) => action();
            bar.Controls.Add(button);
            return button;
        }
        AddCommand("打开", "打开工程（Ctrl+O）", OpenProject);
        AddCommand("保存", "保存工程（Ctrl+S）", SaveProject);
        AddCommand("另存", "另存为（Ctrl+Shift+S）", SaveProjectAs);
        _undoButton = AddCommand("撤销", "撤销当前图层（Ctrl+Z）", DoUndo);
        _redoButton = AddCommand("重做", "重做当前图层（Ctrl+Y）", DoRedo);
        AddCommand("适应", "将画布适应窗口", FitCanvas);
        AddCommand("导出", "导出 PNG 或 GIF 动画", ExportPng);
        AddCommand("沉浸", "沉浸模式（F11）；快捷键帮助（F1）", ToggleImmersive);
        var placementLabel = new Label { Text = "画布位置", AutoSize = false, Width = 90, Height = 32,
            TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.FromArgb(205, 215, 228),
            Margin = new Padding(8, 0, 4, 0) };
        var placement = _placementCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 92,
            BackColor = Color.FromArgb(49, 55, 66), ForeColor = Color.FromArgb(235, 239, 245),
            Margin = new Padding(0, 4, 0, 0), AccessibleName = "画布位置" };
        placement.Items.AddRange(new object[] { "左上", "居中" });
        placement.SelectedIndex = 0;
        placement.SelectedIndexChanged += (_, _) =>
        {
            _centerCanvas = placement.SelectedIndex == 1;
            UpdateCanvasPlacement();
        };
        _toolTip.SetToolTip(placement, "设置画布较小时在工作区中的位置。放大后仍可滚动和平移。");
        bar.Controls.Add(placementLabel);
        bar.Controls.Add(placement);
        return bar;
    }

    private void UpdateCanvasPlacement()
    {
        if (_canvasPanel is null || _canvas is null) return;
        Point scroll = _canvasPanel.AutoScrollPosition;
        int x = _centerCanvas ? Math.Max(0, (_canvasPanel.ClientSize.Width - _canvas.Width) / 2) : 0;
        int y = _centerCanvas ? Math.Max(0, (_canvasPanel.ClientSize.Height - _canvas.Height) / 2) : 0;
        var location = new Point(x + scroll.X, y + scroll.Y);
        if (_canvas.Location != location) _canvas.Location = location;
    }

    private static void StyleButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Color.FromArgb(74, 82, 96);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(70, 80, 96);
        button.BackColor = Color.FromArgb(53, 60, 72);
        button.ForeColor = Color.FromArgb(231, 235, 241);
        button.UseVisualStyleBackColor = false;
    }

    private static void StyleSidebar(Control control)
    {
        control.BackColor = Color.FromArgb(38, 43, 53);
        control.ForeColor = Color.FromArgb(224, 229, 237);
        if (control is Button button) StyleButton(button);
        else if (control is TextBoxBase or ComboBox or NumericUpDown or ListBox)
        {
            control.BackColor = Color.FromArgb(49, 55, 66);
            control.ForeColor = Color.FromArgb(235, 239, 245);
        }
        else if (control is Label label && label.ForeColor == Color.DimGray)
            label.ForeColor = Color.FromArgb(172, 183, 197);
        foreach (Control child in control.Controls) StyleSidebar(child);
    }

    private static void ExpandSidebar(Control control, float factor)
    {
        static int Scale(int value, float factor) => (int)Math.Round(value * factor);
        control.Bounds = new Rectangle(Scale(control.Left, factor), Scale(control.Top, factor),
            Scale(control.Width, factor), Scale(control.Height, factor));
        var margin = control.Margin;
        control.Margin = new Padding(Scale(margin.Left, factor), Scale(margin.Top, factor),
            Scale(margin.Right, factor), Scale(margin.Bottom, factor));
        var padding = control.Padding;
        control.Padding = new Padding(Scale(padding.Left, factor), Scale(padding.Top, factor),
            Scale(padding.Right, factor), Scale(padding.Bottom, factor));
        foreach (Control child in control.Controls) ExpandSidebar(child, factor);
    }

    private Size CanvasAvailable() => new(
        Math.Max(1, _canvasPanel.ClientSize.Width - 2),
        Math.Max(1, _canvasPanel.ClientSize.Height - 2));

    private void CanvasMouseWheel(object? sender, MouseEventArgs e)
    {
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
        if (e.Delta == 0 || e.Button != MouseButtons.None) return;
        int oldZoom = _canvas.Zoom;
        int step = Math.Max(1, oldZoom / 8);
        int newZoom = Math.Clamp(oldZoom + Math.Sign(e.Delta) * step, 1, 128);
        if (newZoom == oldZoom) return;

        Point viewportPoint = _canvasPanel.PointToClient(_canvas.PointToScreen(e.Location));
        double canvasX = (double)e.X / oldZoom;
        double canvasY = (double)e.Y / oldZoom;
        _manualZoom = true;
        _canvas.SetZoom(newZoom);
        _canvasPanel.AutoScrollPosition = new Point(
            Math.Max(0, (int)Math.Round(canvasX * newZoom - viewportPoint.X)),
            Math.Max(0, (int)Math.Round(canvasY * newZoom - viewportPoint.Y)));
    }

    private Button MakeButton(string text, int x, int y, int width) => new()
    {
        Text = text,
        Location = new Point(x, y),
        Size = new Size(width, 26),
    };

    private Panel BuildLeftPanel()
    {
        var panel = new Panel { Dock = DockStyle.Left, Width = 194 };
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(8, 8, 4, 8),
        };
        panel.Controls.Add(flow);

        // 画布尺寸
        var sizeGroup = new GroupBox { Text = "画布尺寸", Width = 164, Height = 74, Margin = new Padding(0, 0, 0, 6) };
        _sizeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 10, Top = 24, Width = 144 };
        foreach (int s in PresetSizes) _sizeCombo.Items.Add($"{s}×{s}");
        _sizeCombo.Items.Add("自定义…");
        _toolTip.SetToolTip(_sizeCombo, "选择画布尺寸；更改尺寸会清空画布和撤销历史。");
        _sizeCombo.SelectedIndex = 2;   // 32×32
        _sizeCombo.SelectedIndexChanged += OnSizeComboChanged;
        _sizeLabel = new Label { Text = "当前：32×32", Left = 10, Top = 50, AutoSize = true, ForeColor = Color.DimGray };
        sizeGroup.Controls.Add(_sizeCombo);
        sizeGroup.Controls.Add(_sizeLabel);
        flow.Controls.Add(sizeGroup);

        // 工具
        var toolGroup = new GroupBox { Text = "工具", Width = 164, Height = 190, Margin = new Padding(0, 0, 0, 6) };
        AddToolButton(toolGroup, ToolKind.Pencil, "画笔", 0);
        AddToolButton(toolGroup, ToolKind.Eraser, "橡皮", 1);
        AddToolButton(toolGroup, ToolKind.Eyedropper, "取色", 2);
        AddToolButton(toolGroup, ToolKind.Fill, "填充", 3);
        AddToolButton(toolGroup, ToolKind.Line, "直线", 4);
        AddToolButton(toolGroup, ToolKind.Rectangle, "矩形", 5);
        AddToolButton(toolGroup, ToolKind.Ellipse, "椭圆", 6);
        AddToolButton(toolGroup, ToolKind.Selection, "选区", 7);
        AddToolButton(toolGroup, ToolKind.Gradient, "渐变", 8);
        flow.Controls.Add(toolGroup);

        var gradientGroup = new GroupBox { Text = "渐变颜色", Width = 164, Height = 92, Margin = new Padding(0, 0, 0, 6) };
        _gradientPreview = new Panel { Left = 10, Top = 22, Width = 142, Height = 18, BorderStyle = BorderStyle.FixedSingle };
        _gradientPreview.Paint += (_, e) =>
        {
            var rect = _gradientPreview.ClientRectangle;
            if (rect.Width < 1 || rect.Height < 1) return;
            using var brush = new LinearGradientBrush(rect, Color.FromArgb(_currentColor), Color.FromArgb(_canvas.GradientEndColor), 0f);
            e.Graphics.FillRectangle(brush, rect);
        };
        var endColor = MakeButton("结束色…", 10, 50, 68);
        var swapColors = MakeButton("交换颜色", 84, 50, 68);
        _toolTip.SetToolTip(endColor, "选择渐变结束色（Ctrl+Shift+G）；起始色为当前画笔颜色。");
        _toolTip.SetToolTip(swapColors, "交换渐变的起始色和结束色。");
        endColor.Click += (_, _) => ChooseGradientEndColor();
        swapColors.Click += (_, _) => SwapGradientColors();
        gradientGroup.Controls.AddRange(new Control[] { _gradientPreview, endColor, swapColors });
        flow.Controls.Add(gradientGroup);

        // 画笔大小
        var brushGroup = new GroupBox { Text = "画笔大小", Width = 164, Height = 62, Margin = new Padding(0, 0, 0, 6) };
        _brushSize = new NumericUpDown { Left = 8, Top = 24, Width = 44, Minimum = 1, Maximum = 4, Value = 1 };
        _toolTip.SetToolTip(_brushSize, "设置画笔和橡皮的大小，范围为 1–4 格。");
        _brushSize.ValueChanged += (_, _) =>
        {
            _canvas.BrushSize = (int)_brushSize.Value;
            _canvas.Invalidate();   // 刷新悬停高亮
        };
        var brushTip = new Label { Text = "1–4 格", Left = 52, Top = 27, AutoSize = true, ForeColor = Color.DimGray };
        brushGroup.Controls.Add(_brushSize);
        brushGroup.Controls.Add(brushTip);
        flow.Controls.Add(brushGroup);

        // 对称与显示
        var mirrorGroup = new GroupBox { Text = "对称 / 显示", Width = 164, Height = 108, Margin = new Padding(0, 0, 0, 6) };
        _mirrorHCheck = new CheckBox { Text = "水平镜像", Left = 10, Top = 22, AutoSize = true };
        _mirrorVCheck = new CheckBox { Text = "垂直镜像", Left = 10, Top = 46, AutoSize = true };
        _gridCheck = new CheckBox { Text = "网格线", Left = 10, Top = 70, AutoSize = true, Checked = true };
        _toolTip.SetToolTip(_mirrorHCheck, "在画布左右对称的位置同时绘制。");
        _toolTip.SetToolTip(_mirrorVCheck, "在画布上下对称的位置同时绘制。");
        _toolTip.SetToolTip(_gridCheck, "显示或隐藏画布网格线；导出的图片不含网格线。");
        _mirrorHCheck.CheckedChanged += (_, _) => _canvas.MirrorH = _mirrorHCheck.Checked;
        _mirrorVCheck.CheckedChanged += (_, _) => _canvas.MirrorV = _mirrorVCheck.Checked;
        _gridCheck.CheckedChanged += (_, _) => { _canvas.ShowGrid = _gridCheck.Checked; _canvas.Invalidate(); };
        mirrorGroup.Controls.AddRange(new Control[] { _mirrorHCheck, _mirrorVCheck, _gridCheck });
        flow.Controls.Add(mirrorGroup);

        // 编辑
        var editGroup = new GroupBox { Text = "选区与清空", Width = 164, Height = 100, Margin = new Padding(0, 0, 0, 6) };
        var clearButton = MakeButton("清空当前图层", 10, 56, 142);
        _toolTip.SetToolTip(clearButton, "清空当前图层的像素；可通过撤销恢复。");
        clearButton.Click += (_, _) => ClearCanvas();
        editGroup.Controls.Add(clearButton);
        var copyButton = MakeButton("复制", 10, 22, 68);
        var pasteButton = MakeButton("粘贴", 84, 22, 68);
        _toolTip.SetToolTip(copyButton, "复制选区（Ctrl+C），使用程序内部剪贴板。");
        _toolTip.SetToolTip(pasteButton, "粘贴选区（Ctrl+V），透明像素不会覆盖背景。");
        copyButton.Click += (_, _) => _canvas.CopySelection();
        pasteButton.Click += (_, _) => { SetTool(ToolKind.Selection); _canvas.PasteSelection(); };
        editGroup.Controls.Add(copyButton);
        editGroup.Controls.Add(pasteButton);
        flow.Controls.Add(editGroup);

        return panel;
    }

    private void AddToolButton(GroupBox group, ToolKind tool, string text, int row)
    {
        var b = new Button
        {
            Text = text,
            Location = new Point(10 + row % 2 * 74, 20 + row / 2 * 32),
            Size = new Size(68, 26),
            TextAlign = ContentAlignment.MiddleRight,
            ImageAlign = ContentAlignment.MiddleLeft,
            Image = MakeToolIcon(tool),
            AccessibleName = text,
        };
        b.Click += (_, _) => SetTool(tool);
        _toolTip.SetToolTip(b, tool switch
        {
            ToolKind.Pencil => "绘制像素（B）。按住左键拖动可连续绘制。",
            ToolKind.Eraser => "擦除像素（E）。按住左键拖动可连续擦除。",
            ToolKind.Eyedropper => "拾取画布上的颜色（I）；也可随时右键取色。",
            ToolKind.Fill => "填充相连的同色区域（G）。",
            ToolKind.Line => "拖动绘制直线（L）。",
            ToolKind.Rectangle => "拖动绘制空心矩形（R）。",
            ToolKind.Ellipse => "拖动绘制空心椭圆（O）。",
            ToolKind.Selection => "拖动框选区域，再拖动选区移动（M）。Ctrl+C 复制，Ctrl+V 粘贴。",
            ToolKind.Gradient => "拖动填充连续同色区域的线性渐变（D）。当前颜色为起始色。",
            _ => text,
        });
        _toolButtons[tool] = b;
        group.Controls.Add(b);
    }

    private static Bitmap MakeToolIcon(ToolKind tool)
    {
        string[] rows = tool switch
        {
            ToolKind.Pencil => new[] { ".......##.", "......###.", ".....###..", "....###...", "...###....", "..###.....", ".###......", ".##.......", ".#........", ".........." },
            ToolKind.Eraser => new[] { "..........", "....####..", "...######.", "..##..##..", ".##..##...", ".#####....", "..###.....", "...######.", "..........", ".........." },
            ToolKind.Eyedropper => new[] { "......###.", ".....####.", "....###...", "...###....", "..###.....", ".###......", ".##.......", "##........", "#.........", ".........." },
            ToolKind.Fill => new[] { "....##....", "...#..#...", "..#....#..", ".########.", ".########.", "..######..", "...####...", "....##..#.", "........#.", ".........." },
            ToolKind.Line => new[] { "........##", ".......##.", "......##..", ".....##...", "....##....", "...##.....", "..##......", ".##.......", "##........", ".........." },
            ToolKind.Rectangle => new[] { "..........", ".########.", ".#......#.", ".#......#.", ".#......#.", ".#......#.", ".#......#.", ".########.", "..........", ".........." },
            ToolKind.Ellipse => new[] { "..........", "...####...", "..#....#..", ".#......#.", ".#......#.", ".#......#.", "..#....#..", "...####...", "..........", ".........." },
            ToolKind.Gradient => new[] { "..........", ".#.#.####.", ".#.#.####.", ".#.#.####.", ".#.#.####.", ".#.#.####.", ".#.#.####.", ".#.#.####.", "..........", ".........." },
            _ => new[] { "..........", ".##.##.##.", ".#......#.", "..........", ".#......#.", ".#......#.", "..........", ".##.##.##.", "..........", ".........." },
        };
        var image = new Bitmap(24, 24);
        for (int y = 0; y < rows.Length; y++)
            for (int x = 0; x < rows[y].Length; x++)
                if (rows[y][x] == '#')
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                            image.SetPixel(2 + x * 2 + dx, 2 + y * 2 + dy, Color.FromArgb(213, 229, 242));
        return image;
    }

    private void SetTool(ToolKind tool)
    {
        _canvas.Tool = tool;
        _canvas.Invalidate();
        UpdateStatus();
        foreach (var (kind, button) in _toolButtons)
        {
            bool active = kind == tool;
            button.BackColor = active ? Color.FromArgb(63, 119, 190) : Color.FromArgb(53, 60, 72);
            button.ForeColor = active ? Color.White : Color.FromArgb(231, 235, 241);
        }
    }

    private Panel BuildRightPanel()
    {
        var panel = new Panel { Dock = DockStyle.Right, Width = 222 };
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(4, 8, 8, 8),
        };
        panel.Controls.Add(flow);

        // 调色板
        var paletteGroup = new GroupBox { Text = "颜色", Width = 192, Height = 224, Margin = new Padding(0, 0, 0, 6) };
        _paletteFlow = new FlowLayoutPanel
        {
            Left = 8, Top = 20, Width = 176, Height = 90,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoScroll = true,
        };
        var customButton = MakeButton("自定义颜色…", 8, 120, 118);
        _toolTip.SetToolTip(customButton, "选择颜色并添加到调色板。");
        customButton.Click += (_, _) => AddCustomColor();
        _colorPreview = new Panel { Left = 134, Top = 120, Size = new Size(50, 26), BorderStyle = BorderStyle.FixedSingle };
        paletteGroup.Controls.Add(_paletteFlow);
        paletteGroup.Controls.Add(customButton);
        paletteGroup.Controls.Add(_colorPreview);
        var replace = MakeButton("替换颜色", 8, 153, 84);
        var remove = MakeButton("删除颜色", 100, 153, 84);
        var loadPalette = MakeButton("加载色板", 8, 187, 84);
        var savePalette = MakeButton("保存色板", 100, 187, 84);
        _toolTip.SetToolTip(replace, "替换当前帧当前图层中的一种颜色，可撤销。");
        _toolTip.SetToolTip(remove, "从调色板移除当前颜色，不影响画布；至少保留一种颜色。");
        replace.Click += (_, _) => ReplaceColorUi();
        remove.Click += (_, _) => RemovePaletteColor();
        loadPalette.Click += (_, _) => LoadPaletteUi();
        savePalette.Click += (_, _) => SavePaletteUi();
        paletteGroup.Controls.AddRange(new Control[] { replace, remove, loadPalette, savePalette });
        flow.Controls.Add(paletteGroup);

        // 导出
        var exportGroup = new GroupBox { Text = "导出设置", Width = 192, Height = 103, Margin = new Padding(0, 0, 0, 6) };
        var scaleLabel = new Label { Text = "放大倍数：", Left = 8, Top = 18, AutoSize = true };
        _scaleCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 8, Top = 34, Width = 176 };
        foreach (int s in ExportScales) _scaleCombo.Items.Add($"{s}×");
        _scaleCombo.SelectedIndex = 3;   // 默认 8×
        _toolTip.SetToolTip(_scaleCombo, "选择导出图片的放大倍数，使用最近邻缩放。");
        exportGroup.Controls.Add(scaleLabel);
        exportGroup.Controls.Add(_scaleCombo);
        var presetsButton = MakeButton("导出预设…", 8, 68, 176);
        _toolTip.SetToolTip(presetsButton, "保存并复用 PNG、GIF 或精灵表的导出设置。");
        presetsButton.Click += (_, _) => ShowExportPresets();
        exportGroup.Controls.Add(presetsButton);
        flow.Controls.Add(exportGroup);

        var referenceGroup = new GroupBox { Text = "参考图（仅显示）", Width = 192, Height = 120, Margin = new Padding(0, 0, 0, 6) };
        var openReference = MakeButton("打开参考图…", 8, 22, 176);
        var showReference = _referenceVisible = new CheckBox { Text = "显示参考图", Left = 8, Top = 55, AutoSize = true, Checked = true };
        var opacityLabel = new Label { Text = "不透明度：", Left = 8, Top = 87, AutoSize = true };
        var opacity = _referenceOpacity = new NumericUpDown { Left = 100, Top = 82, Width = 68, Minimum = 0, Maximum = 100, Value = 35, Increment = 5 };
        var opacityUnit = new Label { Text = "%", Left = 170, Top = 87, AutoSize = true };
        _toolTip.SetToolTip(openReference, "打开本地图片并铺满画布作为临摹参考；不会保存到工程或导出图片。");
        _toolTip.SetToolTip(showReference, "显示或隐藏参考图，不影响画布像素。");
        _toolTip.SetToolTip(opacity, "调整参考图的不透明度。");
        openReference.Click += (_, _) => OpenReferenceImage();
        showReference.CheckedChanged += (_, _) => { _canvas.ShowReference = showReference.Checked; _canvas.Invalidate(); };
        opacity.ValueChanged += (_, _) => _canvas.ReferenceOpacity = (float)opacity.Value / 100f;
        referenceGroup.Controls.AddRange(new Control[] { openReference, showReference, opacityLabel, opacity, opacityUnit });
        flow.Controls.Add(referenceGroup);

        var layerGroup = new GroupBox { Text = "图层（上方遮盖下方）", Width = 192, Height = 176, Margin = new Padding(0, 0, 0, 6) };
        _layerList = new ListBox { Left = 8, Top = 22, Width = 176, Height = 74 };
        _layerList.SelectedIndexChanged += (_, _) =>
        {
            if (_updatingLayers || _layerList.SelectedIndex < 0) return;
            _activeLayer = _layers.Count - 1 - _layerList.SelectedIndex;
            ActivateLayer();
        };
        var addLayer = MakeButton("新建", 8, 100, 54);
        var deleteLayer = MakeButton("删除", 69, 100, 54);
        var visibility = MakeButton("显隐", 130, 100, 54);
        _toolTip.SetToolTip(visibility, "显示或隐藏当前图层。");
        var upLayer = MakeButton("上移", 8, 134, 84);
        var downLayer = MakeButton("下移", 100, 134, 84);
        addLayer.Click += (_, _) => AddLayer();
        deleteLayer.Click += (_, _) => DeleteLayer();
        visibility.Click += (_, _) => ToggleLayerVisibility();
        upLayer.Click += (_, _) => MoveLayer(1);
        downLayer.Click += (_, _) => MoveLayer(-1);
        layerGroup.Controls.AddRange(new Control[] { _layerList, addLayer, deleteLayer, visibility, upLayer, downLayer });
        flow.Controls.Add(layerGroup);
        flow.Controls.SetChildIndex(layerGroup, 1);

        return panel;
    }

    private void OpenReferenceImage()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件 (*.*)|*.*",
            Title = "选择参考图",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            using var source = Image.FromFile(dialog.FileName);
            _canvas.SetReferenceImage(new Bitmap(source), Path.GetFullPath(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, "无法打开参考图：" + ex.Message, "参考图", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RefreshLayerList()
    {
        _updatingLayers = true;
        _layerList.Items.Clear();
        for (int i = _layers.Count - 1; i >= 0; i--)
            _layerList.Items.Add($"{(_layers[i].Visible ? "●" : "○")} {_layers[i].Name}");
        _layerList.SelectedIndex = _layers.Count - 1 - _activeLayer;
        _updatingLayers = false;
    }

    private void ActivateLayer()
    {
        _grid = _layers[_activeLayer].Grid;
        _canvas.Editable = _layers[_activeLayer].Visible;
        _canvas.SetGrid(_grid);
        UpdateUndoButtons();
        UpdateStatus();
    }

    private void AddLayer()
    {
        if (_layers.Count >= 32) { MessageBox.Show(this, "最多支持 32 个图层。"); return; }
        if (_frames.Count > 1 && _frames.Sum(f => (long)f.Layers.Count * _grid.Width * _grid.Height) + (long)_grid.Width * _grid.Height > ProjectFile.MaxAnimationPixels) { MessageBox.Show(this, "动画帧总像素已达上限。", "图层"); return; }
        int number = 1;
        while (_layers.Any(l => l.Name == $"图层 {number}")) number++;
        var layer = new Layer($"图层 {number}", new PixelGrid(_grid.Width, _grid.Height));
        layer.SetHistoryBudget(_historyBudget);
        _layers.Insert(++_activeLayer, layer);
        RefreshLayerList(); ActivateLayer(); SetDirty();
    }

    private void DeleteLayer()
    {
        if (_layers.Count == 1) { MessageBox.Show(this, "至少保留一个图层。"); return; }
        if (MessageBox.Show(this, $"确定删除“{_layers[_activeLayer].Name}”？删除后无法撤销。", "删除图层", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        _layers[_activeLayer].History.Clear();
        _layers.RemoveAt(_activeLayer);
        _activeLayer = Math.Min(_activeLayer, _layers.Count - 1);
        RefreshLayerList(); ActivateLayer(); SetDirty();
    }

    private void ToggleLayerVisibility()
    {
        var layer = _layers[_activeLayer];
        layer.Visible = !layer.Visible;
        RefreshLayerList(); ActivateLayer(); SetDirty();
    }

    private void MoveLayer(int delta)
    {
        int target = _activeLayer + delta;
        if (target < 0 || target >= _layers.Count) return;
        (_layers[_activeLayer], _layers[target]) = (_layers[target], _layers[_activeLayer]);
        _activeLayer = target;
        RefreshLayerList(); ActivateLayer(); SetDirty();
    }

    // ---------- 调色板 ----------

    private void ApplyPalette(List<string> colors)
    {
        if (_palette.SequenceEqual(colors, StringComparer.OrdinalIgnoreCase)) return;
        _palette = colors; RebuildPaletteUi(); SetDirty();
    }

    private void RemovePaletteColor()
    {
        int index = _palette.FindIndex(color => color.Equals(Argb.ToRgbHex(_currentColor), StringComparison.OrdinalIgnoreCase));
        if (index < 0) { MessageBox.Show(this, "当前颜色不在调色板中。", "调色板"); return; }
        if (_palette.Count == 1) { MessageBox.Show(this, "至少保留一种颜色。", "调色板"); return; }
        _palette.RemoveAt(index); RebuildPaletteUi(); SetDirty();
    }

    private void ReplaceColorUi()
    {
        if (_playing || _canvas.InteractionInProgress) return;
        if (!_layers[_activeLayer].Visible) { MessageBox.Show(this, "请先显示当前图层。", "替换颜色"); return; }
        using var source = new ColorDialog { FullOpen = true, Color = Color.FromArgb(_currentColor) };
        MessageBox.Show(this, "接下来选择要被替换的颜色。", "替换颜色");
        if (source.ShowDialog(this) != DialogResult.OK) return;
        using var target = new ColorDialog { FullOpen = true, Color = Color.FromArgb(_currentColor) };
        MessageBox.Show(this, "接下来选择替换后的颜色。", "替换颜色");
        if (target.ShowDialog(this) != DialogResult.OK) return;
        var args = new System.Text.Json.Nodes.JsonObject { ["from_color"] = $"#{source.Color.R:X2}{source.Color.G:X2}{source.Color.B:X2}", ["to_color"] = $"#{target.Color.R:X2}{target.Color.G:X2}{target.Color.B:X2}" };
        var (_, count) = McpCanvas.ReplaceColor(args, _layers, _activeLayer);
        if (count > 0) { _canvas.RefreshFromData(); UpdateUndoButtons(); SetDirty(); }
        MessageBox.Show(this, $"已替换 {count} 个像素。", "替换颜色");
    }

    private void LoadPaletteUi()
    {
        if (_playing) return;
        using var dialog = new OpenFileDialog { Filter = "PixelCanvas 调色板 (*.pxpal)|*.pxpal", InitialDirectory = OutputPaths.DirectoryPath(".pxpal") };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { ApplyPalette(PaletteFile.Load(dialog.FileName)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        { MessageBox.Show(this, "无法加载调色板：" + ex.Message, "调色板", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void SavePaletteUi()
    {
        using var dialog = new SaveFileDialog { Filter = "PixelCanvas 调色板 (*.pxpal)|*.pxpal", InitialDirectory = OutputPaths.DirectoryPath(".pxpal"), FileName = "palette.pxpal" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { PaletteFile.Save(dialog.FileName, _palette, overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { MessageBox.Show(this, "无法保存调色板：" + ex.Message, "调色板", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

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
            _toolTip.SetToolTip(swatch, $"选择颜色 #{hex}");
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
            if (_palette.Count >= ProjectFile.MaxPaletteColors)
            {
                MessageBox.Show(this, $"调色板最多支持 {ProjectFile.MaxPaletteColors} 种颜色。", "调色板", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _palette.Add(hex);       // 追加到调色板末尾
            RebuildPaletteUi();
            SetDirty();              // 仅调色板内容真正变化时才算工程被修改
        }
        SetCurrentColor(Argb.FromRgbHex(hex));
    }

    private void ChooseGradientEndColor()
    {
        using var dialog = new ColorDialog { FullOpen = true, Color = Color.FromArgb(_canvas.GradientEndColor) };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _canvas.GradientEndColor = Argb.FromRgb(dialog.Color.ToArgb());
        _gradientPreview.Invalidate();
    }

    private void SwapGradientColors()
    {
        int oldStart = _currentColor;
        SetCurrentColor(_canvas.GradientEndColor);
        _canvas.GradientEndColor = oldStart;
        _gradientPreview.Invalidate();
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
            $"切换为 {w}×{h} 将清空所有帧及撤销历史，确定继续吗？",
            "切换画布尺寸", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (r != DialogResult.OK) return false;
        ApplyCanvasSize(w, h);
        return true;
    }

    private void ApplyCanvasSize(int w, int h)
    {
        _grid = new PixelGrid(w, h);
        _layers = new List<Layer> { new("图层 1", _grid) };
        _frames = new List<AnimationFrame> { new(_layers) }; _activeFrame = 0;
        _frameUndo.Clear();
        _historyBudget = new HistoryBudget();
        _layers[0].SetHistoryBudget(_historyBudget);
        _activeLayer = 0;
        _canvas.Layers = _layers;
        _canvas.Editable = true;
        RefreshLayerList();
        _canvas.SetGrid(_grid);
        _manualZoom = false;
        _canvas.UpdateZoom(CanvasAvailable());
        UpdateUndoButtons();
        UpdateSizeLabel();
        RefreshAnimationControls();
        SetDirty();
    }

    /// <summary>让尺寸下拉框反映当前实际尺寸（预设方形则选中，否则清空选择）。</summary>
    private void RestoreSizeSelection()
    {
        int idx = Array.IndexOf(PresetSizes, _grid.Width);
        _sizeCombo.SelectedIndex = (_grid.Height == _grid.Width && idx >= 0) ? idx : -1;
        UpdateSizeLabel();
    }

    private void UpdateSizeLabel()
    {
        _sizeLabel.Text = $"当前：{_grid.Width}×{_grid.Height}";
        UpdateStatus();
    }

    /// <summary>自定义尺寸对话框：宽/高文本框 + 校验（4–512，非法弹错误且不应用）。</summary>
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
            ClientSize = new Size(450, 195);
            Font = new Font("Microsoft YaHei UI", 9F);

            var lblW = new Label { Text = "宽（4–512）：", Location = new Point(24, 27), AutoSize = true };
            _w.SetBounds(180, 21, 210, 35);
            _w.Text = curW.ToString();
            var lblH = new Label { Text = "高（4–512）：", Location = new Point(24, 78), AutoSize = true };
            _h.SetBounds(180, 72, 210, 35);
            _h.Text = curH.ToString();
            var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(180, 132), Size = new Size(105, 39) };
            ok.Click += OnOk;
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(300, 132), Size = new Size(105, 39) };
            Controls.AddRange(new Control[] { lblW, _w, lblH, _h, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void OnOk(object? sender, EventArgs e)
        {
            if (!int.TryParse(_w.Text.Trim(), out int w) || !int.TryParse(_h.Text.Trim(), out int h)
                || w < 4 || w > 512 || h < 4 || h > 512)
            {
                MessageBox.Show(this, "宽和高必须是 4–512 之间的整数。", "输入有误",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;   // 校验不通过：不关闭对话框
                return;
            }
            ValueWidth = w;
            ValueHeight = h;
        }
    }

    // ---------- 工具动作 ----------

    private void DoUndo() => StepHistory(redo: false);

    private void DoRedo() => StepHistory(redo: true);

    /// <summary>
    /// 撤销或重做一步：图层像素历史与帧结构快照按动作序号竞争——撤销取序号更大者，重做取更小者。
    /// 像素历史跨所有帧的所有图层扫描，因此可能落在非当前帧；命中时自动切到该帧图层再撤销。
    /// </summary>
    private bool StepHistory(bool redo)
    {
        if (_playing || _canvas.InteractionInProgress) return false;
        SyncCurrentFrame();
        var best = BestPixelHistory(redo);
        bool frame = redo ? _frameUndo.CanRedo : _frameUndo.CanUndo;
        if (best is null && !frame) return false;
        long pixelSeq = best?.Item3 ?? (redo ? long.MaxValue : long.MinValue);
        bool frameFirst = frame && (best is null ||
            (redo ? _frameUndo.RedoSeq < pixelSeq : _frameUndo.UndoSeq > pixelSeq));
        if (frameFirst)
        {
            var current = CaptureFrames();
            RestoreFrameSnapshot(redo ? _frameUndo.Redo(current) : _frameUndo.Undo(current));
        }
        else
        {
            int frameIndex = best!.Value.Item1, layerIndex = best.Value.Item2;
            if (frameIndex != _activeFrame) SwitchFrame(frameIndex);
            if (layerIndex != _activeLayer) { _activeLayer = layerIndex; ActivateLayer(); }
            var layer = _layers[layerIndex];
            int[] current = layer.Grid.Snapshot();
            layer.Grid.Restore(redo ? layer.History.Redo(current) : layer.History.Undo(current));
            _canvas.RefreshFromData();
        }
        UpdateUndoButtons();
        SetDirty();
        return true;
    }

    /// <summary>在所有帧的所有图层中找出撤销/重做序号最优的一层（撤销取最大、重做取最小）。</summary>
    private (int Frame, int Layer, long Seq)? BestPixelHistory(bool redo)
    {
        (int Frame, int Layer, long Seq)? best = null;
        long sideEdge = redo ? long.MaxValue : long.MinValue;
        for (int f = 0; f < _frames.Count; f++)
        {
            var layers = _frames[f].Layers;
            for (int l = 0; l < layers.Count; l++)
            {
                long seq = redo ? layers[l].History.RedoSeq : layers[l].History.UndoSeq;
                if (seq == sideEdge) continue;
                if (best is null || (redo ? seq < best.Value.Seq : seq > best.Value.Seq)) best = (f, l, seq);
            }
        }
        return best;
    }

    private void UpdateUndoButtons()
    {
        _undoButton.Enabled = _history.CanUndo || _frameUndo.CanUndo;
        _redoButton.Enabled = _history.CanRedo || _frameUndo.CanRedo;
    }

    private void ClearCanvas()
    {
        if (!_layers[_activeLayer].Visible) return;
        if (!HasAnyPixel(_grid)) return;         // 已是空画布：不必弹确认框，也不产生历史步骤
        if (MessageBox.Show(this, "确定要清空当前图层吗？（清空后可通过撤销恢复）", "清空图层",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
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
        DeleteOwnRecovery();
        UpdateTitle();
    }

    private void SaveProjectAs()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "像素画布工程 (*.pxproj)|*.pxproj|所有文件 (*.*)|*.*",
            InitialDirectory = OutputPaths.DirectoryPath(".pxproj", _frames.Count > 1),
            FileName = _projectPath is null ? "未命名.pxproj" : Path.GetFileName(_projectPath),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        byte[]? expected;
        try { expected = Fingerprint(dialog.FileName); }
        catch (Exception ex) { MessageBox.Show(this, "无法检查目标文件：" + ex.Message, "保存工程", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        if (!TryWriteProject(dialog.FileName, expected, useCurrentFingerprint: false)) return;
        _projectPath = dialog.FileName;
        _dirty = false;
        DeleteOwnRecovery();
        UpdateTitle();
    }

    /// <summary>确保已有工程路径；没有则弹出保存对话框并记录（用户取消返回 false）。</summary>
    private bool EnsureProjectPath()
    {
        if (_projectPath != null) return true;
        using var dlg = new SaveFileDialog
        {
            Filter = "像素画布工程 (*.pxproj)|*.pxproj|所有文件 (*.*)|*.*",
            InitialDirectory = OutputPaths.DirectoryPath(".pxproj", _frames.Count > 1),
            FileName = "未命名.pxproj",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return false;
        try { _projectFingerprint = Fingerprint(dlg.FileName); }
        catch (Exception ex) { MessageBox.Show(this, "无法检查目标文件：" + ex.Message, "保存工程", MessageBoxButtons.OK, MessageBoxIcon.Error); return false; }
        _projectPath = dlg.FileName;
        return true;
    }

    /// <summary>写入工程文件；失败弹错误提示并返回 false（保留路径便于重试）。</summary>
    private static byte[]? Fingerprint(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > ProjectFile.MaxFileBytes) throw new IOException($"工程文件超过 {ProjectFile.MaxFileBytes / 1024 / 1024} MiB 上限。");
            return SHA256.HashData(stream);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static bool SameFingerprint(byte[]? a, byte[]? b) =>
        a is null ? b is null : b is not null && CryptographicOperations.FixedTimeEquals(a, b);

    private void WriteProjectChecked(string path, byte[]? expected)
    {
        if (!SameFingerprint(expected, Fingerprint(path)))
            throw new IOException("工程文件已被其他程序修改、替换或删除，请另存为新文件后再继续。");
        SyncCurrentFrame();
        ProjectFile.Save(path, _layers, _activeLayer, _palette, overwrite: expected is not null, frames: _frames, activeFrame: _activeFrame, framesPerSecond: _fps);
        _projectFingerprint = Fingerprint(path) ?? throw new IOException("保存后无法确认工程文件，请检查保存位置。");
    }

    private bool TryWriteProject(string path, byte[]? expected = null, bool useCurrentFingerprint = true)
    {
        try
        {
            WriteProjectChecked(path, useCurrentFingerprint ? _projectFingerprint : expected);
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

        if (_dirty)
        {
            var choice = MessageBox.Show(this, "当前工程有未保存的修改，是否保存？", "打开工程",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (choice == DialogResult.Cancel) return;
            if (choice == DialogResult.Yes)
            {
                if (!EnsureProjectPath() || !TryWriteProject(_projectPath!)) return;
                _dirty = false;
                UpdateTitle();
            }
        }

        byte[]? before;
        try { before = Fingerprint(dlg.FileName); }
        catch (Exception ex) { MessageBox.Show(this, "无法检查工程：" + ex.Message, "打开工程", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        var result = ProjectFile.TryLoad(dlg.FileName, out string error);
        if (result is null)
        {
            MessageBox.Show(this, "无法打开工程：" + error, "打开工程",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;                              // 校验失败：当前画布保持不变
        }

        byte[]? after;
        try { after = Fingerprint(dlg.FileName); }
        catch (Exception ex) { MessageBox.Show(this, "无法检查工程：" + ex.Message, "打开工程", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        if (before is null || !SameFingerprint(before, after)) { MessageBox.Show(this, "工程在读取时发生变化，请重试。", "打开工程", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        ApplyLoadedProject(result, dlg.FileName, after!);
    }

    private void ApplyLoadedProject(ProjectFile.LoadResult result, string path, byte[] fingerprint)
    {
        DeleteOwnRecovery();
        _frames = result.Frames;
        _activeFrame = result.ActiveFrame;
        _fps = result.FramesPerSecond;
        _animationTimer.Interval = Math.Max(1, 1000 / _fps);
        _grid = result.Grid;
        _layers = result.Layers;
        _historyBudget = new HistoryBudget();
        foreach (var frame in _frames) foreach (var layer in frame.Layers) layer.SetHistoryBudget(_historyBudget);
        _frameUndo.Clear();
        _activeLayer = result.ActiveLayer;
        _palette = result.Palette;
        _canvas.Layers = _layers;
        _canvas.Editable = _layers[_activeLayer].Visible;
        RefreshLayerList();
        _projectPath = path;
        _projectFingerprint = fingerprint;
        _dirty = false;

        _canvas.SetGrid(_grid);
        _manualZoom = false;
        _canvas.UpdateZoom(CanvasAvailable());
        SetCurrentColor(Argb.FromRgbHex(_palette[0]));
        RebuildPaletteUi();
        RestoreSizeSelection();
        UpdateUndoButtons();
        UpdateTitle();
        RefreshAnimationControls();
    }

    // ---------- 导出 PNG ----------

    private void ExportPng()
    {
        int scale = _scaleCombo.SelectedIndex >= 0 ? ExportScales[_scaleCombo.SelectedIndex] : 1;
        bool animation = _frames.Count > 1;
        using var dlg = new SaveFileDialog
        {
            Filter = animation ? "GIF 动画 (*.gif)|*.gif|PNG 图片 (*.png)|*.png" : "PNG 图片 (*.png)|*.png|GIF 动画 (*.gif)|*.gif",
            InitialDirectory = OutputPaths.DirectoryPath(animation ? ".gif" : ".png", animation),
            FileName = $"pixel-art-{_grid.Width}x{_grid.Height}" + (animation ? ".gif" : ".png"),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            if (string.Equals(Path.GetExtension(dlg.FileName), ".gif", StringComparison.OrdinalIgnoreCase))
            {
                SyncCurrentFrame();
                GifExport.Save(dlg.FileName, _frames, _fps, scale, overwrite: true);
                return;
            }
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
            AtomicFile.Write(dlg.FileName, stream => bmp.Save(stream, ImageFormat.Png), overwrite: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导出失败：" + ex.Message, "导出图片或动画",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------- 快捷键 / 关闭 ----------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_playing) { e.Handled = true; e.SuppressKeyPress = true; return; }
        if (e.KeyCode == Keys.F11 && !e.Control && !e.Alt && !e.Shift)
        { ToggleImmersive(); e.Handled = true; e.SuppressKeyPress = true; return; }
        if (e.KeyCode == Keys.F1 && !e.Control && !e.Alt && !e.Shift)
        { ShowShortcutHelp(); e.Handled = true; e.SuppressKeyPress = true; return; }
        if (_immersive && e.KeyCode == Keys.Escape)
        { ToggleImmersive(); e.Handled = true; e.SuppressKeyPress = true; return; }
        if (ActiveControl is TextBoxBase or NumericUpDown or ComboBox) return;   // 文本输入时不抢按键

        if (e.KeyCode == Keys.F2 && !e.Control && !e.Alt && !e.Shift)
        {
            MessageBox.Show(this, $"{_statusText?.Text}\n颜色：#{Argb.ToRgbHex(_currentColor)}　画笔大小：{_brushSize.Value}\n当前图层：{(_layers[_activeLayer].Visible ? "显示" : "隐藏")}\n水平镜像：{(_mirrorHCheck.Checked ? "开" : "关")}　垂直镜像：{(_mirrorVCheck.Checked ? "开" : "关")}　网格：{(_gridCheck.Checked ? "开" : "关")}\n参考图：{(_referenceVisible.Checked ? "显示" : "隐藏")}，不透明度 {_referenceOpacity.Value}%\n导出放大：{_scaleCombo.SelectedItem}\n画布位置：{_placementCombo.SelectedItem}", "当前画布状态", MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (_immersive) _canvas.Focus();
            e.Handled = true; e.SuppressKeyPress = true; return;
        }
        if (e.Control && e.Shift && !e.Alt)
        {
            switch (e.KeyCode)
            {
                case Keys.N: AddLayer(); break;
                case Keys.S: SaveProjectAs(); break;
                case Keys.G: ChooseGradientEndColor(); break;
                case Keys.Delete: DeleteLayer(); break;
                case Keys.H: ToggleLayerVisibility(); break;
                case Keys.R: OpenReferenceImage(); break;
                case Keys.T: _referenceVisible.Checked = !_referenceVisible.Checked; break;
                case Keys.X: _scaleCombo.SelectedIndex = (_scaleCombo.SelectedIndex + 1) % _scaleCombo.Items.Count; break;
                case Keys.P: _placementCombo.SelectedIndex = 1 - _placementCombo.SelectedIndex; break;
                case Keys.D:
                    using (var dialog = new SizeDialog(_grid.Width, _grid.Height))
                        if (dialog.ShowDialog(this) == DialogResult.OK) ChangeSize(dialog.ValueWidth, dialog.ValueHeight);
                    RestoreSizeSelection();
                    break;
                default: goto OtherShortcuts;
            }
            e.Handled = true; e.SuppressKeyPress = true; return;
        }
        if (e.Control && e.Alt && !e.Shift && e.KeyCode is Keys.Up or Keys.Down)
        { MoveLayer(e.KeyCode == Keys.Up ? 1 : -1); e.Handled = true; e.SuppressKeyPress = true; return; }
        if (e.Alt && !e.Control && !e.Shift && e.KeyCode is Keys.Up or Keys.Down)
        {
            _referenceOpacity.Value = Math.Clamp(_referenceOpacity.Value + (e.KeyCode == Keys.Up ? 5 : -5), _referenceOpacity.Minimum, _referenceOpacity.Maximum);
            e.Handled = true; e.SuppressKeyPress = true; return;
        }
        if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.Delete)
        { ClearCanvas(); e.Handled = true; e.SuppressKeyPress = true; return; }
        if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.E)
        { ExportPng(); e.Handled = true; e.SuppressKeyPress = true; return; }

        OtherShortcuts:

        if (e.KeyCode == Keys.Space && !e.Control && !e.Alt)
        {
            _canvas.SetPanModifier(true);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.Control && !e.Shift && e.KeyCode == Keys.Z) { DoUndo(); e.Handled = true; return; }
        if (e.Control && e.Shift && e.KeyCode == Keys.Z) { DoRedo(); e.Handled = true; return; }
        if (e.Control && e.KeyCode == Keys.Y) { DoRedo(); e.Handled = true; return; }
        if (e.Control && e.KeyCode == Keys.S) { SaveProject(); e.Handled = true; return; }
        if (e.Control && e.KeyCode == Keys.O) { OpenProject(); e.Handled = true; return; }
        if (e.Control && e.KeyCode == Keys.C) { _canvas.CopySelection(); e.Handled = true; e.SuppressKeyPress = true; return; }
        if (e.Control && e.KeyCode == Keys.V) { SetTool(ToolKind.Selection); _canvas.PasteSelection(); e.Handled = true; e.SuppressKeyPress = true; return; }
        if (e.Control || e.Alt) return;

        switch (e.KeyCode)
        {
            case Keys.B: SetTool(ToolKind.Pencil); break;
            case Keys.E: SetTool(ToolKind.Eraser); break;
            case Keys.I: SetTool(ToolKind.Eyedropper); break;
            case Keys.G: SetTool(ToolKind.Fill); break;
            case Keys.L: SetTool(ToolKind.Line); break;
            case Keys.R: SetTool(ToolKind.Rectangle); break;
            case Keys.O: SetTool(ToolKind.Ellipse); break;
            case Keys.M: SetTool(ToolKind.Selection); break;
            case Keys.D: SetTool(ToolKind.Gradient); break;
            case Keys.C: AddCustomColor(); break;
            case Keys.F: FitCanvas(); break;
            case Keys.H: _mirrorHCheck.Checked = !_mirrorHCheck.Checked; break;
            case Keys.V: _mirrorVCheck.Checked = !_mirrorVCheck.Checked; break;
            case Keys.T: _gridCheck.Checked = !_gridCheck.Checked; break;
            case Keys.Oemcomma: case Keys.OemPeriod:
                int colorIndex = _palette.FindIndex(p => string.Equals(p, Argb.ToRgbHex(_currentColor), StringComparison.OrdinalIgnoreCase));
                int direction = e.KeyCode == Keys.OemPeriod ? 1 : -1;
                int next = colorIndex < 0 ? 0 : (colorIndex + direction + _palette.Count) % _palette.Count;
                SetCurrentColor(Argb.FromRgbHex(_palette[next]));
                break;
            case Keys.OemOpenBrackets:
                if (_activeLayer > 0) { _activeLayer--; RefreshLayerList(); ActivateLayer(); }
                break;
            case Keys.OemCloseBrackets:
                if (_activeLayer + 1 < _layers.Count) { _activeLayer++; RefreshLayerList(); ActivateLayer(); }
                break;
            case Keys.Oemplus: case Keys.Add: _brushSize.Value = Math.Min(_brushSize.Maximum, _brushSize.Value + 1); break;
            case Keys.OemMinus: case Keys.Subtract: _brushSize.Value = Math.Max(_brushSize.Minimum, _brushSize.Value - 1); break;
        }
    }

    private void ToggleImmersive()
    {
        if (_canvas.InteractionInProgress) return;
        SuspendLayout();
        if (!_immersive)
        {
            _leftWasVisible = _leftSidebar.Visible;
            _rightWasVisible = _rightSidebar.Visible;
            _previousBorder = FormBorderStyle;
            _previousWindowState = WindowState;
            _immersive = true;
            _leftSidebar.Visible = _rightSidebar.Visible = false;
            _leftHandle.Visible = _rightHandle.Visible = false;
            _commandBar.Visible = _statusBar.Visible = _animationBar.Visible = false;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            WindowState = FormWindowState.Normal;
            FormBorderStyle = _previousBorder;
            _leftSidebar.Visible = _leftWasVisible;
            _rightSidebar.Visible = _rightWasVisible;
            _leftHandle.Visible = _rightHandle.Visible = true;
            _commandBar.Visible = _statusBar.Visible = _animationBar.Visible = true;
            WindowState = _previousWindowState;
            _immersive = false;
        }
        ResumeLayout(true);
        _canvas.Focus();
    }

    private void ShowShortcutHelp()
    {
        using var dialog = new Form
        {
            Text = "快捷键指南", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(990, 810), MinimumSize = new Size(800, 600),
            BackColor = Color.FromArgb(31, 35, 43), ForeColor = Color.FromArgb(228, 234, 241),
            Font = new Font("Microsoft YaHei UI", 9F), ShowInTaskbar = false,
            MaximizeBox = false, MinimizeBox = false,
        };
        var header = new Panel { Dock = DockStyle.Top, Height = 92, Padding = new Padding(24, 10, 24, 0) };
        var title = new Label { Text = "快捷键指南", Dock = DockStyle.Top, Height = 40,
            Font = new Font(dialog.Font.FontFamily, 14F, FontStyle.Bold), ForeColor = Color.FromArgb(136, 207, 245) };
        var subtitle = new Label { Text = "绘图时按 F1 随时打开  ·  F2 查看当前状态", Dock = DockStyle.Top, Height = 30,
            ForeColor = Color.FromArgb(171, 185, 202) };
        header.Controls.Add(subtitle);
        header.Controls.Add(title);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 65, Padding = new Padding(20, 8, 20, 8) };
        var close = new Button { Text = "关闭  Esc", Dock = DockStyle.Right, Width = 125, DialogResult = DialogResult.Cancel };
        StyleButton(close);
        footer.Controls.Add(close);
        dialog.CancelButton = close;
        dialog.AcceptButton = close;

        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(18, 0, 18, 0) };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        FlowLayoutPanel MakeColumn()
        {
            return new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, Padding = new Padding(4, 0, 8, 8) };
        }
        var left = MakeColumn();
        var right = MakeColumn();
        columns.Controls.Add(left, 0, 0);
        columns.Controls.Add(right, 1, 0);

        void AddSection(FlowLayoutPanel column, string heading, params (string Keys, string Action)[] shortcuts)
        {
            var group = new GroupBox { Text = heading, Width = 420, Height = 30 + shortcuts.Length * 34,
                Margin = new Padding(0, 0, 0, 12), ForeColor = Color.FromArgb(139, 205, 239) };
            for (int i = 0; i < shortcuts.Length; i++)
            {
                int y = 24 + i * 34;
                var keys = new Label { Text = shortcuts[i].Keys, Left = 12, Top = y, Width = 205, Height = 30,
                    TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(229, 235, 245) };
                var action = new Label { Text = shortcuts[i].Action, Left = 218, Top = y, Width = 190, Height = 30,
                    TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(181, 193, 210) };
                group.Controls.Add(keys);
                group.Controls.Add(action);
            }
            column.Controls.Add(group);
        }

        AddSection(left, "模式与画布",
            ("F11 / Esc", "沉浸模式 / 退出"), ("F1 / F2", "快捷键 / 当前状态"),
            ("滚轮", "缩放画布"), ("中键拖动 / 空格 + 拖动", "平移画布"),
            ("F", "适应窗口"), ("Ctrl + Shift + D", "自定义画布尺寸"),
            ("Ctrl + Shift + P", "切换左上 / 居中"));
        AddSection(left, "绘图工具",
            ("B / E", "画笔 / 橡皮"), ("I / G", "取色 / 填充"),
            ("L / R / O", "直线 / 矩形 / 椭圆"), ("M / D", "选区 / 渐变"),
            ("+ / -", "调整画笔大小"), (", / .", "切换调色板颜色"),
            ("C", "自定义颜色"), ("Ctrl + Shift + G", "设置渐变结束色"), ("H / V", "水平 / 垂直镜像"), ("T", "网格线"));

        AddSection(right, "编辑与选区",
            ("Ctrl + Z / Y", "撤销 / 重做"), ("Ctrl + Shift + Z", "重做"),
            ("Ctrl + C / V", "复制 / 粘贴选区"), ("Ctrl + Delete", "清空当前图层"));
        AddSection(right, "图层",
            ("[ / ]", "选择下方 / 上方图层"), ("Ctrl + Shift + N", "新建图层"),
            ("Ctrl + Shift + Delete", "删除图层"), ("Ctrl + Shift + H", "显示 / 隐藏图层"),
            ("Ctrl + Alt + ↑ / ↓", "图层上移 / 下移"));
        AddSection(right, "文件与参考图",
            ("Ctrl + S / O / E", "保存 / 打开 / 导出"), ("Ctrl + Shift + S", "另存为"), ("Ctrl + Shift + X", "切换导出倍数"),
            ("Ctrl + Shift + R", "打开参考图"), ("Ctrl + Shift + T", "显示 / 隐藏参考图"),
            ("Alt + ↑ / ↓", "参考图不透明度"));

        dialog.Controls.Add(columns);
        dialog.Controls.Add(footer);
        dialog.Controls.Add(header);
        dialog.ShowDialog(this);
        if (_immersive) _canvas.Focus();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.Space) _canvas.SetPanModifier(false);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        _canvas.SetPanModifier(false);
        base.OnDeactivate(e);
    }

    private void FitCanvas()
    {
        _manualZoom = false;
        _canvasPanel.AutoScrollPosition = Point.Empty;
        _canvas.UpdateZoom(CanvasAvailable());
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel) return;
        if (_playing) ToggleAnimation();

        if (!_dirty) { CloseRecovery(); return; }

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
        if (!e.Cancel) CloseRecovery();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _canvas.UpdateZoom(CanvasAvailable());
        _recoveryPending = true;
        StartMcpPipe();
        try { InitializeRecovery(); }
        finally { _recoveryPending = false; }
    }

    private void InitializeRecovery()
    {
        try
        {
            Directory.CreateDirectory(_recoveryDirectory);
            _recoveryLock = new FileStream(Path.ChangeExtension(RecoveryPath, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            foreach (string candidate in Directory.GetFiles(_recoveryDirectory, "*.pxproj").OrderByDescending(File.GetLastWriteTimeUtc))
            {
                if (string.Equals(candidate, RecoveryPath, StringComparison.OrdinalIgnoreCase)) continue;
                FileStream candidateLock;
                try { candidateLock = new FileStream(Path.ChangeExtension(candidate, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { continue; }
                using (candidateLock)
                {
                    var choice = MessageBox.Show(this, $"发现 {File.GetLastWriteTime(candidate):yyyy-MM-dd HH:mm} 的自动保存副本。是否恢复？\n选择“否”可保留副本，稍后再恢复。", "恢复未保存的作品", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (choice != DialogResult.Yes) break;
                    var result = ProjectFile.TryLoad(candidate, out string error);
                    if (result is null) { MessageBox.Show(this, "恢复失败，副本已保留：" + error, "恢复作品", MessageBoxButtons.OK, MessageBoxIcon.Error); break; }
                    // 先写入当前会话的副本，确认成功后才移除旧副本。
                    ProjectFile.Save(RecoveryPath, result.Layers, result.ActiveLayer, result.Palette, frames: result.Frames, activeFrame: result.ActiveFrame, framesPerSecond: result.FramesPerSecond);
                    _frames = result.Frames; _activeFrame = result.ActiveFrame; _fps = result.FramesPerSecond;
                    _animationTimer.Interval = Math.Max(1, 1000 / _fps);
                    _layers = result.Layers;
                    _historyBudget = new HistoryBudget();
                    foreach (var frame in _frames) foreach (var layer in frame.Layers) layer.SetHistoryBudget(_historyBudget);
                    _frameUndo.Clear();
                    _activeLayer = result.ActiveLayer;
                    _grid = result.Grid;
                    _palette = result.Palette;
                    _projectPath = null;
                    _projectFingerprint = null;
                    _canvas.Layers = _layers;
                    _canvas.Editable = _layers[_activeLayer].Visible;
                    _canvas.SetGrid(_grid);
                    _manualZoom = false;
                    _canvas.UpdateZoom(CanvasAvailable());
                    RefreshLayerList();
                    SetCurrentColor(Argb.FromRgbHex(_palette[0]));
                    RebuildPaletteUi(); RestoreSizeSelection(); UpdateUndoButtons(); RefreshAnimationControls();
                    _dirty = true; UpdateTitle();
                    try { File.Delete(candidate); } catch { }
                    break;
                }
            }
            _autosaveTimer.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法启用自动保存，仍可手动保存工程：" + ex.Message, "自动保存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SaveRecovery()
    {
        if (!_dirty || _canvas.InteractionInProgress || _recoveryLock is null) return;
        try
        {
            SyncCurrentFrame();
            ProjectFile.Save(RecoveryPath, _layers, _activeLayer, _palette, frames: _frames, activeFrame: _activeFrame, framesPerSecond: _fps);
            _autosaveErrorShown = false;
        }
        catch (Exception ex)
        {
            if (_autosaveErrorShown) return;
            _autosaveErrorShown = true;
            MessageBox.Show(this, "自动保存失败，请手动保存工程：" + ex.Message, "自动保存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void DeleteOwnRecovery()
    {
        try { if (File.Exists(RecoveryPath)) File.Delete(RecoveryPath); } catch { }
    }

    private void CloseRecovery()
    {
        _autosaveTimer.Stop();
        _animationTimer.Stop();
        DeleteOwnRecovery();
        _recoveryLock?.Dispose();
        _recoveryLock = null;
        try { File.Delete(Path.ChangeExtension(RecoveryPath, ".lock")); } catch { }
    }
}
