using System.Text.Json.Nodes;

namespace PixelCanvas;

public sealed partial class MainForm
{
    private object ExportUsingPreset(ExportPreset preset)
    {
        if (_playing || _canvas.InteractionInProgress) throw new InvalidOperationException("请先停止播放或结束画布操作。");
        string path = ExportPresets.NewOutputPath(preset);
        SyncCurrentFrame();
        object? layout = null;
        if (preset.Format == "png") McpCanvas.ExportPng(path, _layers, preset.Scale);
        else if (preset.Format == "gif") GifExport.Save(path, _frames, preset.Fps, preset.Scale);
        else layout = SpriteSheetExport.Save(path, _frames, preset.Columns, preset.Scale).ToData();
        return new { path, format = preset.Format, scale = preset.Scale, fps = preset.Format == "gif" ? (int?)preset.Fps : null, layout };
    }

    private void ShowExportPresets()
    {
        using var dialog = new Form { Text = "导出预设", ClientSize = new Size(480, 345), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
        var existing = new ComboBox { Left = 145, Top = 14, Width = 305, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "已保存的预设" };
        var name = new TextBox { Left = 145, Top = 48, Width = 305, AccessibleName = "预设名称" };
        var format = new ComboBox { Left = 145, Top = 82, Width = 130, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "导出格式" };
        format.Items.AddRange(new object[] { "PNG 当前帧", "GIF 动画", "PNG 精灵表" }); format.SelectedIndex = 0;
        var scale = new NumericUpDown { Left = 145, Top = 116, Width = 130, Minimum = 1, Maximum = 16, Value = _scaleCombo.SelectedIndex >= 0 ? ExportScales[_scaleCombo.SelectedIndex] : 1, AccessibleName = "预设放大倍数" };
        var fps = new NumericUpDown { Left = 145, Top = 150, Width = 130, Minimum = 1, Maximum = 30, Value = _fps, AccessibleName = "预设 GIF 帧率" };
        var columns = new NumericUpDown { Left = 145, Top = 184, Width = 130, Minimum = 1, Maximum = 64, Value = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(_frames.Count))), AccessibleName = "预设精灵表列数" };
        var directory = new TextBox { Left = 145, Top = 218, Width = 238, AccessibleName = "预设输出目录" };
        var browse = new Button { Text = "选择…", Left = 390, Top = 216, Width = 60 };
        browse.Click += (_, _) => { using var picker = new FolderBrowserDialog { Description = "选择输出目录" }; if (picker.ShowDialog(dialog) == DialogResult.OK) directory.Text = picker.SelectedPath; };
        var labels = new[] { "已保存预设", "预设名称", "导出格式", "放大倍数", "GIF 帧/秒", "精灵表列数", "输出目录（可留空）" };
        for (int i = 0; i < labels.Length; i++) dialog.Controls.Add(new Label { Text = labels[i], Left = 12, Top = 18 + i * 34, Width = 130, Height = 25 });
        var save = new Button { Text = "保存预设", Left = 12, Top = 275, Width = 100 };
        var delete = new Button { Text = "删除预设", Left = 122, Top = 275, Width = 100 };
        var export = new Button { Text = "按设置导出", Left = 232, Top = 275, Width = 108 };
        var close = new Button { Text = "关闭", Left = 350, Top = 275, Width = 100, DialogResult = DialogResult.Cancel };
        List<ExportPreset> presets = new();
        void LoadList(string? select = null)
        {
            presets = ExportPresets.List(); existing.Items.Clear();
            foreach (var p in presets) existing.Items.Add(p.Name);
            if (select is not null) { int i = presets.FindIndex(p => p.Name.Equals(select, StringComparison.OrdinalIgnoreCase)); if (i >= 0) existing.SelectedIndex = i; }
        }
        existing.SelectedIndexChanged += (_, _) =>
        {
            if (existing.SelectedIndex < 0) return;
            var p = presets[existing.SelectedIndex]; name.Text = p.Name; format.SelectedIndex = p.Format == "png" ? 0 : p.Format == "gif" ? 1 : 2;
            scale.Value = p.Scale; fps.Value = p.Fps; columns.Value = p.Columns; directory.Text = p.Directory;
        };
        ExportPreset Current() => new(name.Text.Trim(), format.SelectedIndex == 0 ? "png" : format.SelectedIndex == 1 ? "gif" : "sheet", (int)scale.Value, (int)fps.Value, (int)columns.Value, directory.Text.Trim());
        void Report(Action action) { try { action(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException or System.Runtime.InteropServices.ExternalException) { MessageBox.Show(dialog, ex.Message, "导出预设", MessageBoxButtons.OK, MessageBoxIcon.Warning); } }
        save.Click += (_, _) => Report(() => { var p = Current(); bool exists = presets.Any(x => x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)); if (exists && MessageBox.Show(dialog, "替换同名预设？", "导出预设", MessageBoxButtons.YesNo) != DialogResult.Yes) return; ExportPresets.Put(p, exists); LoadList(p.Name); });
        delete.Click += (_, _) => Report(() => { if (existing.SelectedIndex < 0) throw new ArgumentException("请选择要删除的预设。"); string selected = presets[existing.SelectedIndex].Name; if (MessageBox.Show(dialog, $"删除预设“{selected}”？", "导出预设", MessageBoxButtons.YesNo) != DialogResult.Yes) return; ExportPresets.Delete(selected); LoadList(); });
        export.Click += (_, _) => Report(() => { var result = JsonSerializerNode(ExportUsingPreset(Current())); MessageBox.Show(dialog, "已导出到：\n" + result["path"]!.GetValue<string>(), "导出完成"); });
        dialog.Controls.AddRange(new Control[] { existing, name, format, scale, fps, columns, directory, browse, save, delete, export, close });
        dialog.CancelButton = close;
        try { LoadList(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException) { MessageBox.Show(this, "无法读取导出预设：" + ex.Message, "导出预设", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        dialog.ShowDialog(this);
    }

    private static JsonNode JsonSerializerNode(object value) => System.Text.Json.JsonSerializer.SerializeToNode(value)!;
}
