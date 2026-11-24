using System.Drawing;
using System.Windows.Forms;

namespace PixelCanvas;

public sealed partial class MainForm
{
    private readonly System.Windows.Forms.Timer _animationTimer = new();
    private readonly StructureUndo _structureUndo = new();
    private FrameTimeline? _timeline;
    private CheckBox? _onionCheck;
    private NumericUpDown _fpsControl = null!;
    private Button _playButton = null!;
    private Control _animationBar = null!;

    private void SyncCurrentFrame()
    {
        if (_frames.Count == 0) return;
        _frames[_activeFrame].Layers = _layers;
        _frames[_activeFrame].ActiveLayer = _activeLayer;
    }

    private bool CanAddFrame(int layerCount)
    {
        long pixels = _frames.Sum(f => (long)f.Layers.Count * _grid.Width * _grid.Height);
        return _frames.Count < ProjectFile.MaxFrames && pixels + (long)layerCount * _grid.Width * _grid.Height <= ProjectFile.MaxAnimationPixels;
    }

    /// <summary>当前结构（帧与图层）的快照（先把正在编辑的图层引用同步回当前帧）。</summary>
    private StructureUndo.Snapshot CaptureStructure()
    {
        SyncCurrentFrame();
        return _structureUndo.Capture(_frames, _activeFrame);
    }

    /// <summary>记录一次结构改动之前的快照；结构改动属于新动作，所有重做同时失效。</summary>
    private void RecordStructure() => CommitStructure(CaptureStructure());

    private void CommitStructure(StructureUndo.Snapshot before)
    {
        _structureUndo.Commit(before);
        foreach (var frame in _frames) foreach (var layer in frame.Layers) layer.History.ClearRedo();
        UpdateUndoButtons();
    }

    /// <summary>把帧与图层结构恢复到快照状态，并重建画布、图层列表与时间轴绑定。</summary>
    private void RestoreStructureSnapshot(StructureUndo.Snapshot snapshot)
    {
        var frames = _structureUndo.Restore(snapshot);
        _frames = frames.ToList();
        _activeFrame = Math.Clamp(snapshot.ActiveFrame, 0, _frames.Count - 1);
        _layers = _frames[_activeFrame].Layers;
        _activeLayer = Math.Clamp(_frames[_activeFrame].ActiveLayer, 0, _layers.Count - 1);
        _grid = _layers[_activeLayer].Grid;
        _canvas.Layers = _layers;
        _canvas.Editable = !_playing && _layers[_activeLayer].Visible;
        _canvas.SetGrid(_grid);
        _canvas.RefreshFromData();
        RefreshLayerList(); UpdateUndoButtons(); UpdateStatus(); RefreshAnimationControls(); SetDirty();
    }

    private Control BuildAnimationBar()
    {
        var root = new Panel { Dock = DockStyle.Bottom, Height = 135, BackColor = Color.FromArgb(30, 34, 43), AccessibleName = "动画时间轴" };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, BackColor = Color.FromArgb(38, 43, 53), Padding = new Padding(8, 6, 4, 4), WrapContents = false, AutoScroll = true };
        var label = new Label { Text = "动画帧", ForeColor = Color.FromArgb(205, 218, 234), Width = 55, Height = 30, TextAlign = ContentAlignment.MiddleLeft };
        bar.Controls.Add(label);
        Button Add(string text, string tip, Action action, int width = 66)
        {
            var button = new Button { Text = text, Width = width, Height = 30, Margin = new Padding(0, 0, 5, 0) };
            StyleButton(button); _toolTip.SetToolTip(button, tip); button.Click += (_, _) => action(); bar.Controls.Add(button); return button;
        }
        Add("新增", "在当前帧之后创建空白帧", () => AddAnimationFrame(false));
        Add("复制", "复制当前帧及图层", () => AddAnimationFrame(true));
        Add("删除", "删除当前帧（至少保留一帧）", DeleteAnimationFrame);
        Add("导入图", "将与画布同尺寸的图片导入为新帧", ImportAnimationFrame, 76);
        Add("精灵表", "将所有帧按网格导出为 PNG", ExportSpriteSheet, 76);
        Add("序列帧", "将全部帧逐张导出为 PNG 序列到新目录", ExportFrameSequence, 76);
        Add("←", "将当前帧左移（可撤销）", () => MoveAnimationFrame(_activeFrame, _activeFrame - 1), 38);
        Add("→", "将当前帧右移（可撤销）", () => MoveAnimationFrame(_activeFrame, _activeFrame + 1), 38);
        _playButton = Add("播放", "循环播放或停止动画", ToggleAnimation);
        _onionCheck = new CheckBox { Text = "洋葱皮", Width = 76, Height = 30, ForeColor = Color.FromArgb(205, 218, 234), AccessibleName = "显示洋葱皮" };
        _toolTip.SetToolTip(_onionCheck, "显示前一帧（红）和后一帧（蓝）；播放和导出时不显示");
        _onionCheck.CheckedChanged += (_, _) => UpdateOnionSkin();
        bar.Controls.Add(_onionCheck);
        bar.Controls.Add(new Label { Text = "帧/秒", Width = 48, Height = 30, ForeColor = Color.FromArgb(205, 218, 234), TextAlign = ContentAlignment.MiddleLeft });
        _fpsControl = new NumericUpDown { Minimum = 1, Maximum = ProjectFile.MaxFps, Value = 8, Width = 55, Margin = new Padding(0, 3, 0, 0), AccessibleName = "每秒帧数" };
        _fpsControl.ValueChanged += (_, _) =>
        {
            if (_updatingFrames) return;
            _fps = (int)_fpsControl.Value;
            _animationTimer.Interval = AnimationClock.PollIntervalMs(_fps);
            if (_playing) { _playStartFrame = _activeFrame; _playClock.Restart(); }   // 播放中改帧率：从当前帧重新计时，避免按旧速率跳到远处
            SetDirty();
        };
        bar.Controls.Add(_fpsControl);
        _animationTimer.Interval = AnimationClock.PollIntervalMs(_fps);
        _animationTimer.Tick += (_, _) =>
        {
            if (!_playing || _canvas.InteractionInProgress || _frames.Count <= 1) return;
            int index = AnimationClock.FrameIndex(_playClock.ElapsedMilliseconds, _fps, _frames.Count, _playStartFrame);
            if (index != _activeFrame) SwitchFrame(index);
        };
        var strip = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(5, 0, 5, 0), AccessibleName = "帧缩略图滚动区域" };
        _timeline = new FrameTimeline { Location = new Point(5, 0) };
        _timeline.FrameSelected += index => { if (!_playing) SwitchFrame(index); };
        _timeline.FrameMoved += MoveAnimationFrame;
        strip.Controls.Add(_timeline);
        root.Controls.Add(strip); root.Controls.Add(bar);
        RefreshAnimationControls();
        return root;
    }

    private void RefreshAnimationControls()
    {
        if (_timeline is null || _fpsControl is null) return;
        _updatingFrames = true;
        _timeline.UpdateFrames(_frames, _activeFrame);
        _fpsControl.Value = _fps;
        _updatingFrames = false;
        UpdateOnionSkin();
    }

    private void UpdateOnionSkin()
    {
        if (_canvas is null) return;
        bool show = !_playing && _onionCheck?.Checked == true;
        _canvas.SetOnionFrames(show && _activeFrame > 0 ? _frames[_activeFrame - 1] : null,
            show && _activeFrame + 1 < _frames.Count ? _frames[_activeFrame + 1] : null);
    }

    private void MoveAnimationFrame(int source, int target)
    {
        if (_playing || _canvas.InteractionInProgress || source < 0 || source >= _frames.Count || target < 0 || target >= _frames.Count || source == target) return;
        RecordStructure();
        var active = _frames[_activeFrame]; var moved = _frames[source];
        _frames.RemoveAt(source); _frames.Insert(target, moved); _activeFrame = _frames.IndexOf(active);
        RefreshAnimationControls(); SetDirty();
    }

    private void SwitchFrame(int index)
    {
        if (index < 0 || index >= _frames.Count || _canvas.InteractionInProgress) return;
        SyncCurrentFrame();
        _activeFrame = index;
        _layers = _frames[index].Layers;
        _activeLayer = _frames[index].ActiveLayer;
        _grid = _layers[_activeLayer].Grid;
        _canvas.Layers = _layers;
        _canvas.Editable = !_playing && _layers[_activeLayer].Visible;
        _canvas.SetGrid(_grid);
        RefreshLayerList(); UpdateUndoButtons(); UpdateStatus(); RefreshAnimationControls();
    }

    private void AddAnimationFrame(bool copy)
    {
        if (_playing || _canvas.InteractionInProgress) return;
        SyncCurrentFrame();
        int count = copy ? _layers.Count : 1;
        if (!CanAddFrame(count)) { MessageBox.Show(this, "已达到动画帧或总像素上限。", "动画帧"); return; }
        var layers = new List<Layer>();
        if (copy)
        {
            foreach (var source in _layers)
            {
                var grid = new PixelGrid(_grid.Width, _grid.Height); grid.Restore(source.Grid.Snapshot());
                var layer = new Layer(source.Name, grid, source.Visible); layer.SetHistoryBudget(_historyBudget); layers.Add(layer);
            }
        }
        else { var layer = new Layer("图层 1", new PixelGrid(_grid.Width, _grid.Height)); layer.SetHistoryBudget(_historyBudget); layers.Add(layer); }
        RecordStructure();
        int next = _activeFrame + 1;
        _frames.Insert(next, new AnimationFrame(layers, copy ? _activeLayer : 0));
        SwitchFrame(next); SetDirty();
    }

    private void DeleteAnimationFrame()
    {
        if (_playing || _canvas.InteractionInProgress) return;
        if (_frames.Count == 1) { MessageBox.Show(this, "至少保留一帧。", "动画帧"); return; }
        if (MessageBox.Show(this, "删除当前帧及其图层？（可用 Ctrl+Z 撤销）", "删除帧", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        DeleteCurrentAnimationFrame();
    }

    private void DeleteCurrentAnimationFrame()
    {
        RecordStructure();
        foreach (var layer in _layers) layer.History.Clear();
        _frames.RemoveAt(_activeFrame);
        int next = Math.Min(_activeFrame, _frames.Count - 1);
        // 当前帧已删除，不能再将旧图层同步到新位置。
        _activeFrame = next; _layers = _frames[next].Layers; _activeLayer = _frames[next].ActiveLayer;
        _grid = _layers[_activeLayer].Grid; _canvas.Layers = _layers; _canvas.Editable = _layers[_activeLayer].Visible;
        _canvas.SetGrid(_grid); RefreshLayerList(); UpdateUndoButtons(); UpdateStatus(); RefreshAnimationControls(); SetDirty();
    }

    private void ImportAnimationFrame()
    {
        if (_playing || _canvas.InteractionInProgress) return;
        if (!CanAddFrame(1)) { MessageBox.Show(this, "已达到动画帧或总像素上限。", "动画帧"); return; }
        using var dialog = new OpenFileDialog { Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp", Title = "导入图片作为新帧" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            ImportAnimationFrameFromPath(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        { MessageBox.Show(this, "无法导入图片：" + ex.Message, "导入帧", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void ExportSpriteSheet()
    {
        if (_playing || _canvas.InteractionInProgress) return;
        using var options = new Form { Text = "导出精灵表", ClientSize = new Size(300, 115), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false };
        var label = new Label { Text = "每行帧数：", AutoSize = true, Location = new Point(14, 20) };
        var columns = new NumericUpDown { Minimum = 1, Maximum = 64, Value = (int)Math.Ceiling(Math.Sqrt(_frames.Count)), Location = new Point(130, 16), Width = 125, AccessibleName = "精灵表每行帧数" };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(84, 66), Width = 80 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(178, 66), Width = 80 };
        options.Controls.AddRange(new Control[] { label, columns, ok, cancel }); options.AcceptButton = ok; options.CancelButton = cancel;
        if (options.ShowDialog(this) != DialogResult.OK) return;
        using var dialog = new SaveFileDialog { Filter = "PNG 精灵表 (*.png)|*.png", InitialDirectory = OutputPaths.DirectoryPath(".png", animation: true), FileName = "sprite-sheet.png" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            SyncCurrentFrame();
            int scale = _scaleCombo.SelectedIndex >= 0 ? ExportScales[_scaleCombo.SelectedIndex] : 1;
            SpriteSheetExport.Save(dialog.FileName, _frames, (int)columns.Value, scale, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException or System.Runtime.InteropServices.ExternalException)
        { MessageBox.Show(this, "精灵表导出失败：" + ex.Message, "导出精灵表", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    /// <summary>逐帧导出 PNG 序列：自动在动画目录下新建唯一子目录，不覆盖既有文件。</summary>
    private void ExportFrameSequence()
    {
        if (_playing || _canvas.InteractionInProgress) return;
        try
        {
            SyncCurrentFrame();
            int scale = _scaleCombo.SelectedIndex >= 0 ? ExportScales[_scaleCombo.SelectedIndex] : 1;
            string baseName = Path.GetFileNameWithoutExtension(_projectPath) ?? "pixel-art";
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "pixel-art";
            string root = OutputPaths.DirectoryPath(".png", animation: true);
            string directory = Path.Combine(root, $"{Sanitize(baseName)}-序列帧-{DateTime.Now:HHmmss}");
            int suffix = 2;
            while (Directory.Exists(directory)) directory = Path.Combine(root, $"{Sanitize(baseName)}-序列帧-{DateTime.Now:HHmmss}-{suffix++}");
            var result = FrameSequenceExport.Save(directory, "frame", _frames, scale);
            MessageBox.Show(this, $"{result.FrameCount} 帧（{result.Width}×{result.Height}，{scale}×）已导出：\n{directory}", "导出序列帧", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException or System.Runtime.InteropServices.ExternalException)
        { MessageBox.Show(this, "序列帧导出失败：" + ex.Message, "导出序列帧", MessageBoxButtons.OK, MessageBoxIcon.Error); }

        static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
            return name.Trim().Length == 0 ? "pixel-art" : name.Trim();
        }
    }

    private void ImportAnimationFrameFromPath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("图片路径必须是绝对路径。");
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp")) throw new ArgumentException("仅支持 PNG、JPEG 或 BMP 图片。");
        if (!CanAddFrame(1)) throw new InvalidOperationException("已达到动画帧或总像素上限。");
        using var source = Image.FromFile(path);
        var frame = AnimationFrame.FromImage(source, _grid.Width, _grid.Height);
        SyncCurrentFrame();
        frame.Layers[0].SetHistoryBudget(_historyBudget);
        RecordStructure();
        int next = _activeFrame + 1; _frames.Insert(next, frame);
        SwitchFrame(next); SetDirty();
    }

    private void ToggleAnimation()
    {
        if (_canvas.InteractionInProgress) return;
        _playing = !_playing;
        _playButton.Text = _playing ? "停止" : "播放";
        _leftSidebar.Enabled = _rightSidebar.Enabled = _commandBar.Enabled = !_playing;
        if (_timeline is not null) _timeline.Enabled = !_playing;
        UpdateOnionSkin();
        _canvas.Editable = !_playing && _layers[_activeLayer].Visible;
        if (_playing)
        {
            _playStartFrame = _activeFrame;
            _playClock.Restart();
            _animationTimer.Interval = AnimationClock.PollIntervalMs(_fps);
            _animationTimer.Start();
        }
        else
        {
            _animationTimer.Stop();
            _playClock.Reset();
        }
    }
}
