using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PixelCanvas;

public sealed partial class MainForm
{
    private CancellationTokenSource? _mcpCancellation;
    private const int MaxPipeRequestChars = 1_048_576;

    private static async Task<string?> ReadPipeRequestAsync(StreamReader reader, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var result = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
            if (count == 0) return result.Length == 0 ? null : throw new IOException("请求未以换行结束。");
            int end = Array.IndexOf(buffer, '\n', 0, count);
            int length = end >= 0 ? end : count;
            if (result.Length + length > MaxPipeRequestChars) throw new IOException("请求超过长度限制。");
            result.Append(buffer, 0, length);
            if (end >= 0) return result.ToString().TrimEnd('\r');
        }
    }

    private void StartMcpPipe()
    {
        _mcpCancellation = new CancellationTokenSource();
        _ = RunMcpPipeAsync(_mcpCancellation.Token);
    }

    /// <summary>
    /// 管道服务后台循环：在后台线程接受本地当前用户级命名管道连接，每个请求经
    /// <see cref="System.Windows.Forms.Control.InvokeAsync"/> 封送到 UI 线程串行执行
    /// <see cref="HandleMcpRequest"/>。恢复中 / 播放中 / 画布交互中的并发闸门都在 UI 线程判定。
    /// 取消令牌只在等待连接与管道读写阶段生效；委托一旦进入 UI 线程执行，便不再响应取消。
    /// 管道实例数为 1，同一时刻只服务一个 MCP 客户端，请求在 UI 线程排队串行处理。
    /// </summary>
    private async Task RunMcpPipeAsync(CancellationToken cancellation)
    {
        string name = $"PixelCanvas.Mcp.{Environment.ProcessId}";
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                string? line = await ReadPipeRequestAsync(reader, cancellation).ConfigureAwait(false);
                if (line is null) continue;
                object response;
                try
                {
                    var request = JsonNode.Parse(line) as JsonObject ?? throw new ArgumentException("无效请求。");
                    response = await InvokeAsync(() => HandleMcpRequest(request), cancellation).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    response = new { ok = false, error = ex.Message };
                }
                using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                writeTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                await writer.WriteLineAsync(JsonSerializer.Serialize(response).AsMemory(), writeTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) when (cancellation.IsCancellationRequested) { break; }
        }
    }

    private object HandleMcpRequest(JsonObject request)
    {
        string action = McpCanvas.Text(request, "action");
        var args = request["arguments"] as JsonObject ?? new JsonObject();
        if (_recoveryPending)
        {
            if (action == "info") return new { ok = true, data = new { ready = false, status = "recovery_pending", message = "画布正在等待用户处理自动恢复提示。" } };
            throw new InvalidOperationException("画布正在等待用户处理自动恢复提示，请先在窗口中完成选择。");
        }
        if (_playing && action is not ("info" or "view_get" or "draw_options_get" or "selection_get" or "read" or "read_composite" or "preview" or "presets_list" or "palette_get" or "palette_save" or "reference_info" or "reference_image" or "reference_load" or "reference_settings" or "reference_clear" or "playback" or "onion_skin")) throw new InvalidOperationException("动画播放中，请先停止播放再修改画布。");
        if (_canvas.InteractionInProgress) throw new InvalidOperationException("画布正在操作，请稍后重试。");
        return action switch
        {
            "info" => new { ok = true, data = FrameStatusFromMcp() },
            "reuse_info" => new { ok = true, data = new { reusable = IsReusableCanvas(), width = _grid.Width, height = _grid.Height } },
            "select_layer" => SelectLayerFromMcp(args),
            "clear_layer" => ClearLayerFromMcp(args),
            "resize" => ResizeFromMcp(args),
            "selection_get" => new { ok = true, data = SelectionInfo() },
            "selection_set" => SelectionSetFromMcp(args),
            "selection_copy" => SelectionCopyFromMcp(),
            "selection_paste" => SelectionPasteFromMcp(),
            "draw_options_get" => new { ok = true, data = DrawOptionsInfo() },
            "draw_options_set" => DrawOptionsFromMcp(args),
            "view_get" => new { ok = true, data = ViewInfo() },
            "view_set" => SetViewFromMcp(args),
            "read" => new { ok = true, data = McpCanvas.Read(args, _layers, _activeLayer) },
            "read_composite" => new { ok = true, data = McpCanvas.ReadComposite(args, _layers) },
            "preview" => PreviewFromMcp(args),
            "reference_info" => new { ok = true, data = _canvas.ReferenceInfo() },
            "reference_image" => new { ok = true, data = _canvas.ReferenceImage(McpCanvas.Number(args, "max_side", 512)) },
            "reference_load" => LoadReferenceFromMcp(args),
            "reference_settings" => ReferenceSettingsFromMcp(args),
            "reference_clear" => ClearReferenceFromMcp(),
            "add_frame" => AddFrameFromMcp(args),
            "batch_frames" => BatchFramesFromMcp(args),
            "select_frame" => SelectFrameFromMcp(args),
            "move_frame" => MoveFrameFromMcp(args),
            "onion_skin" => OnionSkinFromMcp(args),
            "delete_frame" => DeleteFrameFromMcp(args),
            "import_frame" => ImportFrameFromMcp(args),
            "playback" => PlaybackFromMcp(args),
            "paint" => PaintFromMcp(args),
            "replace_color" => ReplaceColorFromMcp(args),
            "palette_get" => new { ok = true, data = new { colors = _palette.Select(c => "#" + c).ToArray() } },
            "palette_set" => SetPaletteFromMcp(args),
            "palette_load" => LoadPaletteFromMcp(args),
            "palette_save" => SavePaletteFromMcp(args),
            "batch_paint" => BatchPaintFromMcp(args),
            "create_artwork" => CreateArtworkFromMcp(args),
            "shape" => ShapeFromMcp(args),
            "fill" => FillFromMcp(args, false),
            "gradient" => FillFromMcp(args, true),
            "copy_region" => TransferFromMcp(args, false),
            "move_region" => TransferFromMcp(args, true),
            "delete_layer" => ManageLayerFromMcp(args, "delete"),
            "move_layer" => ManageLayerFromMcp(args, "move"),
            "layer_visibility" => ManageLayerFromMcp(args, "visibility"),
            "add_layer" => AddLayerFromMcp(),
            "save_copy" => SaveCopyFromMcp(args),
            "export" => ExportFromMcp(args),
            "export_gif" => ExportGifFromMcp(args),
            "export_sprite_sheet" => ExportSpriteSheetFromMcp(args),
            "export_svg" => ExportSvgFromMcp(args),
            "export_frames" => ExportFrameSequenceFromMcp(args),
            "presets_list" => new { ok = true, data = ExportPresets.List().Select(PresetData).ToArray() },
            "presets_put" => PutPresetFromMcp(args),
            "presets_delete" => DeletePresetFromMcp(args),
            "export_preset" => ExportPresetFromMcp(args),
            "open_project" => OpenProjectFromMcp(args),
            "save_project" => SaveProjectFromMcp(args),
            "save_export" => SaveExportFromMcp(args),
            "undo" => HistoryFromMcp(args, false),
            "redo" => HistoryFromMcp(args, true),
            _ => throw new ArgumentException("不支持的操作。"),
        };
    }

    private JsonObject FrameStatusFromMcp()
    {
        var status = JsonSerializer.SerializeToNode(McpCanvas.Info(_layers, _activeLayer, _frames.Count, _activeFrame, _fps, _playing))!.AsObject();
        bool enabled = _onionCheck?.Checked == true;
        status["onionEnabled"] = enabled;
        status["onionVisible"] = enabled && !_playing && _frames.Count > 1;
        return status;
    }

    private object LoadReferenceFromMcp(JsonObject args)
    {
        string path = McpCanvas.Text(args, "path");
        if (!Path.IsPathFullyQualified(path) || Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif"))
            throw new ArgumentException("参考图必须是 PNG、JPEG、BMP 或 GIF 的绝对路径。");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 64L * 1024 * 1024) throw new ArgumentException("参考图文件超过 64 MiB。");
        using var image = Image.FromStream(file);
        if (image.Width < 1 || image.Height < 1 || (long)image.Width * image.Height > 33_554_432) throw new ArgumentException("参考图超过 33554432 像素。");
        _canvas.SetReferenceImage(new Bitmap(image), Path.GetFullPath(path));
        return new { ok = true, data = _canvas.ReferenceInfo() };
    }

    private object ReferenceSettingsFromMcp(JsonObject args)
    {
        bool? visible = null;
        if (args["visible"] is not null)
        {
            if (args["visible"] is not JsonValue v || !v.TryGetValue<bool>(out bool flag)) throw new ArgumentException("visible 必须是布尔值。");
            visible = flag;
        }
        int? opacity = args["opacity"] is null ? null : McpCanvas.Number(args, "opacity");
        if (opacity is < 0 or > 100) throw new ArgumentException("opacity 必须为 0–100 的整数。");
        if (visible is null && opacity is null) throw new ArgumentException("请指定 visible 或 opacity。");
        if (visible.HasValue) _referenceVisible.Checked = visible.Value;
        if (opacity.HasValue) _referenceOpacity.Value = opacity.Value;
        return new { ok = true, data = _canvas.ReferenceInfo() };
    }

    private object ClearReferenceFromMcp()
    {
        _canvas.SetReferenceImage(null);
        return new { ok = true, data = _canvas.ReferenceInfo() };
    }

    private object OnionSkinFromMcp(JsonObject args)
    {
        if (args["enabled"] is not JsonValue value || !value.TryGetValue<bool>(out bool enabled)) throw new ArgumentException("enabled 必须是布尔值。");
        if (_onionCheck is null) throw new InvalidOperationException("动画工具栏尚未初始化。");
        _onionCheck.Checked = enabled;
        return new { ok = true, data = FrameStatusFromMcp() };
    }

    private object MoveFrameFromMcp(JsonObject args)
    {
        int source = McpCanvas.Number(args, "frame"), target = McpCanvas.Number(args, "target_index");
        if (source < 0 || source >= _frames.Count || target < 0 || target >= _frames.Count) throw new ArgumentException("帧索引超出范围（从 0 开始）。");
        MoveAnimationFrame(source, target);
        return new { ok = true, data = new { changed = source != target, canvas = FrameStatusFromMcp() } };
    }

    private object PreviewFromMcp(JsonObject args)
    {
        int maxSide = McpCanvas.Number(args, "max_side", 512);
        if (maxSide is < 1 or > 512) throw new ArgumentException("max_side 必须为 1–512。");
        double ratio = Math.Min(1d, (double)maxSide / Math.Max(_grid.Width, _grid.Height));
        int width = Math.Max(1, (int)Math.Round(_grid.Width * ratio)), height = Math.Max(1, (int)Math.Round(_grid.Height * ratio));
        using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            graphics.DrawImage(_canvas.NativeBitmap, new Rectangle(0, 0, width, height), new Rectangle(0, 0, _grid.Width, _grid.Height), GraphicsUnit.Pixel);
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return new { ok = true, data = new { sourceWidth = _grid.Width, sourceHeight = _grid.Height, width, height, activeFrame = _activeFrame, imageBase64 = Convert.ToBase64String(stream.ToArray()) } };
    }

    private object BatchFramesFromMcp(JsonObject args)
    {
        if (args["frames"] is not JsonArray specs || specs.Count is < 1 or > 32) throw new ArgumentException("frames 须包含 1–32 帧。");
        if (_frames.Count + specs.Count > ProjectFile.MaxFrames) throw new ArgumentException($"动画最多支持 {ProjectFile.MaxFrames} 帧。");
        int fps = McpCanvas.Number(args, "fps", _fps);
        if (fps is < 1 or > ProjectFile.MaxFps) throw new ArgumentException($"帧率必须为 1–{ProjectFile.MaxFps}。");
        long total = _frames.Sum(f => (long)f.Layers.Count * _grid.Width * _grid.Height);
        int operationCount = 0, pixelCount = 0;
        var staged = new List<AnimationFrame>();
        IReadOnlyList<Layer> previous = _layers;
        int previousActive = _activeLayer;
        for (int i = 0; i < specs.Count; i++)
        {
            try
            {
                var spec = specs[i] as JsonObject ?? throw new ArgumentException("帧必须是对象。");
                bool copy = spec["copy"]?.GetValue<bool>() ?? false;
                int layerCount = copy ? previous.Count : 1;
                total += (long)layerCount * _grid.Width * _grid.Height;
                if (total > ProjectFile.MaxAnimationPixels) throw new ArgumentException("动画帧总像素超过上限。");
                var layers = new List<Layer>();
                if (copy)
                {
                    foreach (var source in previous)
                    {
                        var grid = new PixelGrid(_grid.Width, _grid.Height); grid.Restore(source.Grid.Snapshot());
                        layers.Add(new Layer(source.Name, grid, source.Visible));
                    }
                }
                else layers.Add(new Layer("图层 1", new PixelGrid(_grid.Width, _grid.Height)));
                int active = copy ? previousActive : 0;
                if (spec["operations"] is JsonArray operations && operations.Count > 0)
                {
                    operationCount += operations.Count;
                    foreach (var op in operations) if (op is JsonObject obj && obj["pixels"] is JsonArray pixels) pixelCount += pixels.Count;
                    if (operationCount > 256 || pixelCount > 16384) throw new ArgumentException("单次最多 256 个操作和 16384 个像素。");
                    McpCanvas.Batch(new JsonObject { ["operations"] = operations.DeepClone() }, layers, active);
                }
                else if (spec["operations"] is not null && spec["operations"] is not JsonArray) throw new ArgumentException("operations 必须是数组。");
                staged.Add(new AnimationFrame(layers, active)); previous = layers; previousActive = active;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
            { throw new ArgumentException($"第 {i + 1} 帧失败：{ex.Message}", ex); }
        }
        foreach (var frame in staged) foreach (var layer in frame.Layers) layer.SetHistoryBudget(_historyBudget);
        SyncCurrentFrame();
        RecordStructure();
        int start = _frames.Count; _frames.AddRange(staged);
        SwitchFrame(_frames.Count - 1);
        if (_fps != fps) _fpsControl.Value = fps;
        SetDirty();
        return new { ok = true, data = new { added = staged.Count, firstFrame = start, canvas = FrameStatusFromMcp() } };
    }

    private object AddFrameFromMcp(JsonObject args)
    {
        bool copy = args["copy"]?.GetValue<bool>() ?? false;
        if (!CanAddFrame(copy ? _layers.Count : 1)) throw new InvalidOperationException("已达到动画帧或总像素上限。");
        AddAnimationFrame(copy);
        return new { ok = true, data = FrameStatusFromMcp() };
    }

    private object SelectFrameFromMcp(JsonObject args)
    {
        int index = McpCanvas.Number(args, "frame");
        if (index < 0 || index >= _frames.Count) throw new ArgumentException("动画帧索引超出范围（从 0 开始）。");
        SwitchFrame(index);
        return new { ok = true, data = FrameStatusFromMcp() };
    }

    private object DeleteFrameFromMcp(JsonObject args)
    {
        if (args["confirm"]?.GetValue<bool>() != true) throw new ArgumentException("删除帧需要传入 confirm=true 确认（确认后仍可用 canvas_undo 撤销）。");
        if (_frames.Count <= 1) throw new InvalidOperationException("至少保留一帧。");
        DeleteCurrentAnimationFrame();
        return new { ok = true, data = FrameStatusFromMcp() };
    }

    private object ImportFrameFromMcp(JsonObject args)
    {
        ImportAnimationFrameFromPath(McpCanvas.Text(args, "path"));
        return new { ok = true, data = FrameStatusFromMcp() };
    }

    private object PlaybackFromMcp(JsonObject args)
    {
        if (args["playing"] is not JsonValue value || !value.TryGetValue<bool>(out bool playing)) throw new ArgumentException("playing 必须是布尔值。");
        int fps = McpCanvas.Number(args, "fps", _fps);
        if (fps is < 1 or > ProjectFile.MaxFps) throw new ArgumentException($"帧率必须为 1–{ProjectFile.MaxFps}。");
        if (_fps != fps) _fpsControl.Value = fps;
        if (_playing != playing) ToggleAnimation();
        return new { ok = true, data = FrameStatusFromMcp() };
    }

    private object HistoryFromMcp(JsonObject args, bool redo)
    {
        // 未指定图层时按动作先后自动选择：可能落在图层像素历史，也可能落在帧结构快照。
        if (args["layer"] is null)
        {
            bool changed = StepHistory(redo);
            return new { ok = true, data = new { changed, layer = _activeLayer, scope = "auto" } };
        }
        int index = McpCanvas.LayerIndex(args, _layers, _activeLayer);
        var layer = _layers[index];
        var history = layer.History;
        if (redo ? !history.CanRedo : !history.CanUndo) return new { ok = true, data = new { changed = false, layer = index } };
        int[] current = layer.Grid.Snapshot();
        layer.Grid.Restore(redo ? history.Redo(current) : history.Undo(current));
        _canvas.RefreshFromData();
        UpdateUndoButtons();
        SetDirty();
        return new { ok = true, data = new { changed = true, layer = index } };
    }

    private object ShapeFromMcp(JsonObject args)
    {
        int layer = McpCanvas.LayerIndex(args, _layers, _activeLayer);
        bool changed = McpCanvas.Shape(args, _layers, _activeLayer, before => _layers[layer].History.Commit(before));
        if (changed) { _canvas.RefreshFromData(); UpdateUndoButtons(); SetDirty(); }
        return new { ok = true, data = new { changed, layer } };
    }

    private object FillFromMcp(JsonObject args, bool gradient)
    {
        int layer = McpCanvas.LayerIndex(args, _layers, _activeLayer);
        bool changed = McpCanvas.Fill(args, _layers, _activeLayer, gradient, before => _layers[layer].History.Commit(before));
        if (changed) { _canvas.RefreshFromData(); UpdateUndoButtons(); SetDirty(); }
        return new { ok = true, data = new { changed, layer } };
    }

    private object TransferFromMcp(JsonObject args, bool move)
    {
        int layer = McpCanvas.LayerIndex(args, _layers, _activeLayer);
        bool changed = McpCanvas.TransferRegion(args, _layers, _activeLayer, move, before => _layers[layer].History.Commit(before));
        if (changed) { _canvas.RefreshFromData(); UpdateUndoButtons(); SetDirty(); }
        return new { ok = true, data = new { changed, layer } };
    }

    private object ManageLayerFromMcp(JsonObject args, string action)
    {
        var before = CaptureStructure();
        var (active, changed) = McpCanvas.ManageLayer(args, _layers, _activeLayer, action);
        _activeLayer = active;
        if (changed) CommitStructure(before);
        if (changed) { RefreshLayerList(); ActivateLayer(); SetDirty(); }
        return new { ok = true, data = new { changed, canvas = McpCanvas.Info(_layers, _activeLayer, _frames.Count, _activeFrame, _fps, _playing) } };
    }

    private object AddLayerFromMcp()
    {
        if (_layers.Count >= 32) throw new InvalidOperationException("最多支持 32 个图层。");
        if (_frames.Count > 1 && _frames.Sum(f => (long)f.Layers.Count * _grid.Width * _grid.Height) + (long)_grid.Width * _grid.Height > ProjectFile.MaxAnimationPixels) throw new InvalidOperationException("动画帧总像素已达上限。");
        AddLayer();
        return new { ok = true, data = McpCanvas.Info(_layers, _activeLayer, _frames.Count, _activeFrame, _fps, _playing) };
    }

    private object SaveCopyFromMcp(JsonObject args)
    {
        string path = McpCanvas.NewPath(args, "path", ".pxproj", _projectPath, _frames.Count > 1);
        SyncCurrentFrame();
        ProjectFile.Save(path, _layers, _activeLayer, _palette, overwrite: false, frames: _frames, activeFrame: _activeFrame, framesPerSecond: _fps);
        return new { ok = true, data = new { path } };
    }

    private static string ProjectPathFromMcp(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetExtension(path), ".pxproj", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("工程路径必须是 .pxproj 文件的绝对路径。");
        return Path.GetFullPath(path);
    }

    private object OpenProjectFromMcp(JsonObject args)
    {
        if (_dirty) throw new InvalidOperationException("当前画布有未保存的修改，请先保存或另开窗口。");
        string path = ProjectPathFromMcp(McpCanvas.Text(args, "path"));
        byte[]? before = Fingerprint(path);
        var project = ProjectFile.TryLoad(path, out string error) ?? throw new ArgumentException("无法读取工程：" + error);
        byte[]? after = Fingerprint(path);
        if (before is null || !SameFingerprint(before, after)) throw new IOException("工程在读取时发生变化，请重试。");
        ApplyLoadedProject(project, path, after!);
        return new { ok = true, data = new { path, canvas = McpCanvas.Info(_layers, _activeLayer, _frames.Count, _activeFrame, _fps, _playing) } };
    }

    private object SaveProjectFromMcp(JsonObject args)
    {
        string? requested = args["path"] is null ? null : ProjectPathFromMcp(McpCanvas.Text(args, "path"));
        if (_projectPath is not null && requested is not null && !string.Equals(Path.GetFullPath(_projectPath), requested, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("当前工程已有路径；另存请使用 canvas_save_copy。");
        string path = _projectPath is null ? McpCanvas.NewPath(args, "path", ".pxproj", animation: _frames.Count > 1) : ProjectPathFromMcp(_projectPath);
        WriteProjectChecked(path, _projectPath is null ? null : _projectFingerprint);
        _projectPath = path;
        _dirty = false;
        DeleteOwnRecovery();
        UpdateTitle();
        return new { ok = true, data = new { path } };
    }

    private object ExportFromMcp(JsonObject args)
    {
        string path = McpCanvas.NewPath(args, "path", ".png");
        McpCanvas.ExportPng(path, _layers, McpCanvas.Number(args, "scale", 1));
        return new { ok = true, data = new { path } };
    }

    private object ExportGifFromMcp(JsonObject args)
    {
        string path = McpCanvas.NewPath(args, "path", ".gif", animation: true);
        SyncCurrentFrame();
        GifExport.Save(path, _frames, _fps, McpCanvas.Number(args, "scale", 1));
        return new { ok = true, data = new { path, frameCount = _frames.Count, fps = _fps, loop = true } };
    }

    private object ExportSpriteSheetFromMcp(JsonObject args)
    {
        string path = McpCanvas.NewPath(args, "path", ".png", animation: true);
        SyncCurrentFrame();
        int columns = McpCanvas.Number(args, "columns", (int)Math.Ceiling(Math.Sqrt(_frames.Count)));
        var layout = SpriteSheetExport.Save(path, _frames, columns, McpCanvas.Number(args, "scale", 1));
        return new { ok = true, data = new { path, layout = layout.ToData(), order = "row-major" } };
    }

    private object ExportSvgFromMcp(JsonObject args)
    {
        string path = McpCanvas.NewPath(args, "path", ".svg");
        int rectangles = McpCanvas.ExportSvg(path, _layers);
        return new { ok = true, data = new { path, format = "svg", frame = _activeFrame, rectangles, vector = true } };
    }

    private object ExportFrameSequenceFromMcp(JsonObject args)
    {
        string directory = McpCanvas.Text(args, "directory");
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("directory 必须是绝对路径。");
        if (Directory.Exists(directory)) throw new IOException("输出目录已存在，请换一个新的绝对路径。");
        string baseName = args["base_name"] is null ? "frame" : McpCanvas.Text(args, "base_name");
        SyncCurrentFrame();
        var result = FrameSequenceExport.Save(directory, baseName, _frames, McpCanvas.Number(args, "scale", 1));
        return new { ok = true, data = result.ToData() };
    }

    private static object PresetData(ExportPreset p) => new { name = p.Name, format = p.Format, scale = p.Scale, fps = p.Fps, columns = p.Columns, directory = p.Directory };
    private object PutPresetFromMcp(JsonObject args)
    {
        string directory = args["directory"] is null ? "" : args["directory"]!.GetValue<string>();
        var preset = new ExportPreset(McpCanvas.Text(args, "name"), McpCanvas.Text(args, "format"), McpCanvas.Number(args, "scale", 1), McpCanvas.Number(args, "fps", 8), McpCanvas.Number(args, "columns", 1), directory);
        bool overwrite = args["overwrite"]?.GetValue<bool>() ?? false;
        ExportPresets.Put(preset, overwrite);
        return new { ok = true, data = PresetData(preset) };
    }

    private object DeletePresetFromMcp(JsonObject args)
    {
        if (args["confirm"]?.GetValue<bool>() != true) throw new ArgumentException("删除预设须明确传入 confirm=true。");
        string name = McpCanvas.Text(args, "name"); ExportPresets.Delete(name);
        return new { ok = true, data = new { name, deleted = true } };
    }

    private object ExportPresetFromMcp(JsonObject args)
    {
        string name = McpCanvas.Text(args, "name");
        var preset = ExportPresets.List().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("未找到该导出预设。");
        return new { ok = true, data = ExportUsingPreset(preset) };
    }

    private object BatchPaintFromMcp(JsonObject args)
    {
        int[] changedLayers = McpCanvas.Batch(args, _layers, _activeLayer);
        if (changedLayers.Length > 0) { _canvas.RefreshFromData(); UpdateUndoButtons(); SetDirty(); }
        return new { ok = true, data = new { changed = changedLayers.Length > 0, changedLayers, operationCount = ((JsonArray)args["operations"]!).Count, undo = "每个受影响的图层各一步" } };
    }

    private object ReplaceColorFromMcp(JsonObject args)
    {
        var (layer, count) = McpCanvas.ReplaceColor(args, _layers, _activeLayer);
        if (count > 0) { _canvas.RefreshFromData(); UpdateUndoButtons(); SetDirty(); }
        return new { ok = true, data = new { layer, replaced = count } };
    }

    private object SetPaletteFromMcp(JsonObject args)
    {
        var colors = PaletteFile.Validate(args["colors"]);
        ApplyPalette(colors);
        return new { ok = true, data = new { colors = _palette.Select(c => "#" + c).ToArray() } };
    }

    private object LoadPaletteFromMcp(JsonObject args)
    {
        string path = McpCanvas.Text(args, "path");
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetExtension(path), ".pxpal", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("path 必须是 .pxpal 文件的绝对路径。");
        ApplyPalette(PaletteFile.Load(path));
        return new { ok = true, data = new { path, colors = _palette.Select(c => "#" + c).ToArray() } };
    }

    private object SavePaletteFromMcp(JsonObject args)
    {
        string path = McpCanvas.NewPath(args, "path", ".pxpal");
        PaletteFile.Save(path, _palette);
        return new { ok = true, data = new { path, count = _palette.Count } };
    }

    private bool IsReusableCanvas() => !_dirty && _projectPath is null && _frames.Count == 1 && _layers.Count == 1 && !_playing && !_canvas.InteractionInProgress && !_layers[0].Grid.RawPixels.Any(pixel => pixel != Argb.Transparent);

    private object CreateArtworkFromMcp(JsonObject args)
    {
        if (!IsReusableCanvas() || _grid.Width != McpCanvas.Number(args, "width", 32) || _grid.Height != McpCanvas.Number(args, "height", 32))
            throw new InvalidOperationException("画布不再是新建的空白画布，已停止操作以保护现有内容。 ");
        if (args["layers"] is not JsonArray names || names.Count is < 1 or > 32) throw new ArgumentException("图层数量不合法。");
        var layerNames = names.Select(node => node is JsonValue v && v.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) && text.Length <= 100 ? text : throw new ArgumentException("图层名称不合法。 ")).ToArray();
        string stage = "opened";
        try
        {
            _layers[0].Name = layerNames[0];
            for (int i = 1; i < layerNames.Length; i++) { AddLayer(); _layers[_activeLayer].Name = layerNames[i]; }
            RefreshLayerList(); SetDirty();
            stage = "layers_created";
            int[] changed = McpCanvas.Batch(args, _layers, _activeLayer);
            if (changed.Length > 0) { _canvas.RefreshFromData(); UpdateUndoButtons(); SetDirty(); }
            stage = "painted";
            SaveProjectFromMcp(new JsonObject { ["path"] = McpCanvas.Text(args, "project_path") });
            return new { ok = true, data = new { success = true, completedStage = "saved", projectPath = _projectPath, changedLayers = changed, canvas = McpCanvas.Info(_layers, _activeLayer, _frames.Count, _activeFrame, _fps, _playing) } };
        }
        catch (Exception ex)
        {
            return new { ok = true, data = new { success = false, completedStage = stage, error = ex.Message, projectPath = _projectPath, canvas = McpCanvas.Info(_layers, _activeLayer, _frames.Count, _activeFrame, _fps, _playing) } };
        }
    }

    private object SaveExportFromMcp(JsonObject args)
    {
        string png = McpCanvas.NewPath(args, "png_path", ".png");
        int scale = McpCanvas.Number(args, "scale", 1);
        if (scale is < 1 or > 16 || (long)_grid.Width * _grid.Height * scale * scale > 16_777_216)
            throw new ArgumentException("放大倍数必须为 1–16，导出图片不能超过 16777216 像素。");
        var saveArgs = new JsonObject();
        if (args["project_path"] is not null) saveArgs["path"] = args["project_path"]!.DeepClone();
        SaveProjectFromMcp(saveArgs);
        try
        {
            McpCanvas.ExportPng(png, _layers, scale);
            return new { ok = true, data = new { projectSaved = true, projectPath = _projectPath, pngExported = true, pngPath = png, frame = _activeFrame } };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.ExternalException)
        {
            return new { ok = true, data = new { projectSaved = true, projectPath = _projectPath, pngExported = false, pngPath = png, frame = _activeFrame, error = ex.Message } };
        }
    }

    private object PaintFromMcp(JsonObject args)
    {
        int layer = McpCanvas.LayerIndex(args, _layers, _activeLayer);
        bool changed = McpCanvas.Paint(args, _layers, _activeLayer, before => _layers[layer].History.Commit(before));
        if (changed)
        {
            _canvas.RefreshFromData();
            UpdateUndoButtons();
            SetDirty();
        }
        return new { ok = true, data = new { changed, layer } };
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _mcpCancellation?.Cancel();
        _mcpCancellation?.Dispose();
        _mcpCancellation = null;
        base.OnFormClosed(e);
    }
}
