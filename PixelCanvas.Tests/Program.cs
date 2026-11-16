using System.Text.Json;
using PixelCanvas;

string folder = Path.Combine(AppContext.BaseDirectory, "pixelcanvas-project-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    string FileAt(string name) => Path.Combine(folder, name + ".pxproj");
    void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    var bridgeFlags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
    var listTools = typeof(McpBridge).GetMethod("ListTools", bridgeFlags)!;
    var callTool = typeof(McpBridge).GetMethod("CallToolAsync", bridgeFlags)!;
    var advertised = JsonSerializer.SerializeToNode(listTools.Invoke(null, null))!["tools"]!.AsArray();
    Check(advertised.Count == 8, "MCP 工具分组数量");
    object InvokeTool(string name, object arguments)
    {
        var request = JsonSerializer.SerializeToNode(new { name, arguments })!.AsObject();
        return ((Task<object>)callTool.Invoke(null, new object[] { request })!).GetAwaiter().GetResult();
    }
    var lookup = JsonSerializer.SerializeToNode(InvokeTool("pixelcanvas_tool_info", new { action = "project_info" }))!;
    Check(!lookup["isError"]!.GetValue<bool>() && lookup["content"]![0]!["text"]!.GetValue<string>().Contains("inputSchema"), "MCP 操作参数查询");
    var batchLookup = JsonSerializer.SerializeToNode(InvokeTool("pixelcanvas_tool_info", new { actions = new[] { "project_info", "canvas_info" } }))!;
    var definitions = System.Text.Json.Nodes.JsonNode.Parse(batchLookup["content"]![0]!["text"]!.GetValue<string>())!.AsArray();
    Check(!batchLookup["isError"]!.GetValue<bool>() && definitions.Count == 2 && definitions[0]!["name"]!.GetValue<string>() == "project_info" && definitions[1]!["name"]!.GetValue<string>() == "canvas_info", "MCP 批量参数查询按顺序返回");
    var invalidLookup = JsonSerializer.SerializeToNode(InvokeTool("pixelcanvas_tool_info", new { actions = new[] { "project_info", "unknown" } }))!;
    Check(invalidLookup["isError"]!.GetValue<bool>(), "MCP 批量参数查询拒绝未知操作");
    var ambiguousLookup = JsonSerializer.SerializeToNode(InvokeTool("pixelcanvas_tool_info", new { action = "project_info", actions = new[] { "canvas_info" } }))!;
    Check(ambiguousLookup["isError"]!.GetValue<bool>(), "MCP 参数查询拒绝混用单项与批量");
    var invalidGroup = JsonSerializer.SerializeToNode(InvokeTool("pixelcanvas_draw", new { action = "project_info", arguments = new { path = "invalid" } }))!;
    Check(invalidGroup["isError"]!.GetValue<bool>(), "MCP 拒绝跨组操作");
    var grouped = JsonSerializer.SerializeToNode(InvokeTool("pixelcanvas_project", new { action = "project_info", arguments = new { path = "invalid" } }))!;
    var legacyResult = JsonSerializer.SerializeToNode(InvokeTool("project_info", new { path = "invalid" }))!;
    Check(grouped.ToJsonString() == legacyResult.ToJsonString(), "MCP 分组与旧名称的路由一致");
    void Invalid(string file, string message)
    {
        var result = ProjectFile.TryLoad(file, out string error);
        Check(result is null && error.Contains(message, StringComparison.Ordinal), "拒绝：" + message);
    }
    string JsonWithPalette(int count) => JsonSerializer.Serialize(new
    {
        format = ProjectFile.FormatName, version = 2, width = 1, height = 1,
        palette = Enumerable.Repeat("ABCDEF", count).ToArray(),
        layers = new[] { new { name = "layer", visible = true, pixels = new[] { "1x00000000" } } }, activeLayer = 0
    });

    string normal = FileAt("normal");
    var grid = new PixelGrid(4, 4);
    grid.Set(1, 2, Argb.FromRgb(0x123456));
    ProjectFile.Save(normal, new[] { new Layer("测试", grid) }, 0, new[] { "ABCDEF" });
    var loaded = ProjectFile.TryLoad(normal, out string normalError);
    Check(loaded is not null && loaded.Grid.Get(1, 2) == Argb.FromRgb(0x123456), "正常工程保存和读取：" + normalError);

    string animation = FileAt("animation");
    var secondGrid = new PixelGrid(4, 4); secondGrid.Set(3, 1, Argb.FromRgb(0xFEDCBA));
    var frames = new List<AnimationFrame> { new(new List<Layer> { new("first", grid) }), new(new List<Layer> { new("second", secondGrid) }) };
    ProjectFile.Save(animation, frames[1].Layers, 0, new[] { "ABCDEF" }, frames: frames, activeFrame: 1, framesPerSecond: 12);
    var loadedAnimation = ProjectFile.TryLoad(animation, out string animationError);
    Check(loadedAnimation is not null && loadedAnimation.Frames.Count == 2 && loadedAnimation.ActiveFrame == 1 && loadedAnimation.FramesPerSecond == 12 &&
        loadedAnimation.Frames[0].Layers[0].Grid.Get(1, 2) == Argb.FromRgb(0x123456) && loadedAnimation.Grid.Get(3, 1) == Argb.FromRgb(0xFEDCBA), "多帧工程保存和读取：" + animationError);
    string? previousOutputRoot = Environment.GetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR");
    Environment.SetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR", folder);
    try
    {
        string ProjectAction(string action, object arguments)
        {
            var result = JsonSerializer.SerializeToNode(InvokeTool("pixelcanvas_project", new { action, arguments }))!;
            if (result["isError"]!.GetValue<bool>()) throw new Exception(result["content"]![0]!["text"]!.GetValue<string>());
            return System.Text.Json.Nodes.JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!["path"]!.GetValue<string>();
        }
        string pngOutput = ProjectAction("project_export_png", new { path = animation });
        string sheetOutput = ProjectAction("project_export_sprite_sheet", new { path = animation });
        string gifOutput = ProjectAction("project_export_gif", new { path = animation });
        string projectOutput = ProjectAction("project_add_layer", new { path = animation });
        Check(Path.GetDirectoryName(pngOutput) == Path.Combine(folder, "PNG") && Path.GetDirectoryName(sheetOutput) == Path.Combine(folder, "动画") &&
            Path.GetDirectoryName(gifOutput) == Path.Combine(folder, "动画") && Path.GetDirectoryName(projectOutput) == Path.Combine(folder, "工程"), "MCP 工程按用途分类输出");
        string explicitSheet = Path.Combine(folder, "explicit-sheet.png");
        Check(ProjectAction("project_export_sprite_sheet", new { path = animation, output_path = explicitSheet }) == explicitSheet && File.Exists(explicitSheet), "精灵表显式路径优先");
        string nestedPng = Path.Combine(folder, "输出目录", "多层", "图片.png");
        Check(ProjectAction("project_export_png", new { path = animation, output_path = nestedPng }) == nestedPng && File.Exists(nestedPng), "工程导出自动创建显式父目录");
        byte[] originalPng = File.ReadAllBytes(nestedPng);
        var duplicate = JsonSerializer.SerializeToNode(InvokeTool("pixelcanvas_project", new { action = "project_export_png", arguments = new { path = animation, output_path = nestedPng } }))!;
        Check(duplicate["isError"]!.GetValue<bool>() && File.ReadAllBytes(nestedPng).SequenceEqual(originalPng), "显式输出拒绝覆盖已有文件");
        string nestedProject = Path.Combine(folder, "显式工程", "子目录", "工程.pxproj");
        Check(ProjectAction("project_create", new { path = nestedProject, width = 4, height = 4 }) == nestedProject && ProjectFile.TryLoad(nestedProject, out _) is not null, "工程创建自动创建显式父目录");
        string blockedParent = Path.Combine(folder, "not-a-directory"); File.WriteAllText(blockedParent, "保留");
        var blocked = JsonSerializer.SerializeToNode(InvokeTool("pixelcanvas_project", new { action = "project_export_png", arguments = new { path = animation, output_path = Path.Combine(blockedParent, "image.png") } }))!;
        Check(blocked["isError"]!.GetValue<bool>() && File.ReadAllText(blockedParent) == "保留", "父路径被文件占用时返回错误并保留原文件");
        var presetType = typeof(MainForm).Assembly.GetType("PixelCanvas.ExportPreset")!;
        var presetMethod = typeof(MainForm).Assembly.GetType("PixelCanvas.ExportPresets")!.GetMethod("NewOutputPath", bridgeFlags)!;
        string PresetPath(string format, string directory) => (string)presetMethod.Invoke(null, new[] { Activator.CreateInstance(presetType, "test", format, 1, 8, 1, directory)! })!;
        Check(Path.GetDirectoryName(PresetPath("sheet", "")) == Path.Combine(folder, "动画") && Path.GetDirectoryName(PresetPath("png", "")) == Path.Combine(folder, "PNG") && Path.GetDirectoryName(PresetPath("sheet", folder)) == folder, "导出预设按用途分类并尊重自定义目录");
        using var sheetImage = new System.Drawing.Bitmap(sheetOutput);
        Check(sheetImage.Width == 8 && sheetImage.Height == 4 && File.ReadAllBytes(gifOutput).Take(6).SequenceEqual(System.Text.Encoding.ASCII.GetBytes("GIF89a")) && ProjectFile.TryLoad(projectOutput, out _)?.Frames.Count == 2, "分类输出文件有效");
    }
    finally { Environment.SetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR", previousOutputRoot); }
    string gifPath = Path.Combine(folder, "animation.gif");
    var gifType = typeof(MainForm).Assembly.GetType("PixelCanvas.GifExport")!;
    var gifSave = gifType.GetMethod("Save", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
    gifSave.Invoke(null, new object[] { gifPath, frames, 12, 1 });
    using (var gif = System.Drawing.Image.FromFile(gifPath))
    {
        var dimension = new System.Drawing.Imaging.FrameDimension(gif.FrameDimensionsList[0]);
        Check(gif.GetFrameCount(dimension) == 2, "GIF 包含全部帧");
        gif.SelectActiveFrame(dimension, 0);
        using (var frame = new System.Drawing.Bitmap(gif)) Check(frame.GetPixel(1, 2).ToArgb() == Argb.FromRgb(0x123456) && frame.GetPixel(0, 0).A == 0, "GIF 首帧颜色与透明度");
        gif.SelectActiveFrame(dimension, 1);
        using (var frame = new System.Drawing.Bitmap(gif)) Check(frame.GetPixel(3, 1).ToArgb() == Argb.FromRgb(0xFEDCBA) && frame.GetPixel(1, 2).A == 0, "GIF 下一帧颜色及透明背景清除");
        var delays = gif.GetPropertyItem(0x5100)!.Value!;
        Check(BitConverter.ToInt32(delays, 0) == 8 && BitConverter.ToInt32(delays, 4) == 9, "GIF 按帧率分配延时");
    }
    var manyGrid = new PixelGrid(16, 16);
    for (int i = 0; i < 256; i++) manyGrid.Set(i % 16, i / 16, Argb.FromRgb((i << 16) | ((255 - i) << 8) | i));
    string quantized = Path.Combine(folder, "quantized.gif");
    gifSave.Invoke(null, new object[] { quantized, new List<AnimationFrame> { new(new List<Layer> { new("many", manyGrid) }) }, 8, 1 });
    using (var gif = new System.Drawing.Bitmap(quantized)) Check(gif.Width == 16 && gif.Height == 16 && gif.GetPixel(0, 0).G > 240 && gif.GetPixel(15, 15).R > 240, "GIF 超过 255 色时转换调色板并正确解码整帧");
    var sheetType = typeof(MainForm).Assembly.GetType("PixelCanvas.SpriteSheetExport")!;
    var sheetSave = sheetType.GetMethod("Save", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
    var thirdGrid = new PixelGrid(4, 4); thirdGrid.Set(0, 0, Argb.FromRgb(0xAA2200));
    var hiddenGrid = new PixelGrid(4, 4); hiddenGrid.Set(0, 0, Argb.FromRgb(0xFFFFFF));
    var sheetFrames = new List<AnimationFrame>(frames) { new(new List<Layer> { new("visible", thirdGrid), new("hidden", hiddenGrid, false) }) };
    string sheetPath = Path.Combine(folder, "sheet.png");
    sheetSave.Invoke(null, new object[] { sheetPath, sheetFrames, 2, 2 });
    using (var sheet = new System.Drawing.Bitmap(sheetPath))
        Check(sheet.Width == 16 && sheet.Height == 16 && sheet.GetPixel(2, 4).ToArgb() == Argb.FromRgb(0x123456) &&
              sheet.GetPixel(14, 2).ToArgb() == Argb.FromRgb(0xFEDCBA) && sheet.GetPixel(0, 8).ToArgb() == Argb.FromRgb(0xAA2200) &&
              sheet.GetPixel(15, 15).A == 0, "精灵表帧顺序、缩放、隐藏图层和透明空格");
    string largeSheet = Path.Combine(folder, "large-sheet.png");
    var largeFrames = new List<AnimationFrame> { new(new List<Layer> { new("large", new PixelGrid(512, 512)) }) };
    try { sheetSave.Invoke(null, new object[] { largeSheet, largeFrames, 64, 16 }); throw new Exception("超大精灵表应被拒绝"); }
    catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is ArgumentException) { }
    Check(!File.Exists(largeSheet), "精灵表超限不创建文件");
    using (var image = new System.Drawing.Bitmap(4, 4))
    {
        image.SetPixel(0, 0, System.Drawing.Color.FromArgb(0, 200, 30, 20));
        image.SetPixel(1, 0, System.Drawing.Color.FromArgb(255, 18, 52, 86));
        image.SetPixel(2, 0, System.Drawing.Color.FromArgb(127, 18, 52, 86));
        image.SetPixel(3, 0, System.Drawing.Color.FromArgb(128, 18, 52, 86));
        var imported = AnimationFrame.FromImage(image, 4, 4);
        Check(imported.Layers[0].Grid.Get(0, 0) == Argb.Transparent && imported.Layers[0].Grid.Get(1, 0) == Argb.FromRgb(0x123456), "图片导入帧与透明像素");
        Check(imported.Layers[0].Grid.Get(2, 0) == Argb.Transparent && Argb.AlphaOf(imported.Layers[0].Grid.Get(3, 0)) == 255, "图片导入帧采用二值 alpha 阈值");
        try { AnimationFrame.FromImage(image, 8, 4); throw new Exception("应拒绝不同尺寸的图片"); }
        catch (ArgumentException) { Console.WriteLine("PASS 拒绝不同尺寸的导入图片"); }
    }
    string brokenAnimation = FileAt("bad-animation");
    File.WriteAllText(brokenAnimation, File.ReadAllText(animation).Replace("\"fps\":12", "\"fps\":0"));
    Invalid(brokenAnimation, "动画帧或帧率不合法");

    string legacy = FileAt("legacy");
    File.WriteAllText(legacy, JsonSerializer.Serialize(new { format = ProjectFile.FormatName, version = 1, width = 1, height = 1, palette = new[] { "ABCDEF" }, pixels = new[] { "1xABCDEFff" } }));
    Check(ProjectFile.TryLoad(legacy, out _)?.Grid.Get(0, 0) == Argb.FromRgb(0xABCDEF), "旧版工程可读取");

    string limit = FileAt("palette-limit");
    File.WriteAllText(limit, JsonWithPalette(ProjectFile.MaxPaletteColors));
    Check(ProjectFile.TryLoad(limit, out _)?.Palette.Count == ProjectFile.MaxPaletteColors, "调色板上限可读取");
    string excessivePalette = FileAt("palette-over");
    File.WriteAllText(excessivePalette, JsonWithPalette(ProjectFile.MaxPaletteColors + 1));
    Invalid(excessivePalette, "palette 颜色数量");

    string corrupt = FileAt("corrupt");
    File.WriteAllText(corrupt, "{not json");
    Invalid(corrupt, "不是有效的 JSON");
    string badVersion = FileAt("bad-version");
    File.WriteAllText(badVersion, JsonWithPalette(1).Replace("\"version\":2", "\"version\":99"));
    Invalid(badVersion, "工程版本不受支持");
    string badRows = FileAt("bad-rows");
    File.WriteAllText(badRows, JsonWithPalette(1).Replace("1x00000000", "2x00000000"));
    Invalid(badRows, "图层数据不合法");
    Invalid(FileAt("missing"), "无法读取文件");

    string oversized = FileAt("oversized");
    using (var file = new FileStream(oversized, FileMode.CreateNew, FileAccess.Write)) file.SetLength(ProjectFile.MaxFileBytes + 1);
    Invalid(oversized, "超过 128 MiB");

    string original = File.ReadAllText(normal);
    try
    {
        ProjectFile.Save(normal, new[] { new Layer("测试", grid) }, 0, Enumerable.Repeat("ABCDEF", ProjectFile.MaxPaletteColors + 1).ToArray());
        throw new Exception("保存超限调色板应失败");
    }
    catch (ArgumentException) { }
    Check(File.ReadAllText(normal) == original, "保存超限时原文件保持不变");

    string? sample = args.FirstOrDefault(x => x != "--ui");
    if (sample is not null)
        Check(ProjectFile.TryLoad(sample, out var error) is not null, "用户验证工程可读取：" + error);

    if (args.Contains("--ui"))
    {
        Exception? uiError = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var referenceControl = new CanvasControl(new PixelGrid(4, 4));
                var refInfo = typeof(CanvasControl).GetMethod("ReferenceInfo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                referenceControl.SetReferenceImage(new System.Drawing.Bitmap(4, 4), @"C:\test\reference.png");
                var referenceData = JsonSerializer.SerializeToNode(refInfo.Invoke(referenceControl, null))!;
                if (referenceData["sourcePath"]!.GetValue<string>() != @"C:\test\reference.png") throw new Exception("参考图路径未返回");
                referenceControl.SetReferenceImage(null);
                referenceData = JsonSerializer.SerializeToNode(refInfo.Invoke(referenceControl, null))!;
                if (referenceData["sourcePath"] is not null) throw new Exception("清除参考图后路径未清除");
                using var form = new MainForm(4, 4);
                form.CreateControl();
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                void Invoke(string name, params object[] values) => typeof(MainForm).GetMethod(name, flags)!.Invoke(form, values);
                T Field<T>(string name) => (T)typeof(MainForm).GetField(name, flags)!.GetValue(form)!;
                void Paint(string color)
                {
                    var request = System.Text.Json.Nodes.JsonNode.Parse("{\"action\":\"paint\",\"arguments\":{\"pixels\":[{\"x\":0,\"y\":0,\"color\":\"" + color + "\"}]}}")!;
                    Invoke("HandleMcpRequest", request);
                }
                Paint("#FF0000");
                Invoke("AddAnimationFrame", true);
                Paint("#00FF00");
                Invoke("SwitchFrame", 0);
                if (Field<PixelGrid>("_grid").Get(0, 0) != Argb.FromRgb(0xFF0000)) throw new Exception("切换帧后原帧像素丢失");
                Invoke("HandleMcpRequest", System.Text.Json.Nodes.JsonNode.Parse("{\"action\":\"undo\",\"arguments\":{}}")!);
                if (Field<PixelGrid>("_grid").Get(0, 0) != Argb.Transparent) throw new Exception("原帧撤销历史不正确");
                Invoke("SwitchFrame", 1);
                if (Field<PixelGrid>("_grid").Get(0, 0) != Argb.FromRgb(0x00FF00)) throw new Exception("另一帧像素被影响");
                Invoke("ToggleAnimation");
                if (!Field<bool>("_playing")) throw new Exception("未进入播放状态");
                Thread.Sleep(180);
                System.Windows.Forms.Application.DoEvents();
                if (Field<int>("_activeFrame") != 0) throw new Exception("播放定时器未切换帧");
                Invoke("ToggleAnimation");
                if (Field<bool>("_playing")) throw new Exception("未退出播放状态");

                using var batchForm = new MainForm(4, 4);
                batchForm.CreateControl();
                object Request(string json) => typeof(MainForm).GetMethod("HandleMcpRequest", flags)!.Invoke(batchForm, new object[] { System.Text.Json.Nodes.JsonNode.Parse(json)! })!;
                var batchGrid = (PixelGrid)typeof(MainForm).GetField("_grid", flags)!.GetValue(batchForm)!;
                Request("{\"action\":\"batch_paint\",\"arguments\":{\"operations\":[{\"type\":\"paint\",\"pixels\":[{\"x\":0,\"y\":0,\"color\":\"#FF0000\"}]},{\"type\":\"shape\",\"shape\":\"line\",\"x1\":1,\"y1\":0,\"x2\":2,\"y2\":0,\"color\":\"#00FF00\"}]}}");
                if (batchGrid.Get(0, 0) != Argb.FromRgb(0xFF0000) || batchGrid.Get(2, 0) != Argb.FromRgb(0x00FF00)) throw new Exception("批量绘画结果错误");
                try { Request("{\"action\":\"batch_paint\",\"arguments\":{\"operations\":[{\"type\":\"fill\",\"x\":0,\"y\":0,\"color\":\"#0000FF\"},{\"type\":\"paint\",\"pixels\":[{\"x\":99,\"y\":0,\"color\":\"#000000\"}]}]}}"); throw new Exception("无效批量操作应失败"); }
                catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is ArgumentException) { }
                if (batchGrid.Get(0, 0) != Argb.FromRgb(0xFF0000)) throw new Exception("批量失败后画布被修改");
                Request("{\"action\":\"undo\",\"arguments\":{}}");
                if (batchGrid.Get(0, 0) != Argb.Transparent || batchGrid.Get(2, 0) != Argb.Transparent) throw new Exception("批量撤销不正确");
                string saved = FileAt("batch-saved"), png = Path.Combine(folder, "batch.png");
                string saveRequest = JsonSerializer.Serialize(new { action = "save_export", arguments = new { project_path = saved, png_path = png } });
                string saveResult = JsonSerializer.Serialize(Request(saveRequest));
                if (!File.Exists(saved) || !File.Exists(png) || !saveResult.Contains("\"pngExported\":true")) throw new Exception("一键保存导出失败：" + saveResult);
                string badPng = Path.Combine(folder, "blocked-target.png");
                Directory.CreateDirectory(badPng);
                string partial = JsonSerializer.Serialize(Request(JsonSerializer.Serialize(new { action = "save_export", arguments = new { png_path = badPng } })));
                if (!partial.Contains("\"projectSaved\":true") || !partial.Contains("\"pngExported\":false")) throw new Exception("部分成功状态未报告：" + partial);

                using var artworkForm = new MainForm(4, 4);
                artworkForm.CreateControl();
                object Artwork(object arguments) => typeof(MainForm).GetMethod("HandleMcpRequest", flags)!.Invoke(artworkForm, new object[] { JsonSerializer.SerializeToNode(new { action = "create_artwork", arguments })! })!;
                string artPath = FileAt("artwork");
                string reuseState = JsonSerializer.Serialize(typeof(MainForm).GetMethod("HandleMcpRequest", flags)!.Invoke(artworkForm, new object[] { JsonSerializer.SerializeToNode(new { action = "reuse_info", arguments = new { } })! }));
                if (!reuseState.Contains("\"reusable\":true")) throw new Exception("空白画布应允许复用");
                var artArgs = new { width = 4, height = 4, layers = new[] { "背景", "主体" }, operations = new object[] { new { type = "fill", layer = 0, x = 0, y = 0, color = "#FF0000" }, new { type = "paint", layer = 1, pixels = new[] { new { x = 1, y = 1, color = "#00FF00" } } } }, project_path = artPath };
                string artResult = JsonSerializer.Serialize(Artwork(artArgs));
                var artProject = ProjectFile.TryLoad(artPath, out _);
                if (!artResult.Contains("\"completedStage\":\"saved\"") || artProject is null || artProject.Layers.Count != 2 || artProject.Layers[0].Name != "背景" || artProject.Layers[1].Grid.Get(1, 1) != Argb.FromRgb(0x00FF00)) throw new Exception("组合工具结果错误：" + artResult);
                using var failedForm = new MainForm(4, 4);
                failedForm.CreateControl();
                reuseState = JsonSerializer.Serialize(typeof(MainForm).GetMethod("HandleMcpRequest", flags)!.Invoke(artworkForm, new object[] { JsonSerializer.SerializeToNode(new { action = "reuse_info", arguments = new { } })! }));
                if (!reuseState.Contains("\"reusable\":false")) throw new Exception("已有工程不可复用");
                var failedArgs = new { width = 4, height = 4, layers = new[] { "底层", "顶层" }, operations = new object[] { new { type = "paint", layer = 1, pixels = new[] { new { x = 99, y = 0, color = "#FF0000" } } } }, project_path = FileAt("should-not-exist") };
                string failed = JsonSerializer.Serialize(typeof(MainForm).GetMethod("HandleMcpRequest", flags)!.Invoke(failedForm, new object[] { JsonSerializer.SerializeToNode(new { action = "create_artwork", arguments = failedArgs })! }));
                if (!failed.Contains("\"completedStage\":\"layers_created\"") || File.Exists(failedArgs.project_path)) throw new Exception("组合工具失败阶段不正确：" + failed);

                using var animationForm = new MainForm(4, 4);
                animationForm.CreateControl();
                object AnimRequest(string json) => typeof(MainForm).GetMethod("HandleMcpRequest", flags)!.Invoke(animationForm, new object[] { System.Text.Json.Nodes.JsonNode.Parse(json)! })!;
                AnimRequest("{\"action\":\"paint\",\"arguments\":{\"pixels\":[{\"x\":0,\"y\":0,\"color\":\"#FF0000\"}]}}");
                var preview = JsonSerializer.SerializeToNode(AnimRequest("{\"action\":\"preview\",\"arguments\":{}}"))!;
                using (var buffer = new MemoryStream(Convert.FromBase64String(preview["data"]!["imageBase64"]!.GetValue<string>())))
                using (var image = new System.Drawing.Bitmap(buffer))
                    if (image.GetPixel(0, 0).ToArgb() != Argb.FromRgb(0xFF0000) || image.GetPixel(3, 3).A != 0) throw new Exception("画布预览颜色或透明度不正确");
                string framesJson = "{\"action\":\"batch_frames\",\"arguments\":{\"fps\":12,\"frames\":[{\"copy\":true,\"operations\":[{\"type\":\"paint\",\"pixels\":[{\"x\":1,\"y\":0,\"color\":\"#00FF00\"}]}]},{\"copy\":true,\"operations\":[{\"type\":\"paint\",\"pixels\":[{\"x\":2,\"y\":0,\"color\":\"#0000FF\"}]}]}]}}";
                AnimRequest(framesJson);
                var animFrames = (List<AnimationFrame>)typeof(MainForm).GetField("_frames", flags)!.GetValue(animationForm)!;
                if (animFrames.Count != 3 || animFrames[1].Layers[0].Grid.Get(0, 0) != Argb.FromRgb(0xFF0000) || animFrames[2].Layers[0].Grid.Get(1, 0) != Argb.FromRgb(0x00FF00) || animFrames[2].Layers[0].Grid.Get(2, 0) != Argb.FromRgb(0x0000FF)) throw new Exception("批量帧复制或绘制错误");
                try { AnimRequest("{\"action\":\"batch_frames\",\"arguments\":{\"fps\":20,\"frames\":[{\"copy\":true},{\"operations\":[{\"type\":\"paint\",\"pixels\":[{\"x\":99,\"y\":0,\"color\":\"#FF0000\"}]}]}]}}"); throw new Exception("无效帧应失败"); }
                catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is ArgumentException) { }
                if (animFrames.Count != 3 || (int)typeof(MainForm).GetField("_fps", flags)!.GetValue(animationForm)! != 12) throw new Exception("批量帧失败后状态被修改");
                var animCanvas = (CanvasControl)typeof(MainForm).GetField("_canvas", flags)!.GetValue(animationForm)!;
                var onion = (System.Windows.Forms.CheckBox)typeof(MainForm).GetField("_onionCheck", flags)!.GetValue(animationForm)!;
                var onionResult = JsonSerializer.SerializeToNode(AnimRequest("{\"action\":\"onion_skin\",\"arguments\":{\"enabled\":true}}"))!;
                if (!onion.Checked || onionResult["data"]!["onionEnabled"]!.GetValue<bool>() != true || onionResult["data"]!["onionVisible"]!.GetValue<bool>() != true) throw new Exception("插件洋葱皮状态错误");
                var previousOnion = typeof(CanvasControl).GetField("_onionPrevious", flags)!.GetValue(animCanvas);
                if (previousOnion is null || animCanvas.NativeBitmap.GetPixel(3, 3).A != 0) throw new Exception("洋葱皮未显示或污染画布位图");
                var chosenFrame = animFrames[2];
                AnimRequest("{\"action\":\"move_frame\",\"arguments\":{\"frame\":2,\"target_index\":0}}");
                if (!ReferenceEquals(animFrames[0], chosenFrame) || (int)typeof(MainForm).GetField("_activeFrame", flags)!.GetValue(animationForm)! != 0) throw new Exception("帧排序或当前帧错误");
                string reorderedPath = FileAt("reordered");
                typeof(MainForm).GetMethod("HandleMcpRequest", flags)!.Invoke(animationForm, new object[] { JsonSerializer.SerializeToNode(new { action = "save_project", arguments = new { path = reorderedPath } })! });
                var reordered = ProjectFile.TryLoad(reorderedPath, out _);
                if (reordered is null || reordered.Frames[0].Layers[0].Grid.Get(2, 0) != Argb.FromRgb(0x0000FF)) throw new Exception("帧顺序未保存");
                typeof(MainForm).GetMethod("ToggleAnimation", flags)!.Invoke(animationForm, null);
                if (typeof(CanvasControl).GetField("_onionNext", flags)!.GetValue(animCanvas) is not null) throw new Exception("播放时洋葱皮未隐藏");
                var playingInfo = JsonSerializer.SerializeToNode(AnimRequest("{\"action\":\"info\",\"arguments\":{}}"))!;
                if (playingInfo["data"]!["onionEnabled"]!.GetValue<bool>() != true || playingInfo["data"]!["onionVisible"]!.GetValue<bool>()) throw new Exception("播放时洋葱皮状态错误");
                try { AnimRequest("{\"action\":\"move_frame\",\"arguments\":{\"frame\":0,\"target_index\":1}}"); throw new Exception("播放中不应排序"); }
                catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
                typeof(MainForm).GetMethod("ToggleAnimation", flags)!.Invoke(animationForm, null);
                var timeline = (System.Windows.Forms.Control)typeof(MainForm).GetField("_timeline", flags)!.GetValue(animationForm)!;
                timeline.CreateControl();
                using (var timelineImage = new System.Drawing.Bitmap(timeline.Width, timeline.Height)) timeline.DrawToBitmap(timelineImage, timeline.ClientRectangle);
                var timelineFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                timeline.GetType().GetMethod("OnMouseDown", timelineFlags)!.Invoke(timeline, new object[] { new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, 10, 10, 0) });
                timeline.GetType().GetMethod("OnMouseUp", timelineFlags)!.Invoke(timeline, new object[] { new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, 170, 10, 0) });
                if (!ReferenceEquals(animFrames[2], chosenFrame)) throw new Exception("时间轴拖放排序失败");

                using var paletteForm = new MainForm(4, 4);
                paletteForm.CreateControl();
                object PaletteRequest(object req) => typeof(MainForm).GetMethod("HandleMcpRequest", flags)!.Invoke(paletteForm, new object[] { JsonSerializer.SerializeToNode(req)! })!;
                PaletteRequest(new { action = "paint", arguments = new { pixels = new[] { new { x = 0, y = 0, color = "#FF0000" }, new { x = 1, y = 0, color = "#FF0000" } } } });
                string replaced = JsonSerializer.Serialize(PaletteRequest(new { action = "replace_color", arguments = new { from_color = "#FF0000", to_color = "#00FF00" } }));
                var paletteGrid = (PixelGrid)typeof(MainForm).GetField("_grid", flags)!.GetValue(paletteForm)!;
                if (!replaced.Contains("\"replaced\":2") || paletteGrid.Get(1, 0) != Argb.FromRgb(0x00FF00)) throw new Exception("颜色替换错误");
                PaletteRequest(new { action = "undo", arguments = new { } });
                if (paletteGrid.Get(0, 0) != Argb.FromRgb(0xFF0000) || paletteGrid.Get(1, 0) != Argb.FromRgb(0xFF0000)) throw new Exception("颜色替换撤销错误");
                PaletteRequest(new { action = "palette_set", arguments = new { colors = new[] { "#123456", "ABCDEF" } } });
                string palettePath = Path.Combine(folder, "palette.pxpal");
                PaletteRequest(new { action = "palette_save", arguments = new { path = palettePath } });
                PaletteRequest(new { action = "palette_set", arguments = new { colors = new[] { "#000000" } } });
                PaletteRequest(new { action = "palette_load", arguments = new { path = palettePath } });
                string paletteResult = JsonSerializer.Serialize(PaletteRequest(new { action = "palette_get", arguments = new { } }));
                if (!paletteResult.Contains("#123456") || !paletteResult.Contains("#ABCDEF")) throw new Exception("调色板保存和加载错误");
                string invalidPalette = Path.Combine(folder, "broken.pxpal"); File.WriteAllText(invalidPalette, "{\"format\":\"pixelcanvas-palette\",\"version\":1,\"colors\":[\"bad\"]}");
                try { PaletteRequest(new { action = "palette_load", arguments = new { path = invalidPalette } }); throw new Exception("损坏调色板应被拒绝"); }
                catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is ArgumentException) { }
                if (!JsonSerializer.Serialize(PaletteRequest(new { action = "palette_get", arguments = new { } })).Contains("#123456")) throw new Exception("加载失败改变了调色板");
                string? oldPresetDir = Environment.GetEnvironmentVariable("PIXELCANVAS_PRESET_DIR"), oldOutputDir = Environment.GetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR");
                try
                {
                    Environment.SetEnvironmentVariable("PIXELCANVAS_PRESET_DIR", folder); Environment.SetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR", folder);
                    PaletteRequest(new { action = "presets_put", arguments = new { name = "test", format = "gif", scale = 2, fps = 12, columns = 2, directory = "" } });
                    try { PaletteRequest(new { action = "presets_put", arguments = new { name = "test", format = "png" } }); throw new Exception("同名预设应拒绝覆盖"); }
                    catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is IOException) { }
                    var exported = JsonSerializer.SerializeToNode(PaletteRequest(new { action = "export_preset", arguments = new { name = "test" } }))!;
                    string exportedPath = exported["data"]!["path"]!.GetValue<string>();
                    if (!File.Exists(exportedPath) || Path.GetExtension(exportedPath) != ".gif" || exported["data"]!["fps"]!.GetValue<int>() != 12 || (int)typeof(MainForm).GetField("_fps", flags)!.GetValue(paletteForm)! != 8) throw new Exception("预设导出或原工程帧率错误");
                    string listed = JsonSerializer.Serialize(PaletteRequest(new { action = "presets_list", arguments = new { } }));
                    if (!listed.Contains("test")) throw new Exception("预设未持久化");
                    try { PaletteRequest(new { action = "presets_delete", arguments = new { name = "test", confirm = false } }); throw new Exception("未确认删除应失败"); }
                    catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is ArgumentException) { }
                    PaletteRequest(new { action = "presets_delete", arguments = new { name = "test", confirm = true } });
                    if (JsonSerializer.Serialize(PaletteRequest(new { action = "presets_list", arguments = new { } })).Contains("test")) throw new Exception("预设未删除");
                }
                finally { Environment.SetEnvironmentVariable("PIXELCANVAS_PRESET_DIR", oldPresetDir); Environment.SetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR", oldOutputDir); }

                using var mirrorForm = new MainForm(5, 5); mirrorForm.CreateControl();
                System.Text.Json.Nodes.JsonNode MirrorRequest(object request) => JsonSerializer.SerializeToNode(typeof(MainForm).GetMethod("HandleMcpRequest", flags)!.Invoke(mirrorForm, new object[] { JsonSerializer.SerializeToNode(request)! }))!;
                MirrorRequest(new { action = "paint", arguments = new { mirror_h = true, mirror_v = true, pixels = new[] { new { x = 0, y = 1, color = "#FF0000" } } } });
                var mirrorGrid = (PixelGrid)typeof(MainForm).GetField("_grid", flags)!.GetValue(mirrorForm)!;
                if (mirrorGrid.Get(0, 1) != Argb.FromRgb(0xFF0000) || mirrorGrid.Get(4, 1) != Argb.FromRgb(0xFF0000) || mirrorGrid.Get(0, 3) != Argb.FromRgb(0xFF0000) || mirrorGrid.Get(4, 3) != Argb.FromRgb(0xFF0000)) throw new Exception("双轴镜像像素错误");
                MirrorRequest(new { action = "undo", arguments = new { } });
                if (mirrorGrid.Get(4, 3) != Argb.Transparent) throw new Exception("镜像绘制撤销失败");
                MirrorRequest(new { action = "shape", arguments = new { shape = "line", x1 = 0, y1 = 0, x2 = 1, y2 = 0, color = "#FF0000", mirror_h = true } });
                if (mirrorGrid.Get(4, 0) != Argb.FromRgb(0xFF0000)) throw new Exception("形状镜像失败");
                MirrorRequest(new { action = "add_layer", arguments = new { } });
                MirrorRequest(new { action = "paint", arguments = new { pixels = new[] { new { x = 0, y = 0, color = "#00FF00" } } } });
                var composite = MirrorRequest(new { action = "read_composite", arguments = new { x = 0, y = 0 } });
                if (composite["data"]!["color"]!.GetValue<string>() != "#00FF00") throw new Exception("合成取色未考虑图层顺序");
                MirrorRequest(new { action = "layer_visibility", arguments = new { layer = 1, visible = false } });
                composite = MirrorRequest(new { action = "read_composite", arguments = new { x = 0, y = 0 } });
                if (composite["data"]!["color"]!.GetValue<string>() != "#FF0000") throw new Exception("合成取色未忽略隐藏图层");
                string reference = Path.Combine(folder, "reference.png"); using (var image = new System.Drawing.Bitmap(3, 2)) image.Save(reference);
                MirrorRequest(new { action = "reference_load", arguments = new { path = reference } });
                var referenceState = MirrorRequest(new { action = "reference_settings", arguments = new { visible = false, opacity = 70 } });
                if (referenceState["data"]!["loaded"]!.GetValue<bool>() != true || referenceState["data"]!["visible"]!.GetValue<bool>() || Math.Abs(referenceState["data"]!["opacity"]!.GetValue<float>() - 0.7f) > 0.001f) throw new Exception("参考图设置错误");
                try { MirrorRequest(new { action = "reference_load", arguments = new { path = Path.Combine(folder, "missing.png") } }); throw new Exception("不存在的参考图应失败"); }
                catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is IOException) { }
                referenceState = MirrorRequest(new { action = "reference_info", arguments = new { } });
                if (!referenceState["data"]!["loaded"]!.GetValue<bool>()) throw new Exception("加载失败导致原参考图丢失");
                MirrorRequest(new { action = "reference_clear", arguments = new { } });
                using var fillForm = new MainForm(5, 5); fillForm.CreateControl();
                void FillRequest(object request) => typeof(MainForm).GetMethod("HandleMcpRequest", flags)!.Invoke(fillForm, new object[] { JsonSerializer.SerializeToNode(request)! });
                FillRequest(new { action = "shape", arguments = new { shape = "line", x1 = 2, y1 = 0, x2 = 2, y2 = 4, color = "#000000" } });
                FillRequest(new { action = "fill", arguments = new { x = 0, y = 0, color = "#FF0000", mirror_h = true } });
                var fillGrid = (PixelGrid)typeof(MainForm).GetField("_grid", flags)!.GetValue(fillForm)!;
                if (fillGrid.Get(4, 0) != Argb.FromRgb(0xFF0000)) throw new Exception("填充镜像失败");
                FillRequest(new { action = "undo", arguments = new { } });
                FillRequest(new { action = "gradient", arguments = new { x = 0, y = 0, end_x = 1, end_y = 0, start_color = "#FF0000", end_color = "#0000FF", mirror_h = true } });
                if (fillGrid.Get(4, 0) != Argb.FromRgb(0xFF0000) || fillGrid.Get(3, 0) != Argb.FromRgb(0x0000FF)) throw new Exception("渐变镜像方向错误");

                using var controlForm = new MainForm(5, 5); controlForm.CreateControl();
                System.Text.Json.Nodes.JsonNode ControlRequest(object request) => JsonSerializer.SerializeToNode(typeof(MainForm).GetMethod("HandleMcpRequest", flags)!.Invoke(controlForm, new object[] { JsonSerializer.SerializeToNode(request)! }))!;
                string? priorRoot = Environment.GetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR");
                Environment.SetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR", folder);
                try
                {
                    string PathFor(object request) => ControlRequest(request)["data"]!["path"]!.GetValue<string>();
                    string sheet = PathFor(new { action = "export_sprite_sheet", arguments = new { } });
                    string canvasPng = PathFor(new { action = "export", arguments = new { } });
                    string gif = PathFor(new { action = "export_gif", arguments = new { } });
                    ControlRequest(new { action = "add_frame", arguments = new { } });
                    string project = PathFor(new { action = "save_copy", arguments = new { } });
                    if (Path.GetDirectoryName(sheet) != Path.Combine(folder, "动画") || Path.GetDirectoryName(canvasPng) != Path.Combine(folder, "PNG") || Path.GetDirectoryName(gif) != Path.Combine(folder, "动画") || Path.GetDirectoryName(project) != Path.Combine(folder, "工程")) throw new Exception("画布输出用途分类错误");
                    string explicitCanvasPng = Path.Combine(folder, "画布显式", "嵌套", "图片.png");
                    if (PathFor(new { action = "export", arguments = new { path = explicitCanvasPng } }) != explicitCanvasPng || !File.Exists(explicitCanvasPng)) throw new Exception("画布导出未创建父目录");
                    string explicitCanvasProject = Path.Combine(folder, "画布工程", "嵌套", "新建.pxproj");
                    if (PathFor(new { action = "save_project", arguments = new { path = explicitCanvasProject } }) != explicitCanvasProject || !File.Exists(explicitCanvasProject)) throw new Exception("画布保存未创建父目录");
                }
                finally { Environment.SetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR", priorRoot); }
                ControlRequest(new { action = "paint", arguments = new { pixels = new[] { new { x = 0, y = 0, color = "#123456" } } } });
                ControlRequest(new { action = "selection_set", arguments = new { x = 0, y = 0, width = 1, height = 1 } });
                ControlRequest(new { action = "selection_copy", arguments = new { } });
                var pasted = ControlRequest(new { action = "selection_paste", arguments = new { } });
                var controlGrid = (PixelGrid)typeof(MainForm).GetField("_grid", flags)!.GetValue(controlForm)!;
                if (!pasted["data"]!["changed"]!.GetValue<bool>() || controlGrid.Get(1, 1) != Argb.FromRgb(0x123456)) throw new Exception("插件选区复制粘贴错误");
                try { ControlRequest(new { action = "clear_layer", arguments = new { confirm = false } }); throw new Exception("清空需确认"); }
                catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is ArgumentException) { }
                ControlRequest(new { action = "clear_layer", arguments = new { confirm = true } });
                ControlRequest(new { action = "undo", arguments = new { } });
                if (controlGrid.Get(1, 1) != Argb.FromRgb(0x123456)) throw new Exception("清空图层撤销错误");
                ControlRequest(new { action = "add_layer", arguments = new { } });
                var selected = ControlRequest(new { action = "select_layer", arguments = new { layer = 0 } });
                if (selected["data"]!["activeLayer"]!.GetValue<int>() != 0) throw new Exception("图层选择错误");
                var options = ControlRequest(new { action = "draw_options_set", arguments = new { tool = "line", color = "#FF0000", brush_size = 3, mirror_h = true } });
                if (options["data"]!["tool"]!.GetValue<string>() != "line" || !options["data"]!["mirrorH"]!.GetValue<bool>()) throw new Exception("绘画设置错误");
                var view = ControlRequest(new { action = "view_set", arguments = new { zoom = 3, grid = false, placement = "center" } });
                if (view["data"]!["zoom"]!.GetValue<int>() != 3 || view["data"]!["grid"]!.GetValue<bool>()) throw new Exception("画布视图设置错误");
                try { ControlRequest(new { action = "resize", arguments = new { width = 8, height = 8, confirm = false } }); throw new Exception("调整尺寸需确认"); }
                catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is ArgumentException) { }
                if (controlGrid.Get(0, 0) != Argb.FromRgb(0x123456)) throw new Exception("取消尺寸调整破坏画布");
                var resized = ControlRequest(new { action = "resize", arguments = new { width = 8, height = 8, confirm = true } });
                if (resized["data"]!["canvas"]!["width"]!.GetValue<int>() != 8 || resized["data"]!["canvas"]!["frameCount"]!.GetValue<int>() != 1) throw new Exception("调整尺寸结果错误");
            }
            catch (Exception ex) { uiError = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        Check(uiError is null, "帧切换、独立历史和播放状态：" + uiError);
    }
}
finally { Directory.Delete(folder, recursive: true); }
