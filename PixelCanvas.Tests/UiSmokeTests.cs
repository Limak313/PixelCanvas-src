using System.Drawing;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using PixelCanvas;
using Xunit;

namespace PixelCanvas.Tests;

/// <summary>
/// 交互式 WinForms 冒烟测试：在 STA 线程内创建窗体与控件，通过反射覆盖帧切换、独立撤销历史、
/// 播放定时器、批量绘画、组合工具、批量帧、洋葱皮、时间轴拖放、调色板与预设、双轴镜像、
/// 图层合成、参考图、填充/渐变镜像、选区、图层、视图与尺寸调整等界面路径。
/// 这些用例依赖交互式桌面会话，默认跳过；本地启用时移除对应 fact 的 Skip。
/// </summary>
public class UiSmokeTests
{
    private const BindingFlags Flags = UiTestSupport.InstanceNonPublic;

    private static JsonNode J(object value) => JsonSerializer.SerializeToNode(value)!;

    private static JsonNode Data(JsonNode node) => node["data"]!;

    [Fact(Skip = "需要交互式桌面 STA 会话，不在无头或 CI 环境运行")]
    public void ReferenceControl_LoadsAndClearsSourcePath()
    {
        UiTestSupport.RunInSta(() =>
        {
            using var control = new CanvasControl(new PixelGrid(4, 4));
            control.SetReferenceImage(new Bitmap(4, 4), @"C:\test\reference.png");
            var info = J(control.ReferenceInfo());
            Assert.Equal(@"C:\test\reference.png", info["sourcePath"]!.GetValue<string>());

            control.SetReferenceImage(null);
            info = J(control.ReferenceInfo());
            Assert.Null(info["sourcePath"]);
        });
    }

    [Fact(Skip = "需要交互式桌面 STA 会话，不在无头或 CI 环境运行")]
    public void FrameHistory_SwitchFrameUndoAndPlayback()
    {
        UiTestSupport.RunInSta(() =>
        {
            using var form = new MainForm(4, 4);
            form.CreateControl();

            UiTestSupport.Handle(form, new { action = "paint", arguments = new { pixels = new[] { new { x = 0, y = 0, color = "#FF0000" } } } });
            UiTestSupport.Invoke(form, "AddAnimationFrame", true);
            UiTestSupport.Handle(form, new { action = "paint", arguments = new { pixels = new[] { new { x = 0, y = 0, color = "#00FF00" } } } });

            UiTestSupport.Invoke(form, "SwitchFrame", 0);
            // 注意：SwitchFrame 会替换 _grid 指向的 Grid 对象，断言前必须重新取字段，不能缓存跨帧引用。
            Assert.Equal(Argb.FromRgb(0xFF0000), UiTestSupport.Field<PixelGrid>(form, "_grid").Get(0, 0));

            UiTestSupport.Handle(form, new { action = "undo", arguments = new { } });
            Assert.Equal(Argb.Transparent, UiTestSupport.Field<PixelGrid>(form, "_grid").Get(0, 0));

            UiTestSupport.Invoke(form, "SwitchFrame", 1);
            Assert.Equal(Argb.FromRgb(0x00FF00), UiTestSupport.Field<PixelGrid>(form, "_grid").Get(0, 0));

            UiTestSupport.Invoke(form, "ToggleAnimation");
            Assert.True(UiTestSupport.Field<bool>(form, "_playing"));
            Thread.Sleep(180);
            Application.DoEvents();
            Assert.Equal(0, UiTestSupport.Field<int>(form, "_activeFrame"));

            UiTestSupport.Invoke(form, "ToggleAnimation");
            Assert.False(UiTestSupport.Field<bool>(form, "_playing"));
        });
    }

    [Fact(Skip = "需要交互式桌面 STA 会话，不在无头或 CI 环境运行")]
    public void BatchPaint_RollsBackOnInvalid_AndSaveExportReportsPartial()
    {
        using var dir = new TempDir();
        UiTestSupport.RunInSta(() =>
        {
            using var form = new MainForm(4, 4);
            form.CreateControl();
            var grid = UiTestSupport.Field<PixelGrid>(form, "_grid");

            UiTestSupport.Handle(form, new
            {
                action = "batch_paint",
                arguments = new
                {
                    operations = new object[]
                    {
                        new { type = "paint", pixels = new[] { new { x = 0, y = 0, color = "#FF0000" } } },
                        new { type = "shape", shape = "line", x1 = 1, y1 = 0, x2 = 2, y2 = 0, color = "#00FF00" }
                    }
                }
            });
            Assert.Equal(Argb.FromRgb(0xFF0000), grid.Get(0, 0));
            Assert.Equal(Argb.FromRgb(0x00FF00), grid.Get(2, 0));

            var invalid = new
            {
                action = "batch_paint",
                arguments = new
                {
                    operations = new object[]
                    {
                        new { type = "fill", x = 0, y = 0, color = "#0000FF" },
                        new { type = "paint", pixels = new[] { new { x = 99, y = 0, color = "#000000" } } }
                    }
                }
            };
            var ex = Assert.Throws<TargetInvocationException>(() => UiTestSupport.Handle(form, invalid));
            Assert.IsType<ArgumentException>(UiTestSupport.Inner<ArgumentException>(ex));
            Assert.Equal(Argb.FromRgb(0xFF0000), grid.Get(0, 0));

            UiTestSupport.Handle(form, new { action = "undo", arguments = new { } });
            Assert.Equal(Argb.Transparent, grid.Get(0, 0));
            Assert.Equal(Argb.Transparent, grid.Get(2, 0));

            string saved = dir.File("batch-saved.pxproj"), png = Path.Combine(dir.Path, "batch.png");
            var save = J(UiTestSupport.Handle(form, new { action = "save_export", arguments = new { project_path = saved, png_path = png } }));
            Assert.True(File.Exists(saved));
            Assert.True(File.Exists(png));
            Assert.True(Data(save)["pngExported"]!.GetValue<bool>());

            string badPng = Path.Combine(dir.Path, "blocked-target.png");
            Directory.CreateDirectory(badPng);
            var partial = J(UiTestSupport.Handle(form, new { action = "save_export", arguments = new { png_path = badPng } }));
            Assert.True(Data(partial)["projectSaved"]!.GetValue<bool>());
            Assert.False(Data(partial)["pngExported"]!.GetValue<bool>());
        });
    }

    [Fact(Skip = "需要交互式桌面 STA 会话，不在无头或 CI 环境运行")]
    public void CreateArtwork_SavesLayers_ReuseAndFailureStage()
    {
        using var dir = new TempDir();
        UiTestSupport.RunInSta(() =>
        {
            using var form = new MainForm(4, 4);
            form.CreateControl();

            var reuse = J(UiTestSupport.Handle(form, new { action = "reuse_info", arguments = new { } }));
            Assert.True(Data(reuse)["reusable"]!.GetValue<bool>());

            string artPath = dir.File("artwork.pxproj");
            var artArgs = new
            {
                width = 4,
                height = 4,
                layers = new[] { "背景", "主体" },
                operations = new object[]
                {
                    new { type = "fill", layer = 0, x = 0, y = 0, color = "#FF0000" },
                    new { type = "paint", layer = 1, pixels = new[] { new { x = 1, y = 1, color = "#00FF00" } } }
                },
                project_path = artPath
            };
            var art = J(UiTestSupport.Handle(form, new { action = "create_artwork", arguments = artArgs }));
            var project = ProjectFile.TryLoad(artPath, out _);
            Assert.Equal("saved", Data(art)["completedStage"]!.GetValue<string>());
            Assert.NotNull(project);
            Assert.Equal(2, project!.Layers.Count);
            Assert.Equal("背景", project.Layers[0].Name);
            Assert.Equal(Argb.FromRgb(0x00FF00), project.Layers[1].Grid.Get(1, 1));

            reuse = J(UiTestSupport.Handle(form, new { action = "reuse_info", arguments = new { } }));
            Assert.False(Data(reuse)["reusable"]!.GetValue<bool>());

            using var failedForm = new MainForm(4, 4);
            failedForm.CreateControl();
            var failedArgs = new
            {
                width = 4,
                height = 4,
                layers = new[] { "底层", "顶层" },
                operations = new object[] { new { type = "paint", layer = 1, pixels = new[] { new { x = 99, y = 0, color = "#FF0000" } } } },
                project_path = dir.File("should-not-exist.pxproj")
            };
            var failed = J(UiTestSupport.Handle(failedForm, new { action = "create_artwork", arguments = failedArgs }));
            Assert.Equal("layers_created", Data(failed)["completedStage"]!.GetValue<string>());
            Assert.False(File.Exists(failedArgs.project_path));
        });
    }

    [Fact(Skip = "需要交互式桌面 STA 会话，不在无头或 CI 环境运行")]
    public void AnimationFrames_OnionSkin_ReorderAndTimelineDrag()
    {
        using var dir = new TempDir();
        UiTestSupport.RunInSta(() =>
        {
            using var form = new MainForm(4, 4);
            form.CreateControl();

            UiTestSupport.Handle(form, new { action = "paint", arguments = new { pixels = new[] { new { x = 0, y = 0, color = "#FF0000" } } } });
            var preview = Data(J(UiTestSupport.Handle(form, new { action = "preview", arguments = new { } })));
            using (var buffer = new MemoryStream(Convert.FromBase64String(preview["imageBase64"]!.GetValue<string>())))
            using (var image = new Bitmap(buffer))
            {
                Assert.Equal(Argb.FromRgb(0xFF0000), image.GetPixel(0, 0).ToArgb());
                Assert.Equal(0, image.GetPixel(3, 3).A);
            }

            UiTestSupport.Handle(form, new
            {
                action = "batch_frames",
                arguments = new
                {
                    fps = 12,
                    frames = new object[]
                    {
                        new { copy = true, operations = new[] { new { type = "paint", pixels = new[] { new { x = 1, y = 0, color = "#00FF00" } } } } },
                        new { copy = true, operations = new[] { new { type = "paint", pixels = new[] { new { x = 2, y = 0, color = "#0000FF" } } } } }
                    }
                }
            });
            var frames = UiTestSupport.Field<List<AnimationFrame>>(form, "_frames");
            Assert.Equal(3, frames.Count);
            Assert.Equal(Argb.FromRgb(0xFF0000), frames[1].Layers[0].Grid.Get(0, 0));
            Assert.Equal(Argb.FromRgb(0x00FF00), frames[2].Layers[0].Grid.Get(1, 0));
            Assert.Equal(Argb.FromRgb(0x0000FF), frames[2].Layers[0].Grid.Get(2, 0));

            var badFrames = new
            {
                action = "batch_frames",
                arguments = new
                {
                    fps = 20,
                    frames = new object[]
                    {
                        new { copy = true },
                        new { operations = new[] { new { type = "paint", pixels = new[] { new { x = 99, y = 0, color = "#FF0000" } } } } }
                    }
                }
            };
            var ex = Assert.Throws<TargetInvocationException>(() => UiTestSupport.Handle(form, badFrames));
            Assert.IsType<ArgumentException>(UiTestSupport.Inner<ArgumentException>(ex));
            Assert.Equal(3, frames.Count);
            Assert.Equal(12, UiTestSupport.Field<int>(form, "_fps"));

            var onion = (CheckBox)UiTestSupport.Field<CheckBox>(form, "_onionCheck")!;
            var onionResult = Data(J(UiTestSupport.Handle(form, new { action = "onion_skin", arguments = new { enabled = true } })));
            Assert.True(onion.Checked);
            Assert.True(onionResult["onionEnabled"]!.GetValue<bool>());
            Assert.True(onionResult["onionVisible"]!.GetValue<bool>());

            var canvas = UiTestSupport.Field<CanvasControl>(form, "_canvas");
            Assert.NotNull(UiTestSupport.Field<object?>(canvas, "_onionPrevious"));
            Assert.Equal(0, canvas.NativeBitmap.GetPixel(3, 3).A);

            var chosenFrame = frames[2];
            UiTestSupport.Handle(form, new { action = "move_frame", arguments = new { frame = 2, target_index = 0 } });
            Assert.Same(chosenFrame, frames[0]);
            Assert.Equal(0, UiTestSupport.Field<int>(form, "_activeFrame"));

            string reorderedPath = dir.File("reordered.pxproj");
            UiTestSupport.Handle(form, new { action = "save_project", arguments = new { path = reorderedPath } });
            var reordered = ProjectFile.TryLoad(reorderedPath, out _);
            Assert.NotNull(reordered);
            Assert.Equal(Argb.FromRgb(0x0000FF), reordered!.Frames[0].Layers[0].Grid.Get(2, 0));

            UiTestSupport.Invoke(form, "ToggleAnimation");
            Assert.Null(UiTestSupport.Field<object?>(canvas, "_onionNext"));
            var playingInfo = Data(J(UiTestSupport.Handle(form, new { action = "info", arguments = new { } })));
            Assert.True(playingInfo["onionEnabled"]!.GetValue<bool>());
            Assert.False(playingInfo["onionVisible"]!.GetValue<bool>());

            var playingEx = Assert.Throws<TargetInvocationException>(() =>
                UiTestSupport.Handle(form, new { action = "move_frame", arguments = new { frame = 0, target_index = 1 } }));
            Assert.IsType<InvalidOperationException>(UiTestSupport.Inner<InvalidOperationException>(playingEx));

            UiTestSupport.Invoke(form, "ToggleAnimation");
            var timeline = UiTestSupport.Field<Control>(form, "_timeline");
            timeline.CreateControl();
            timeline.GetType().GetMethod("OnMouseDown", Flags)!.Invoke(timeline, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 10, 10, 0) });
            timeline.GetType().GetMethod("OnMouseUp", Flags)!.Invoke(timeline, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 170, 10, 0) });
            Assert.Same(chosenFrame, frames[2]);
        });
    }

    [Fact(Skip = "需要交互式桌面 STA 会话，不在无头或 CI 环境运行")]
    public void Palette_ReplaceSaveLoad_AndPresetLifecycle()
    {
        using var dir = new TempDir();
        UiTestSupport.RunInSta(() =>
        {
            using var form = new MainForm(4, 4);
            form.CreateControl();
            var grid = UiTestSupport.Field<PixelGrid>(form, "_grid");

            UiTestSupport.Handle(form, new { action = "paint", arguments = new { pixels = new[] { new { x = 0, y = 0, color = "#FF0000" }, new { x = 1, y = 0, color = "#FF0000" } } } });
            var replaced = J(UiTestSupport.Handle(form, new { action = "replace_color", arguments = new { from_color = "#FF0000", to_color = "#00FF00" } }));
            Assert.Equal(2, Data(replaced)["replaced"]!.GetValue<int>());
            Assert.Equal(Argb.FromRgb(0x00FF00), grid.Get(1, 0));

            UiTestSupport.Handle(form, new { action = "undo", arguments = new { } });
            Assert.Equal(Argb.FromRgb(0xFF0000), grid.Get(0, 0));
            Assert.Equal(Argb.FromRgb(0xFF0000), grid.Get(1, 0));

            UiTestSupport.Handle(form, new { action = "palette_set", arguments = new { colors = new[] { "#123456", "ABCDEF" } } });
            string palettePath = Path.Combine(dir.Path, "palette.pxpal");
            UiTestSupport.Handle(form, new { action = "palette_save", arguments = new { path = palettePath } });
            UiTestSupport.Handle(form, new { action = "palette_set", arguments = new { colors = new[] { "#000000" } } });
            UiTestSupport.Handle(form, new { action = "palette_load", arguments = new { path = palettePath } });
            var paletteResult = J(UiTestSupport.Handle(form, new { action = "palette_get", arguments = new { } }));
            Assert.Contains("#123456", paletteResult.ToJsonString());
            Assert.Contains("#ABCDEF", paletteResult.ToJsonString());

            string invalidPalette = Path.Combine(dir.Path, "broken.pxpal");
            File.WriteAllText(invalidPalette, "{\"format\":\"pixelcanvas-palette\",\"version\":1,\"colors\":[\"bad\"]}");
            var badEx = Assert.Throws<TargetInvocationException>(() =>
                UiTestSupport.Handle(form, new { action = "palette_load", arguments = new { path = invalidPalette } }));
            Assert.IsType<ArgumentException>(UiTestSupport.Inner<ArgumentException>(badEx));
            Assert.Contains("#123456", J(UiTestSupport.Handle(form, new { action = "palette_get", arguments = new { } })).ToJsonString());

            string? oldPresetDir = Environment.GetEnvironmentVariable("PIXELCANVAS_PRESET_DIR");
            string? oldOutputDir = Environment.GetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR");
            try
            {
                Environment.SetEnvironmentVariable("PIXELCANVAS_PRESET_DIR", dir.Path);
                Environment.SetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR", dir.Path);
                UiTestSupport.Handle(form, new { action = "presets_put", arguments = new { name = "test", format = "gif", scale = 2, fps = 12, columns = 2, directory = "" } });
                var dup = Assert.Throws<TargetInvocationException>(() =>
                    UiTestSupport.Handle(form, new { action = "presets_put", arguments = new { name = "test", format = "png" } }));
                Assert.IsType<IOException>(UiTestSupport.Inner<IOException>(dup));

                var exported = Data(J(UiTestSupport.Handle(form, new { action = "export_preset", arguments = new { name = "test" } })));
                string exportedPath = exported["path"]!.GetValue<string>();
                Assert.True(File.Exists(exportedPath));
                Assert.Equal(".gif", Path.GetExtension(exportedPath));
                Assert.Equal(12, exported["fps"]!.GetValue<int>());
                Assert.Equal(8, UiTestSupport.Field<int>(form, "_fps"));

                Assert.Contains("test", J(UiTestSupport.Handle(form, new { action = "presets_list", arguments = new { } })).ToJsonString());
                var deleteConfirm = Assert.Throws<TargetInvocationException>(() =>
                    UiTestSupport.Handle(form, new { action = "presets_delete", arguments = new { name = "test", confirm = false } }));
                Assert.IsType<ArgumentException>(UiTestSupport.Inner<ArgumentException>(deleteConfirm));
                UiTestSupport.Handle(form, new { action = "presets_delete", arguments = new { name = "test", confirm = true } });
                Assert.DoesNotContain("test", J(UiTestSupport.Handle(form, new { action = "presets_list", arguments = new { } })).ToJsonString());
            }
            finally
            {
                Environment.SetEnvironmentVariable("PIXELCANVAS_PRESET_DIR", oldPresetDir);
                Environment.SetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR", oldOutputDir);
            }
        });
    }

    [Fact(Skip = "需要交互式桌面 STA 会话，不在无头或 CI 环境运行")]
    public void Mirror_LayerComposite_AndReferenceImage()
    {
        using var dir = new TempDir();
        UiTestSupport.RunInSta(() =>
        {
            using var form = new MainForm(5, 5);
            form.CreateControl();
            var grid = UiTestSupport.Field<PixelGrid>(form, "_grid");

            UiTestSupport.Handle(form, new { action = "paint", arguments = new { mirror_h = true, mirror_v = true, pixels = new[] { new { x = 0, y = 1, color = "#FF0000" } } } });
            Assert.Equal(Argb.FromRgb(0xFF0000), grid.Get(0, 1));
            Assert.Equal(Argb.FromRgb(0xFF0000), grid.Get(4, 1));
            Assert.Equal(Argb.FromRgb(0xFF0000), grid.Get(0, 3));
            Assert.Equal(Argb.FromRgb(0xFF0000), grid.Get(4, 3));

            UiTestSupport.Handle(form, new { action = "undo", arguments = new { } });
            Assert.Equal(Argb.Transparent, grid.Get(4, 3));

            UiTestSupport.Handle(form, new { action = "shape", arguments = new { shape = "line", x1 = 0, y1 = 0, x2 = 1, y2 = 0, color = "#FF0000", mirror_h = true } });
            Assert.Equal(Argb.FromRgb(0xFF0000), grid.Get(4, 0));

            UiTestSupport.Handle(form, new { action = "add_layer", arguments = new { } });
            UiTestSupport.Handle(form, new { action = "paint", arguments = new { pixels = new[] { new { x = 0, y = 0, color = "#00FF00" } } } });
            var composite = Data(J(UiTestSupport.Handle(form, new { action = "read_composite", arguments = new { x = 0, y = 0 } })));
            Assert.Equal("#00FF00", composite["color"]!.GetValue<string>());

            UiTestSupport.Handle(form, new { action = "layer_visibility", arguments = new { layer = 1, visible = false } });
            composite = Data(J(UiTestSupport.Handle(form, new { action = "read_composite", arguments = new { x = 0, y = 0 } })));
            Assert.Equal("#FF0000", composite["color"]!.GetValue<string>());

            string reference = Path.Combine(dir.Path, "reference.png");
            using (var image = new Bitmap(3, 2)) image.Save(reference);
            UiTestSupport.Handle(form, new { action = "reference_load", arguments = new { path = reference } });
            var referenceState = Data(J(UiTestSupport.Handle(form, new { action = "reference_settings", arguments = new { visible = false, opacity = 70 } })));
            Assert.True(referenceState["loaded"]!.GetValue<bool>());
            Assert.False(referenceState["visible"]!.GetValue<bool>());
            Assert.True(Math.Abs(referenceState["opacity"]!.GetValue<float>() - 0.7f) < 0.001f);

            var missing = Assert.Throws<TargetInvocationException>(() =>
                UiTestSupport.Handle(form, new { action = "reference_load", arguments = new { path = Path.Combine(dir.Path, "missing.png") } }));
            Assert.IsAssignableFrom<IOException>(missing.InnerException);
            referenceState = Data(J(UiTestSupport.Handle(form, new { action = "reference_info", arguments = new { } })));
            Assert.True(referenceState["loaded"]!.GetValue<bool>());

            UiTestSupport.Handle(form, new { action = "reference_clear", arguments = new { } });
        });
    }

    [Fact(Skip = "需要交互式桌面 STA 会话，不在无头或 CI 环境运行")]
    public void FillAndGradient_MirrorDirection()
    {
        UiTestSupport.RunInSta(() =>
        {
            using var form = new MainForm(5, 5);
            form.CreateControl();
            var grid = UiTestSupport.Field<PixelGrid>(form, "_grid");

            UiTestSupport.Handle(form, new { action = "shape", arguments = new { shape = "line", x1 = 2, y1 = 0, x2 = 2, y2 = 4, color = "#000000" } });
            UiTestSupport.Handle(form, new { action = "fill", arguments = new { x = 0, y = 0, color = "#FF0000", mirror_h = true } });
            Assert.Equal(Argb.FromRgb(0xFF0000), grid.Get(4, 0));

            UiTestSupport.Handle(form, new { action = "undo", arguments = new { } });
            UiTestSupport.Handle(form, new { action = "gradient", arguments = new { x = 0, y = 0, end_x = 1, end_y = 0, start_color = "#FF0000", end_color = "#0000FF", mirror_h = true } });
            Assert.Equal(Argb.FromRgb(0xFF0000), grid.Get(4, 0));
            Assert.Equal(Argb.FromRgb(0x0000FF), grid.Get(3, 0));
        });
    }

    [Fact(Skip = "需要交互式桌面 STA 会话，不在无头或 CI 环境运行")]
    public void Selection_Layer_View_AndResize()
    {
        using var dir = new TempDir();
        UiTestSupport.RunInSta(() =>
        {
            using var form = new MainForm(5, 5);
            form.CreateControl();

            string? priorRoot = Environment.GetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR");
            Environment.SetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR", dir.Path);
            try
            {
                string PathFor(object request) => Data(J(UiTestSupport.Handle(form, request)))["path"]!.GetValue<string>();
                string sheet = PathFor(new { action = "export_sprite_sheet", arguments = new { } });
                string canvasPng = PathFor(new { action = "export", arguments = new { } });
                string gif = PathFor(new { action = "export_gif", arguments = new { } });
                UiTestSupport.Handle(form, new { action = "add_frame", arguments = new { } });
                string project = PathFor(new { action = "save_copy", arguments = new { } });
                Assert.Equal(Path.Combine(dir.Path, "动画"), Path.GetDirectoryName(sheet));
                Assert.Equal(Path.Combine(dir.Path, "PNG"), Path.GetDirectoryName(canvasPng));
                Assert.Equal(Path.Combine(dir.Path, "动画"), Path.GetDirectoryName(gif));
                Assert.Equal(Path.Combine(dir.Path, "工程"), Path.GetDirectoryName(project));

                string explicitPng = Path.Combine(dir.Path, "画布显式", "嵌套", "图片.png");
                Assert.Equal(explicitPng, PathFor(new { action = "export", arguments = new { path = explicitPng } }));
                Assert.True(File.Exists(explicitPng));
                string explicitProject = Path.Combine(dir.Path, "画布工程", "嵌套", "新建.pxproj");
                Assert.Equal(explicitProject, PathFor(new { action = "save_project", arguments = new { path = explicitProject } }));
                Assert.True(File.Exists(explicitProject));
            }
            finally
            {
                Environment.SetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR", priorRoot);
            }

            UiTestSupport.Handle(form, new { action = "paint", arguments = new { pixels = new[] { new { x = 0, y = 0, color = "#123456" } } } });
            UiTestSupport.Handle(form, new { action = "selection_set", arguments = new { x = 0, y = 0, width = 1, height = 1 } });
            UiTestSupport.Handle(form, new { action = "selection_copy", arguments = new { } });
            var pasted = Data(J(UiTestSupport.Handle(form, new { action = "selection_paste", arguments = new { } })));
            Assert.True(pasted["changed"]!.GetValue<bool>());
            // add_frame 已切换过帧，_grid 指向新帧网格；断言前重新取字段，不能缓存旧引用。
            Assert.Equal(Argb.FromRgb(0x123456), UiTestSupport.Field<PixelGrid>(form, "_grid").Get(1, 1));

            var clearConfirm = Assert.Throws<TargetInvocationException>(() =>
                UiTestSupport.Handle(form, new { action = "clear_layer", arguments = new { confirm = false } }));
            Assert.IsType<ArgumentException>(UiTestSupport.Inner<ArgumentException>(clearConfirm));
            UiTestSupport.Handle(form, new { action = "clear_layer", arguments = new { confirm = true } });
            UiTestSupport.Handle(form, new { action = "undo", arguments = new { } });
            Assert.Equal(Argb.FromRgb(0x123456), UiTestSupport.Field<PixelGrid>(form, "_grid").Get(1, 1));

            UiTestSupport.Handle(form, new { action = "add_layer", arguments = new { } });
            var selected = Data(J(UiTestSupport.Handle(form, new { action = "select_layer", arguments = new { layer = 0 } })));
            Assert.Equal(0, selected["activeLayer"]!.GetValue<int>());

            var options = Data(J(UiTestSupport.Handle(form, new { action = "draw_options_set", arguments = new { tool = "line", color = "#FF0000", brush_size = 3, mirror_h = true } })));
            Assert.Equal("line", options["tool"]!.GetValue<string>());
            Assert.True(options["mirrorH"]!.GetValue<bool>());

            var view = Data(J(UiTestSupport.Handle(form, new { action = "view_set", arguments = new { zoom = 3, grid = false, placement = "center" } })));
            Assert.Equal(3, view["zoom"]!.GetValue<int>());
            Assert.False(view["grid"]!.GetValue<bool>());

            var resizeConfirm = Assert.Throws<TargetInvocationException>(() =>
                UiTestSupport.Handle(form, new { action = "resize", arguments = new { width = 8, height = 8, confirm = false } }));
            Assert.IsType<ArgumentException>(UiTestSupport.Inner<ArgumentException>(resizeConfirm));
            Assert.Equal(Argb.FromRgb(0x123456), UiTestSupport.Field<PixelGrid>(form, "_grid").Get(0, 0));

            var resized = Data(J(UiTestSupport.Handle(form, new { action = "resize", arguments = new { width = 8, height = 8, confirm = true } })));
            Assert.Equal(8, resized["canvas"]!["width"]!.GetValue<int>());
            Assert.Equal(1, resized["canvas"]!["frameCount"]!.GetValue<int>());
        });
    }
}
