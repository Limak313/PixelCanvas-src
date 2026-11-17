using System.IO.Pipes;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PixelCanvas;

/// <summary>通过标准输入输出提供 MCP，并用当前用户专属命名管道连接画布。</summary>
public static class McpBridge
{
    private static int _lastOpenedId, _lastOpenedWidth, _lastOpenedHeight;
    private static long _lastOpenedAt;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonObject InfoSchema = Schema("""
        {"type":"object","properties":{"process_id":{"type":"integer","description":"PixelCanvas 进程 ID；仅有一个窗口时可省略"}},"additionalProperties":false}
        """);
    private static readonly JsonObject ReadSchema = Schema("""
        {"type":"object","properties":{"process_id":{"type":"integer"},"layer":{"type":"integer","description":"从 0 开始，默认为当前图层"},"x":{"type":"integer"},"y":{"type":"integer"},"width":{"type":"integer"},"height":{"type":"integer"}},"additionalProperties":false}
        """);
    private static readonly JsonObject PaintSchema = Schema("""
        {"type":"object","properties":{"process_id":{"type":"integer"},"layer":{"type":"integer","description":"从 0 开始，默认为当前图层"},"pixels":{"type":"array","minItems":1,"maxItems":4096,"items":{"type":"object","properties":{"x":{"type":"integer"},"y":{"type":"integer"},"color":{"type":"string","description":"#RRGGBB 或 transparent"}},"required":["x","y","color"],"additionalProperties":false}}},"required":["pixels"],"additionalProperties":false}
        """);
    private static readonly JsonObject ShapeSchema = Schema("""
        {"type":"object","properties":{"process_id":{"type":"integer"},"layer":{"type":"integer"},"shape":{"type":"string","enum":["line","rectangle","ellipse"]},"x1":{"type":"integer"},"y1":{"type":"integer"},"x2":{"type":"integer"},"y2":{"type":"integer"},"color":{"type":"string","description":"#RRGGBB 或 transparent"},"size":{"type":"integer","minimum":1,"maximum":4}},"required":["shape","x1","y1","x2","y2","color"],"additionalProperties":false}
        """);
    private static readonly JsonObject BatchSchema = Schema("""
        {"type":"object","properties":{"process_id":{"type":"integer"},"operations":{"type":"array","minItems":1,"maxItems":64,"items":{"type":"object","properties":{"type":{"type":"string","enum":["paint","shape","fill","gradient"]},"layer":{"type":"integer"},"pixels":{"type":"array","minItems":1,"maxItems":4096,"items":{"type":"object","properties":{"x":{"type":"integer"},"y":{"type":"integer"},"color":{"type":"string"}},"required":["x","y","color"],"additionalProperties":false}},"shape":{"type":"string","enum":["line","rectangle","ellipse"]},"x":{"type":"integer"},"y":{"type":"integer"},"x1":{"type":"integer"},"y1":{"type":"integer"},"x2":{"type":"integer"},"y2":{"type":"integer"},"end_x":{"type":"integer"},"end_y":{"type":"integer"},"color":{"type":"string"},"start_color":{"type":"string"},"end_color":{"type":"string"},"size":{"type":"integer","minimum":1,"maximum":4}},"required":["type"],"additionalProperties":false}}},"required":["operations"],"additionalProperties":false}
        """);
    private static JsonObject CreateArtworkSchema()
    {
        var operations = BatchSchema["properties"]!["operations"]!.DeepClone();
        var schema = Schema("""
            {"type":"object","properties":{"width":{"type":"integer","minimum":4,"maximum":512},"height":{"type":"integer","minimum":4,"maximum":512},"reuse_existing":{"type":"boolean","description":"尝试复用尺寸匹配、未修改的空白画布"},"process_id":{"type":"integer","description":"指定要复用的画布，须同时设置 reuse_existing=true"},"layers":{"type":"array","minItems":1,"maxItems":32,"description":"图层名称，从底层到顶层；每个操作必须明确指定 layer 索引","items":{"type":"string","minLength":1,"maxLength":100}},"project_path":{"type":"string","description":"可选，不存在的 .pxproj 绝对路径；省略使用默认产出目录"}},"required":["layers","operations"],"additionalProperties":false}
            """);
        schema["properties"]!["operations"] = operations;
        return schema;
    }
    private static JsonObject BatchFramesSchema()
    {
        var schema = Schema("""
            {"type":"object","properties":{"process_id":{"type":"integer"},"fps":{"type":"integer","minimum":1,"maximum":60},"frames":{"type":"array","minItems":1,"maxItems":32,"description":"依次追加到动画末尾；copy=true 复制上一帧（首帧复制当前帧）","items":{"type":"object","properties":{"copy":{"type":"boolean"}},"additionalProperties":false}}},"required":["frames"],"additionalProperties":false}
            """);
        var operations = BatchSchema["properties"]!["operations"]!.DeepClone();
        operations["minItems"] = 0;
        schema["properties"]!["frames"]!["items"]!["properties"]!["operations"] = operations;
        return schema;
    }
    private static readonly JsonObject FillSchema = Schema("""
        {"type":"object","properties":{"process_id":{"type":"integer"},"layer":{"type":"integer"},"x":{"type":"integer"},"y":{"type":"integer"},"color":{"type":"string","description":"#RRGGBB 或 transparent"}},"required":["x","y","color"],"additionalProperties":false}
        """);
    private static readonly JsonObject GradientSchema = Schema("""
        {"type":"object","properties":{"process_id":{"type":"integer"},"layer":{"type":"integer"},"x":{"type":"integer"},"y":{"type":"integer"},"end_x":{"type":"integer"},"end_y":{"type":"integer"},"start_color":{"type":"string","description":"#RRGGBB"},"end_color":{"type":"string","description":"#RRGGBB"}},"required":["x","y","end_x","end_y","start_color","end_color"],"additionalProperties":false}
        """);
    private static readonly JsonObject RegionSchema = Schema("""
        {"type":"object","properties":{"process_id":{"type":"integer"},"layer":{"type":"integer"},"x":{"type":"integer"},"y":{"type":"integer"},"width":{"type":"integer","minimum":1},"height":{"type":"integer","minimum":1},"dest_x":{"type":"integer"},"dest_y":{"type":"integer"}},"required":["x","y","width","height","dest_x","dest_y"],"additionalProperties":false}
        """);
    private static readonly JsonObject DeleteLayerSchema = Schema("""
        {"type":"object","properties":{"process_id":{"type":"integer"},"layer":{"type":"integer"},"confirm":{"type":"boolean","description":"删除不可撤销，须明确传入 true"}},"required":["confirm"],"additionalProperties":false}
        """);
    private static readonly JsonObject MoveLayerSchema = Schema("""
        {"type":"object","properties":{"process_id":{"type":"integer"},"layer":{"type":"integer"},"target_index":{"type":"integer","description":"目标索引，从底层 0 开始"}},"required":["target_index"],"additionalProperties":false}
        """);
    private static readonly JsonObject LayerVisibilitySchema = Schema("""
        {"type":"object","properties":{"process_id":{"type":"integer"},"layer":{"type":"integer"},"visible":{"type":"boolean"}},"required":["visible"],"additionalProperties":false}
        """);
    private static readonly JsonObject CanvasOutputSchema = Schema("""
        {"type":"object","properties":{"process_id":{"type":"integer"},"path":{"type":"string","description":"不存在的输出文件绝对路径；省略使用默认产出目录"},"scale":{"type":"integer","minimum":1,"maximum":16}},"additionalProperties":false}
        """);
    private static JsonObject SpriteSheetSchema(bool project)
    {
        var schema = project ? FileExportSchema() : (JsonObject)CanvasOutputSchema.DeepClone();
        schema["properties"]!["columns"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 64, ["description"] = "每行帧数；默认向上取整的帧数平方根" };
        return schema;
    }
    private static readonly JsonObject CanvasLayerSchema = Schema("""
        {"type":"object","properties":{"process_id":{"type":"integer"},"layer":{"type":"integer","description":"从 0 开始，默认为当前图层"}},"additionalProperties":false}
        """);
    static McpBridge()
    {
        void AddMirror(JsonNode properties)
        {
            properties["mirror_h"] = new JsonObject { ["type"] = "boolean", ["description"] = "左右镜像，默认 false" };
            properties["mirror_v"] = new JsonObject { ["type"] = "boolean", ["description"] = "上下镜像，默认 false" };
        }
        foreach (var schema in new[] { PaintSchema, ShapeSchema, FillSchema, GradientSchema }) AddMirror(schema["properties"]!);
        AddMirror(BatchSchema["properties"]!["operations"]!["items"]!["properties"]!);
    }
    private static JsonObject Schema(string s) => JsonNode.Parse(s)!.AsObject();
    private static JsonObject FileSchema(JsonObject source)
    {
        var result = (JsonObject)source.DeepClone();
        var properties = result["properties"]!.AsObject();
        properties.Remove("process_id");
        properties["path"] = new JsonObject { ["type"] = "string", ["description"] = "工程文件的绝对路径（.pxproj）" };
        var required = result["required"] as JsonArray;
        if (required is null) { required = new JsonArray(); result["required"] = required; }
        required.Add("path");
        return result;
    }

    public static async Task RunAsync()
    {
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        while (await input.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            JsonObject? request;
            try { request = JsonNode.Parse(line) as JsonObject ?? throw new JsonException(); }
            catch (JsonException)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32700, message = "Parse error" } })).ConfigureAwait(false);
                continue;
            }
            if (!request.ContainsKey("id")) continue;
            JsonNode? id = request["id"]?.DeepClone();
            object response;
            try
            {
                string method = McpCanvas.Text(request, "method");
                object result = method switch
                {
                    "initialize" => Initialize(request["params"] as JsonObject),
                    "ping" => new { },
                    "tools/list" => ListTools(),
                    "tools/call" => await CallToolAsync(request["params"] as JsonObject ?? throw new ArgumentException("Missing params")).ConfigureAwait(false),
                    _ => throw new NotSupportedException("Method not found"),
                };
                response = new { jsonrpc = "2.0", id, result };
            }
            catch (Exception ex)
            {
                response = new { jsonrpc = "2.0", id, error = new { code = ex is NotSupportedException ? -32601 : -32602, message = ex.Message } };
            }
            await output.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions)).ConfigureAwait(false);
        }
    }

    private static string ServerVersion()
    {
        var v = typeof(McpBridge).Assembly.GetName().Version;
        return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    private static object Initialize(JsonObject? args)
    {
        string? requested = args?["protocolVersion"]?.GetValue<string>();
        string version = requested is "2025-11-25" or "2025-06-18" or "2025-03-26" or "2024-11-05" ? requested : "2025-06-18";
        return new { protocolVersion = version, capabilities = new { tools = new { listChanged = false } },
            serverInfo = new { name = "pixelcanvas", version = ServerVersion() },
            instructions = "本地画布使用 process_id 区分窗口；图层索引从底部 0 开始。文件绘制保存到新的输出文件，不覆盖源文件。" };
    }

    private static object LegacyTools() => new { tools = new object[] {
        new { name = "open_canvas", description = "打开画布；reuse_existing=true 时可复用同尺寸未修改的空白窗口，多个候选需指定 process_id；force_new 新建。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"width\":{\"type\":\"integer\",\"minimum\":4,\"maximum\":512},\"height\":{\"type\":\"integer\",\"minimum\":4,\"maximum\":512},\"force_new\":{\"type\":\"boolean\"},\"reuse_existing\":{\"type\":\"boolean\"},\"process_id\":{\"type\":\"integer\",\"minimum\":1}},\"additionalProperties\":false}") },
        new { name = "create_artwork", description = "新开画布或复用未修改的空白画布、建立图层、批量绘画并保存为新工程；失败返回已完成阶段及窗口 ID。", inputSchema = CreateArtworkSchema() },
        new { name = "list_canvases", description = "列出本机运行中的 PixelCanvas 窗口及进程 ID。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}") },
        new { name = "canvas_info", description = "读取正在运行的画布尺寸、图层和当前图层。", inputSchema = InfoSchema },
        new { name = "canvas_select_layer", description = "切换当前选中图层，不修改像素。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"process_id\":{\"type\":\"integer\"},\"layer\":{\"type\":\"integer\",\"minimum\":0}},\"required\":[\"layer\"],\"additionalProperties\":false}") },
        new { name = "canvas_clear_layer", description = "清空指定图层，可撤销；必须显式确认。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"process_id\":{\"type\":\"integer\"},\"layer\":{\"type\":\"integer\"},\"confirm\":{\"type\":\"boolean\"}},\"required\":[\"confirm\"],\"additionalProperties\":false}") },
        new { name = "canvas_resize", description = "改变画布尺寸会清空所有帧和历史，必须显式确认。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"process_id\":{\"type\":\"integer\"},\"width\":{\"type\":\"integer\",\"minimum\":4,\"maximum\":512},\"height\":{\"type\":\"integer\",\"minimum\":4,\"maximum\":512},\"confirm\":{\"type\":\"boolean\"}},\"required\":[\"width\",\"height\",\"confirm\"],\"additionalProperties\":false}") },
        new { name = "canvas_selection_get", description = "读取界面选区和复制缓存状态。", inputSchema = InfoSchema },
        new { name = "canvas_selection_set", description = "设置界面矩形选区，或 clear=true 取消选区。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"process_id\":{\"type\":\"integer\"},\"x\":{\"type\":\"integer\"},\"y\":{\"type\":\"integer\"},\"width\":{\"type\":\"integer\"},\"height\":{\"type\":\"integer\"},\"clear\":{\"type\":\"boolean\"}},\"additionalProperties\":false}") },
        new { name = "canvas_selection_copy", description = "复制当前界面选区到软件内部缓存。", inputSchema = InfoSchema },
        new { name = "canvas_selection_paste", description = "粘贴内部选区缓存，位置和透明像素行为与界面一致。", inputSchema = InfoSchema },
        new { name = "canvas_draw_options_get", description = "读取界面当前工具、颜色、画笔和镜像设置。", inputSchema = InfoSchema },
        new { name = "canvas_draw_options_set", description = "设置界面工具、颜色、画笔大小和镜像；MCP 绘画仍需在调用时明确指定镜像。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"process_id\":{\"type\":\"integer\"},\"tool\":{\"type\":\"string\",\"enum\":[\"pencil\",\"eraser\",\"eyedropper\",\"fill\",\"line\",\"rectangle\",\"ellipse\",\"selection\",\"gradient\"]},\"color\":{\"type\":\"string\"},\"gradient_end\":{\"type\":\"string\"},\"brush_size\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":4},\"mirror_h\":{\"type\":\"boolean\"},\"mirror_v\":{\"type\":\"boolean\"}},\"additionalProperties\":false}") },
        new { name = "canvas_view_get", description = "读取缩放、滚动、画布位置、网格、沉浸和侧栏状态。", inputSchema = InfoSchema },
        new { name = "canvas_view_set", description = "设置画布视图；沉浸模式下侧栏设置需先退出沉浸。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"process_id\":{\"type\":\"integer\"},\"zoom\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":128},\"fit\":{\"type\":\"boolean\"},\"scroll_x\":{\"type\":\"integer\",\"minimum\":0},\"scroll_y\":{\"type\":\"integer\",\"minimum\":0},\"placement\":{\"type\":\"string\",\"enum\":[\"center\",\"top_left\"]},\"grid\":{\"type\":\"boolean\"},\"immersive\":{\"type\":\"boolean\"},\"left_sidebar\":{\"type\":\"boolean\"},\"right_sidebar\":{\"type\":\"boolean\"}},\"additionalProperties\":false}") },
        new { name = "canvas_preview", description = "读取当前帧可见图层合成后的透明 PNG 预览，不含参考图与网格。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"process_id\":{\"type\":\"integer\"},\"max_side\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":512}},\"additionalProperties\":false}") },
        new { name = "canvas_add_frame", description = "在当前帧后新增空白帧或复制当前帧并切换。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"process_id\":{\"type\":\"integer\"},\"copy\":{\"type\":\"boolean\",\"description\":\"true 复制当前帧，默认 false 新增空白帧\"}},\"additionalProperties\":false}") },
        new { name = "canvas_batch_frames", description = "一次向末尾追加最多 32 帧，并对每帧批量绘画；全部成功才写入。每帧最多 64 操作，合计最多 256 操作、16384 像素。追加帧不可整体撤销。", inputSchema = BatchFramesSchema() },
        new { name = "canvas_select_frame", description = "切换动画帧，索引从 0 开始。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"process_id\":{\"type\":\"integer\"},\"frame\":{\"type\":\"integer\",\"minimum\":0}},\"required\":[\"frame\"],\"additionalProperties\":false}") },
        new { name = "canvas_move_frame", description = "将动画帧移动到目标位置，索引从 0 开始，排序不可撤销；当前选中的帧保持选中。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"process_id\":{\"type\":\"integer\"},\"frame\":{\"type\":\"integer\",\"minimum\":0},\"target_index\":{\"type\":\"integer\",\"minimum\":0}},\"required\":[\"frame\",\"target_index\"],\"additionalProperties\":false}") },
        new { name = "canvas_set_onion_skin", description = "设置洋葱皮开关；仅影响窗口显示，播放期间暂时隐藏，不进入预览或导出。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"process_id\":{\"type\":\"integer\"},\"enabled\":{\"type\":\"boolean\"}},\"required\":[\"enabled\"],\"additionalProperties\":false}") },
        new { name = "canvas_delete_frame", description = "删除当前帧，不可撤销；须明确确认且至少保留一帧。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"process_id\":{\"type\":\"integer\"},\"confirm\":{\"type\":\"boolean\"}},\"required\":[\"confirm\"],\"additionalProperties\":false}") },
        new { name = "canvas_import_frame", description = "从本机绝对路径导入同尺寸 PNG、JPEG 或 BMP 图片作为新帧；alpha 小于 128 变透明，其余变不透明，不保留半透明。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"process_id\":{\"type\":\"integer\"},\"path\":{\"type\":\"string\"}},\"required\":[\"path\"],\"additionalProperties\":false}") },
        new { name = "canvas_set_playback", description = "开始或停止动画，可同时设置每秒 1–60 帧。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"process_id\":{\"type\":\"integer\"},\"playing\":{\"type\":\"boolean\"},\"fps\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":60}},\"required\":[\"playing\"],\"additionalProperties\":false}") },
        new { name = "canvas_read_pixels", description = "读取当前画布的指定图层区域，最多 4096 像素。", inputSchema = ReadSchema },
        new { name = "canvas_pick_composite_color", description = "读取当前帧指定坐标的可见图层合成颜色，不包含参考图、网格或洋葱皮。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"process_id\":{\"type\":\"integer\"},\"x\":{\"type\":\"integer\"},\"y\":{\"type\":\"integer\"}},\"required\":[\"x\",\"y\"],\"additionalProperties\":false}") },
        new { name = "canvas_reference_info", description = "读取参考图状态、原始尺寸和加载时的 sourcePath；文件可能已移动、修改或删除，图像为加载时的快照。", inputSchema = InfoSchema },
        new { name = "canvas_reference_image", description = "以 PNG 图片读取窗口中已加载的参考图，保留原始内容并限制最长边，供观察或临摹；不读取画布合成图。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"process_id\":{\"type\":\"integer\"},\"max_side\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":512,\"description\":\"预览最长边像素数，默认 512\"}},\"additionalProperties\":false}") },
        new { name = "canvas_reference_load", description = "从本机绝对路径加载 PNG、JPEG、BMP 或 GIF 参考图，不修改画布。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"process_id\":{\"type\":\"integer\"},\"path\":{\"type\":\"string\"}},\"required\":[\"path\"],\"additionalProperties\":false}") },
        new { name = "canvas_reference_settings", description = "设置参考图显示状态和不透明度百分比，至少传一个参数。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"process_id\":{\"type\":\"integer\"},\"visible\":{\"type\":\"boolean\"},\"opacity\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100}},\"additionalProperties\":false}") },
        new { name = "canvas_reference_clear", description = "移除已加载的参考图，不修改画布。", inputSchema = InfoSchema },
        new { name = "canvas_paint_pixels", description = "在运行中的画布批量绘制像素，一次调用对应一步撤销。", inputSchema = PaintSchema },
        new { name = "canvas_replace_color", description = "替换当前帧指定图层中匹配的全部颜色，可撤销；支持 transparent。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"process_id\":{\"type\":\"integer\"},\"layer\":{\"type\":\"integer\"},\"from_color\":{\"type\":\"string\"},\"to_color\":{\"type\":\"string\"}},\"required\":[\"from_color\",\"to_color\"],\"additionalProperties\":false}") },
        new { name = "canvas_palette_get", description = "读取当前工程调色板。", inputSchema = InfoSchema },
        new { name = "canvas_palette_set", description = "替换当前工程调色板；不改变画布像素。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"process_id\":{\"type\":\"integer\"},\"colors\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":1024,\"items\":{\"type\":\"string\"}}},\"required\":[\"colors\"],\"additionalProperties\":false}") },
        new { name = "canvas_palette_load", description = "从 .pxpal 文件加载调色板，不改变画布像素。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"process_id\":{\"type\":\"integer\"},\"path\":{\"type\":\"string\"}},\"required\":[\"path\"],\"additionalProperties\":false}") },
        new { name = "canvas_palette_save", description = "将调色板保存到新的 .pxpal 文件，不覆盖已有文件。", inputSchema = CanvasOutputSchema },
        new { name = "canvas_batch_paint", description = "依次批量绘制像素、形状、填充或渐变；全部成功才生效，每个受影响图层各一步撤销。最多64个操作，合计16384个像素。", inputSchema = BatchSchema },
        new { name = "canvas_draw_shape", description = "在运行中的画布绘制直线、空心矩形或椭圆，可撤销。", inputSchema = ShapeSchema },
        new { name = "canvas_fill", description = "填充起点所在的四连通同色区域，可撤销。", inputSchema = FillSchema },
        new { name = "canvas_gradient", description = "在起点所在的四连通同色区域绘制线性渐变，可撤销。", inputSchema = GradientSchema },
        new { name = "canvas_copy_region", description = "复制当前图层矩形区域到指定位置；透明像素不覆盖目标，可撤销。", inputSchema = RegionSchema },
        new { name = "canvas_move_region", description = "移动当前图层矩形区域到指定位置，清除源区域，可撤销。", inputSchema = RegionSchema },
        new { name = "canvas_add_layer", description = "在当前图层上方创建新图层。", inputSchema = InfoSchema },
        new { name = "canvas_delete_layer", description = "删除指定图层，不可撤销；须明确确认，至少保留一层。", inputSchema = DeleteLayerSchema },
        new { name = "canvas_move_layer", description = "将图层移动到目标索引，索引从底层 0 开始。", inputSchema = MoveLayerSchema },
        new { name = "canvas_set_layer_visibility", description = "设置指定图层的显示状态。", inputSchema = LayerVisibilitySchema },
        new { name = "canvas_undo", description = "撤销运行中画布指定图层的上一步操作。", inputSchema = CanvasLayerSchema },
        new { name = "canvas_redo", description = "重做运行中画布指定图层的操作。", inputSchema = CanvasLayerSchema },
        new { name = "canvas_save_copy", description = "将运行中的画布另存为新工程，不覆盖已有文件。", inputSchema = CanvasOutputSchema },
        new { name = "canvas_open_project", description = "将现有工程打开到未修改的画布窗口；如有未保存修改则拒绝。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"process_id\":{\"type\":\"integer\"},\"path\":{\"type\":\"string\",\"description\":\"现有 .pxproj 文件绝对路径\"}},\"required\":[\"path\"],\"additionalProperties\":false}") },
        new { name = "canvas_save_project", description = "保存当前工程；未命名画布省略路径时保存到默认产出目录，已有路径的工程将更新原文件。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"process_id\":{\"type\":\"integer\"},\"path\":{\"type\":\"string\",\"description\":\"未命名画布的全新 .pxproj 文件绝对路径\"}},\"additionalProperties\":false}") },
        new { name = "canvas_save_and_export", description = "保存当前工程并将当前帧导出到新 PNG；请检查返回的 pngExported，导出失败时工程可能已保存。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"process_id\":{\"type\":\"integer\"},\"project_path\":{\"type\":\"string\",\"description\":\"未命名工程的新 .pxproj 绝对路径\"},\"png_path\":{\"type\":\"string\",\"description\":\"不存在的 PNG 绝对路径\"},\"scale\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":16}},\"additionalProperties\":false}") },
        new { name = "canvas_export_png", description = "将运行中的画布导出到新的 PNG 文件。", inputSchema = CanvasOutputSchema },
        new { name = "canvas_export_gif", description = "将当前画布全部帧导出为循环 GIF；默认保存到动画文件夹。", inputSchema = CanvasOutputSchema },
        new { name = "canvas_export_sprite_sheet", description = "将当前画布全部帧按从左到右、从上到下排列为透明 PNG 精灵表；返回布局信息。", inputSchema = SpriteSheetSchema(false) },
        new { name = "canvas_export_presets_list", description = "列出本机共享的导出预设。", inputSchema = InfoSchema },
        new { name = "canvas_export_presets_put", description = "新增导出预设；替换同名预设须明确传入 overwrite=true。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"process_id\":{\"type\":\"integer\"},\"name\":{\"type\":\"string\"},\"format\":{\"type\":\"string\",\"enum\":[\"png\",\"gif\",\"sheet\"]},\"scale\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":16},\"fps\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":60},\"columns\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":64},\"directory\":{\"type\":\"string\",\"description\":\"输出目录绝对路径，空字符串使用默认目录\"},\"overwrite\":{\"type\":\"boolean\"}},\"required\":[\"name\",\"format\"],\"additionalProperties\":false}") },
        new { name = "canvas_export_presets_delete", description = "删除指定预设；需明确确认。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"process_id\":{\"type\":\"integer\"},\"name\":{\"type\":\"string\"},\"confirm\":{\"type\":\"boolean\"}},\"required\":[\"name\",\"confirm\"],\"additionalProperties\":false}") },
        new { name = "canvas_export_with_preset", description = "按已保存的预设导出当前画布到新的文件；GIF 的预设帧率不修改工程帧率。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"process_id\":{\"type\":\"integer\"},\"name\":{\"type\":\"string\"}},\"required\":[\"name\"],\"additionalProperties\":false}") },
        new { name = "project_create", description = "创建新的透明画布工程，不覆盖已有文件；省略路径时保存到默认产出目录。", inputSchema = Schema("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"width\":{\"type\":\"integer\",\"minimum\":4,\"maximum\":512},\"height\":{\"type\":\"integer\",\"minimum\":4,\"maximum\":512}},\"required\":[\"width\",\"height\"],\"additionalProperties\":false}") },
        new { name = "project_info", description = "读取磁盘工程的尺寸和图层，不影响打开的窗口。", inputSchema = FileSchema(InfoSchema) },
        new { name = "project_read_pixels", description = "读取工程文件中指定图层的像素。", inputSchema = FileSchema(ReadSchema) },
        new { name = "project_paint_pixels", description = "修改工程像素并另存到不存在的新文件，不覆盖源文件。", inputSchema = FilePaintSchema() },
        new { name = "project_draw_shape", description = "绘制形状并另存为新工程，不覆盖原文件。", inputSchema = FileOutputSchema(ShapeSchema) },
        new { name = "project_fill", description = "填充工程的连续同色区域并另存。", inputSchema = FileOutputSchema(FillSchema) },
        new { name = "project_gradient", description = "对工程的连续同色区域绘制线性渐变并另存。", inputSchema = FileOutputSchema(GradientSchema) },
        new { name = "project_copy_region", description = "复制工程中的矩形区域并另存。", inputSchema = FileOutputSchema(RegionSchema) },
        new { name = "project_move_region", description = "移动工程中的矩形区域并另存。", inputSchema = FileOutputSchema(RegionSchema) },
        new { name = "project_add_layer", description = "添加图层并另存为新工程。", inputSchema = FileOutputSchema(InfoSchema) },
        new { name = "project_delete_layer", description = "删除指定图层并另存，不覆盖原文件。", inputSchema = FileOutputSchema(DeleteLayerSchema) },
        new { name = "project_move_layer", description = "调整图层顺序并另存。", inputSchema = FileOutputSchema(MoveLayerSchema) },
        new { name = "project_set_layer_visibility", description = "设置图层显隐并另存。", inputSchema = FileOutputSchema(LayerVisibilitySchema) },
        new { name = "project_export_png", description = "导出工程可见图层到新的 PNG 文件。", inputSchema = FileExportSchema() },
        new { name = "project_export_gif", description = "将工程全部帧导出为循环 GIF；默认保存到动画文件夹。", inputSchema = GifExportSchema() },
        new { name = "project_export_sprite_sheet", description = "将工程全部帧导出为透明 PNG 精灵表；返回布局信息。", inputSchema = SpriteSheetSchema(true) },
    } };

    // Keep the original names callable for existing clients, while advertising a smaller
    // set of grouped tools. The schema lookup exposes the exact original contracts.
    private static JsonArray ToolDefinitions() =>
        (JsonSerializer.SerializeToNode(LegacyTools(), JsonOptions)!["tools"] as JsonArray)!;

    private static string ToolGroup(string name)
    {
        if (name.StartsWith("project_", StringComparison.Ordinal)) return "pixelcanvas_project";
        if (name is "open_canvas" or "list_canvases" or "create_artwork" or "canvas_info" or "canvas_resize") return "pixelcanvas_canvas";
        if (name.Contains("frame") || name is "canvas_set_playback" or "canvas_set_onion_skin") return "pixelcanvas_animation";
        if (name.Contains("layer") || name.Contains("selection") || name is "canvas_undo" or "canvas_redo") return "pixelcanvas_layers";
        if (name.Contains("export") || name.Contains("save") || name == "canvas_open_project") return "pixelcanvas_files";
        if (name.Contains("reference") || name.Contains("view") || name.Contains("options") || name.Contains("palette")) return "pixelcanvas_settings";
        return "pixelcanvas_draw";
    }

    internal static object ListTools()
    {
        var definitions = ToolDefinitions().Select(node => (JsonObject)node!).ToArray();
        var tools = definitions.GroupBy(d => ToolGroup(d["name"]!.GetValue<string>()))
            .Select(group => new {
                name = group.Key,
                description = "按 action 执行操作；参数放入 arguments。调用 pixelcanvas_tool_info 查询操作的完整参数和限制。操作：" +
                    string.Join("；", group.Select(d => d["name"]!.GetValue<string>() + "：" + d["description"]!.GetValue<string>())),
                inputSchema = new JsonObject {
                    ["type"] = "object",
                    ["properties"] = new JsonObject {
                        ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(group.Select(d => (JsonNode?)JsonValue.Create(d["name"]!.GetValue<string>())).ToArray()) },
                        ["arguments"] = new JsonObject { ["type"] = "object", ["description"] = "操作参数；先通过 pixelcanvas_tool_info 查询精确格式。" }
                    },
                    ["required"] = new JsonArray("action", "arguments"), ["additionalProperties"] = false
                }
            }).Cast<object>().ToList();
        tools.Add(new { name = "pixelcanvas_tool_info", description = "查询一个 action 或一组 actions 的完整参数 schema；二者只能提供一个，批量最多 32 个。", inputSchema = new JsonObject {
            ["type"] = "object", ["properties"] = new JsonObject { ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(definitions.Select(d => (JsonNode?)JsonValue.Create(d["name"]!.GetValue<string>())).ToArray()) },
                ["actions"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 32, ["items"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(definitions.Select(d => (JsonNode?)JsonValue.Create(d["name"]!.GetValue<string>())).ToArray()) } } },
            ["oneOf"] = new JsonArray(new JsonObject { ["required"] = new JsonArray("action") }, new JsonObject { ["required"] = new JsonArray("actions") }), ["additionalProperties"] = false
        } });
        return new { tools };
    }

    private static JsonObject FilePaintSchema()
    {
        return FileOutputSchema(PaintSchema);
    }

    private static JsonObject FileOutputSchema(JsonObject source)
    {
        var schema = FileSchema(source);
        schema["properties"]!["output_path"] = new JsonObject { ["type"] = "string", ["description"] = "不存在的输出 .pxproj 绝对路径；省略时使用默认产出目录" };
        return schema;
    }

    private static JsonObject FileExportSchema()
    {
        var schema = FileOutputSchema(InfoSchema);
        schema["properties"]!["output_path"]!["description"] = "不存在的输出 .png 绝对路径；省略时使用默认产出目录";
        schema["properties"]!["scale"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 16 };
        return schema;
    }

    private static JsonObject GifExportSchema()
    {
        var schema = FileExportSchema();
        schema["properties"]!["output_path"]!["description"] = "不存在的 .gif 绝对路径；省略时使用动画文件夹";
        return schema;
    }

    internal static async Task<object> CallToolAsync(JsonObject request)
    {
        try
        {
            string name = McpCanvas.Text(request, "name");
            var args = request["arguments"] as JsonObject ?? new JsonObject();
            if (name == "pixelcanvas_tool_info")
            {
                if (args.ContainsKey("actions"))
                {
                    if (args.ContainsKey("action") || args["actions"] is not JsonArray actions || actions.Count is < 1 or > 32)
                        throw new ArgumentException("请提供 action 或 1–32 个 actions，不能同时提供。");
                    var definitions = ToolDefinitions();
                    var result = new JsonArray();
                    foreach (var item in actions)
                    {
                        if (item is not JsonValue value || !value.TryGetValue<string>(out string? action) || string.IsNullOrEmpty(action))
                            throw new ArgumentException("actions 必须包含有效的操作名称。");
                        var matched = definitions.FirstOrDefault(d => d!["name"]!.GetValue<string>() == action)
                            ?? throw new ArgumentException($"未知操作：{action}。");
                        result.Add(matched.DeepClone());
                    }
                    return new { content = new[] { new { type = "text", text = result.ToJsonString(JsonOptions) } }, isError = false };
                }
                string operation = McpCanvas.Text(args, "action");
                var definition = ToolDefinitions().FirstOrDefault(d => d!["name"]!.GetValue<string>() == operation)
                    ?? throw new ArgumentException("未知操作。");
                return new { content = new[] { new { type = "text", text = definition.ToJsonString(JsonOptions) } }, isError = false };
            }
            if (name.StartsWith("pixelcanvas_", StringComparison.Ordinal))
            {
                string operation = McpCanvas.Text(args, "action");
                if (!ToolDefinitions().Any(d => d!["name"]!.GetValue<string>() == operation && ToolGroup(operation) == name))
                    throw new ArgumentException("该操作不属于此工具或不存在。");
                args = args["arguments"] as JsonObject ?? throw new ArgumentException("arguments 必须是对象。");
                name = operation;
            }
            object data;
            if (name == "open_canvas") data = await OpenCanvasAsync(args).ConfigureAwait(false);
            else if (name == "create_artwork") data = await CreateArtworkAsync(args).ConfigureAwait(false);
            else if (name == "list_canvases") data = await ListCanvasesAsync().ConfigureAwait(false);
            else if (name.StartsWith("canvas_", StringComparison.Ordinal))
            {
                int id = McpCanvas.Number(args, "process_id");
                if (id < 0)
                {
                    var instances = await ListCanvasesAsync().ConfigureAwait(false);
                    if (instances.Count != 1) throw new ArgumentException("请先调用 list_canvases，并指定 process_id。");
                    id = instances[0];
                }
                string action = name switch { "canvas_info" => "info", "canvas_select_layer" => "select_layer", "canvas_clear_layer" => "clear_layer", "canvas_resize" => "resize", "canvas_selection_get" => "selection_get", "canvas_selection_set" => "selection_set", "canvas_selection_copy" => "selection_copy", "canvas_selection_paste" => "selection_paste", "canvas_draw_options_get" => "draw_options_get", "canvas_draw_options_set" => "draw_options_set", "canvas_view_get" => "view_get", "canvas_view_set" => "view_set", "canvas_preview" => "preview", "canvas_batch_frames" => "batch_frames", "canvas_add_frame" => "add_frame", "canvas_select_frame" => "select_frame", "canvas_move_frame" => "move_frame", "canvas_set_onion_skin" => "onion_skin", "canvas_delete_frame" => "delete_frame", "canvas_import_frame" => "import_frame", "canvas_set_playback" => "playback", "canvas_read_pixels" => "read", "canvas_pick_composite_color" => "read_composite", "canvas_reference_info" => "reference_info", "canvas_reference_load" => "reference_load", "canvas_reference_settings" => "reference_settings", "canvas_reference_clear" => "reference_clear", "canvas_reference_image" => "reference_image", "canvas_paint_pixels" => "paint", "canvas_replace_color" => "replace_color", "canvas_palette_get" => "palette_get", "canvas_palette_set" => "palette_set", "canvas_palette_load" => "palette_load", "canvas_palette_save" => "palette_save", "canvas_batch_paint" => "batch_paint", "canvas_draw_shape" => "shape", "canvas_fill" => "fill", "canvas_gradient" => "gradient", "canvas_copy_region" => "copy_region", "canvas_move_region" => "move_region", "canvas_add_layer" => "add_layer", "canvas_delete_layer" => "delete_layer", "canvas_move_layer" => "move_layer", "canvas_set_layer_visibility" => "layer_visibility", "canvas_undo" => "undo", "canvas_redo" => "redo", "canvas_save_copy" => "save_copy", "canvas_open_project" => "open_project", "canvas_save_project" => "save_project", "canvas_save_and_export" => "save_export", "canvas_export_presets_list" => "presets_list", "canvas_export_presets_put" => "presets_put", "canvas_export_presets_delete" => "presets_delete", "canvas_export_with_preset" => "export_preset", "canvas_export_png" => "export", "canvas_export_gif" => "export_gif", "canvas_export_sprite_sheet" => "export_sprite_sheet", _ => throw new ArgumentException("未知工具。") };
                var reply = await CallCanvasAsync(id, action, args, name is "canvas_batch_paint" or "canvas_batch_frames" or "canvas_save_and_export" or "canvas_export_gif" or "canvas_export_sprite_sheet" or "canvas_export_with_preset" ? 30000 : 5000).ConfigureAwait(false);
                if (reply["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException(reply["error"]?.GetValue<string>() ?? "画布操作失败。");
                data = reply["data"]?.DeepClone() ?? new JsonObject();
                if (name is "canvas_reference_image" or "canvas_preview" && data is JsonObject reference)
                {
                    string imageData = McpCanvas.Text(reference, "imageBase64");
                    reference.Remove("imageBase64");
                    return new { content = new object[] { new { type = "text", text = reference.ToJsonString(JsonOptions) }, new { type = "image", mimeType = "image/png", data = imageData } }, isError = false };
                }
            }
            else if (name.StartsWith("project_", StringComparison.Ordinal)) data = FileOperation(name, args);
            else throw new ArgumentException("未知工具。");
            return new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(data, JsonOptions) } }, isError = false };
        }
        catch (Exception ex)
        {
            return new { content = new[] { new { type = "text", text = ex.Message } }, isError = true };
        }
    }

    private static object FileOperation(string name, JsonObject args)
    {
        if (name == "project_create")
        {
            string target = McpCanvas.NewPath(args, "path", ".pxproj");
            int width = McpCanvas.Number(args, "width"), height = McpCanvas.Number(args, "height");
            if (width is < 4 or > 512 || height is < 4 or > 512) throw new ArgumentException("宽高必须为 4–512。");
            var layers = new List<Layer> { new("图层 1", new PixelGrid(width, height)) };
            ProjectFile.Save(target, layers, 0, Palettes.Db32, overwrite: false);
            return new { path = target, width, height };
        }
        string path = McpCanvas.Text(args, "path");
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetExtension(path), ".pxproj", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("path 必须是 .pxproj 文件的绝对路径。");
        var project = ProjectFile.TryLoad(path, out string error) ?? throw new ArgumentException("无法读取工程：" + error);
        return name switch
        {
            "project_info" => new { width = project.Grid.Width, height = project.Grid.Height, activeLayer = project.ActiveLayer, frameCount = project.Frames.Count, activeFrame = project.ActiveFrame, fps = project.FramesPerSecond, layers = project.Layers.Select((l, i) => new { index = i, name = l.Name, visible = l.Visible }).ToArray() },
            "project_read_pixels" => McpCanvas.Read(args, project.Layers, project.ActiveLayer),
            "project_paint_pixels" => PaintFile(path, args, project),
            "project_draw_shape" => ShapeFile(path, args, project),
            "project_fill" => FillFile(path, args, project, false),
            "project_gradient" => FillFile(path, args, project, true),
            "project_copy_region" => TransferFile(path, args, project, false),
            "project_move_region" => TransferFile(path, args, project, true),
            "project_add_layer" => AddFileLayer(path, args, project),
            "project_delete_layer" => ManageFileLayer(path, args, project, "delete"),
            "project_move_layer" => ManageFileLayer(path, args, project, "move"),
            "project_set_layer_visibility" => ManageFileLayer(path, args, project, "visibility"),
            "project_export_png" => ExportFile(args, project),
            "project_export_gif" => ExportGifFile(args, project),
            "project_export_sprite_sheet" => ExportSpriteSheetFile(args, project),
            _ => throw new ArgumentException("未知工具。"),
        };
    }

    private static object PaintFile(string path, JsonObject args, ProjectFile.LoadResult project)
    {
        string output = McpCanvas.NewPath(args, "output_path", ".pxproj", path, project.Frames.Count > 1);
        bool changed = McpCanvas.Paint(args, project.Layers, project.ActiveLayer);
        SaveFile(output, project, project.ActiveLayer);
        return new { changed, path = output };
    }

    private static object ShapeFile(string path, JsonObject args, ProjectFile.LoadResult project)
    {
        string output = McpCanvas.NewPath(args, "output_path", ".pxproj", path, project.Frames.Count > 1);
        bool changed = McpCanvas.Shape(args, project.Layers, project.ActiveLayer);
        SaveFile(output, project, project.ActiveLayer);
        return new { changed, path = output };
    }

    private static object AddFileLayer(string path, JsonObject args, ProjectFile.LoadResult project)
    {
        string output = McpCanvas.NewPath(args, "output_path", ".pxproj", path, project.Frames.Count > 1);
        if (project.Layers.Count >= 32) throw new ArgumentException("最多支持 32 个图层。");
        int number = 1;
        while (project.Layers.Any(l => l.Name == $"图层 {number}")) number++;
        int active = project.ActiveLayer + 1;
        project.Layers.Insert(active, new Layer($"图层 {number}", new PixelGrid(project.Grid.Width, project.Grid.Height)));
        SaveFile(output, project, active);
        return new { path = output, activeLayer = active };
    }

    private static object FillFile(string path, JsonObject args, ProjectFile.LoadResult project, bool gradient)
    {
        string output = McpCanvas.NewPath(args, "output_path", ".pxproj", path, project.Frames.Count > 1);
        bool changed = McpCanvas.Fill(args, project.Layers, project.ActiveLayer, gradient);
        SaveFile(output, project, project.ActiveLayer);
        return new { changed, path = output };
    }

    private static object ManageFileLayer(string path, JsonObject args, ProjectFile.LoadResult project, string action)
    {
        string output = McpCanvas.NewPath(args, "output_path", ".pxproj", path, project.Frames.Count > 1);
        var (active, changed) = McpCanvas.ManageLayer(args, project.Layers, project.ActiveLayer, action);
        SaveFile(output, project, active);
        return new { path = output, changed, canvas = McpCanvas.Info(project.Layers, active) };
    }

    private static object TransferFile(string path, JsonObject args, ProjectFile.LoadResult project, bool move)
    {
        string output = McpCanvas.NewPath(args, "output_path", ".pxproj", path, project.Frames.Count > 1);
        bool changed = McpCanvas.TransferRegion(args, project.Layers, project.ActiveLayer, move);
        SaveFile(output, project, project.ActiveLayer);
        return new { changed, path = output };
    }

    private static object ExportFile(JsonObject args, ProjectFile.LoadResult project)
    {
        string output = McpCanvas.NewPath(args, "output_path", ".png");
        McpCanvas.ExportPng(output, project.Layers, McpCanvas.Number(args, "scale", 1));
        return new { path = output };
    }

    private static object ExportGifFile(JsonObject args, ProjectFile.LoadResult project)
    {
        string output = McpCanvas.NewPath(args, "output_path", ".gif", animation: true);
        GifExport.Save(output, project.Frames, project.FramesPerSecond, McpCanvas.Number(args, "scale", 1));
        return new { path = output, frameCount = project.Frames.Count, fps = project.FramesPerSecond, loop = true };
    }

    private static object ExportSpriteSheetFile(JsonObject args, ProjectFile.LoadResult project)
    {
        string output = McpCanvas.NewPath(args, "output_path", ".png", animation: true);
        int columns = McpCanvas.Number(args, "columns", (int)Math.Ceiling(Math.Sqrt(project.Frames.Count)));
        var layout = SpriteSheetExport.Save(output, project.Frames, columns, McpCanvas.Number(args, "scale", 1));
        return new { path = output, layout = layout.ToData(), order = "row-major" };
    }

    private static void SaveFile(string output, ProjectFile.LoadResult project, int activeLayer)
    {
        project.Frames[project.ActiveFrame].Layers = project.Layers;
        project.Frames[project.ActiveFrame].ActiveLayer = activeLayer;
        ProjectFile.Save(output, project.Layers, activeLayer, project.Palette, overwrite: false,
            frames: project.Frames, activeFrame: project.ActiveFrame, framesPerSecond: project.FramesPerSecond);
    }

    private static async Task<object> CreateArtworkAsync(JsonObject args)
    {
        int width = McpCanvas.Number(args, "width", 32), height = McpCanvas.Number(args, "height", 32);
        if (width is < 4 or > 512 || height is < 4 or > 512) throw new ArgumentException("画布宽高必须为 4–512 的整数。");
        string path = McpCanvas.NewPath(args, "project_path", ".pxproj");
        args["project_path"] = path;
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new ArgumentException("工程保存目录不存在。");
        if (args["layers"] is not JsonArray layers || layers.Count is < 1 or > 32) throw new ArgumentException("layers 必须包含 1–32 个图层名称。");
        foreach (var item in layers)
            if (item is not JsonValue v || !v.TryGetValue<string>(out string? name) || string.IsNullOrWhiteSpace(name) || name.Length > 100)
                throw new ArgumentException("图层名称必须为 1–100 个字符的非空字符串。");
        if (args["operations"] is not JsonArray operations || operations.Count is < 1 or > 64) throw new ArgumentException("operations 必须包含 1–64 个操作。");
        foreach (var item in operations)
        {
            if (item is not JsonObject operation || operation["layer"] is null) throw new ArgumentException("每个操作必须明确指定 layer 图层索引。");
            int index = McpCanvas.Number(operation, "layer");
            if (index < 0 || index >= layers.Count) throw new ArgumentException("操作的图层索引超出范围。");
        }
        bool reuse = args["reuse_existing"]?.GetValue<bool>() ?? false;
        var openArgs = new JsonObject { ["width"] = width, ["height"] = height, ["force_new"] = !reuse, ["reuse_existing"] = reuse };
        if (args["process_id"] is not null) openArgs["process_id"] = args["process_id"]!.DeepClone();
        var opened = JsonSerializer.SerializeToNode(await OpenCanvasAsync(openArgs).ConfigureAwait(false), JsonOptions)!.AsObject();
        if (opened["processId"] is null)
            return new { success = false, completedStage = "not_started", outcome = "ambiguous", candidates = opened["candidates"]?.DeepClone(), error = "有多个可复用画布，请指定 process_id。" };
        int id = opened["processId"]!.GetValue<int>();
        bool reused = opened["reused"]?.GetValue<bool>() ?? false;
        if (opened["ready"]?.GetValue<bool>() != true)
            return new { success = false, processId = id, completedStage = "opened", error = "画布尚未就绪，请检查窗口，勿重复打开。", outcome = "not_started" };
        try
        {
            var reply = await CallCanvasAsync(id, "create_artwork", args, 30000).ConfigureAwait(false);
            if (reply["ok"]?.GetValue<bool>() != true)
                return new { success = false, processId = id, completedStage = "opened", error = reply["error"]?.GetValue<string>() ?? "画布操作失败。", outcome = "not_started" };
            var result = reply["data"]!.DeepClone().AsObject();
            result["processId"] = id;
            result["reused"] = reused;
            return result;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            return new { success = false, processId = id, completedStage = "opened", error = "通信中断，绘画或保存可能仍在进行，请先检查画布和工程文件，不要直接重试：" + ex.Message, outcome = "unknown" };
        }
    }

    /// <summary>判断 reuse_info 响应是否对应尺寸匹配、未修改的空白可复用画布。</summary>
    internal static bool MatchesReusable(JsonObject? reply, int width, int height)
    {
        var data = reply?["data"];
        return reply?["ok"]?.GetValue<bool>() == true
            && data?["reusable"]?.GetValue<bool>() == true
            && data?["width"]?.GetValue<int>() == width
            && data?["height"]?.GetValue<int>() == height;
    }

    private static async Task<object> OpenCanvasAsync(JsonObject args)
    {
        int width = McpCanvas.Number(args, "width", 32), height = McpCanvas.Number(args, "height", 32);
        if (width is < 4 or > 512 || height is < 4 or > 512) throw new ArgumentException("画布宽高必须为 4–512 的整数。");
        bool forceNew = args["force_new"]?.GetValue<bool>() ?? false;
        bool reuse = args["reuse_existing"]?.GetValue<bool>() ?? false;
        int requestedId = McpCanvas.Number(args, "process_id");
        if (args["process_id"] is not null && requestedId <= 0) throw new ArgumentException("process_id 必须为正整数。");
        if ((forceNew && reuse) || (!reuse && args["process_id"] is not null)) throw new ArgumentException("process_id 须配合 reuse_existing=true，且不能同时设置 force_new。");
        if (reuse)
        {
            var ids = requestedId > 0 ? new List<int> { requestedId } : await ListCanvasesAsync().ConfigureAwait(false);
            var matches = new List<int>();
            foreach (int candidate in ids)
            {
                try
                {
                    var reply = await CallCanvasAsync(candidate, "reuse_info", new JsonObject(), 1000).ConfigureAwait(false);
                    if (MatchesReusable(reply, width, height)) matches.Add(candidate);
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException) { }
            }
            if (matches.Count > 1) return new { ready = false, reused = false, candidates = matches, message = "多个空白画布符合条件，请指定 process_id。" };
            if (matches.Count == 1) return new { processId = matches[0], ready = true, reused = true };
            if (requestedId > 0) throw new InvalidOperationException("指定画布不可复用：可能尺寸不符、不是未修改的空白画布或无法连接。");
            forceNew = true;
        }
        if (!forceNew && _lastOpenedId > 0 && _lastOpenedWidth == width && _lastOpenedHeight == height && Environment.TickCount64 - _lastOpenedAt < 5000)
        {
            JsonObject? check = null;
            bool unreachable = false;
            try
            {
                check = await CallCanvasAsync(_lastOpenedId, "reuse_info", new JsonObject(), 500).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
            {
                unreachable = true;
            }

            if (unreachable)
            {
                return new { processId = _lastOpenedId, ready = false, reused = true, message = "刚启动的画布尚未响应，请检查窗口，不要重复启动。" };
            }

            bool reusable = MatchesReusable(check, width, height);

            if (reusable)
            {
                try
                {
                    var previous = await CallCanvasAsync(_lastOpenedId, "info", new JsonObject(), 500).ConfigureAwait(false);
                    if (previous["ok"]?.GetValue<bool>() == true)
                    {
                        var data = previous["data"];
                        bool ready = data?["ready"]?.GetValue<bool>() != false;
                        return new { processId = _lastOpenedId, ready, reused = true, canvas = data?.DeepClone() };
                    }
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException) { }
            }

            // 缓存窗已被修改、尺寸不符或暂不可用：不复用，新开窗口。
            forceNew = true;
        }
        string appPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "app", "PixelCanvas.exe"));
        if (!File.Exists(appPath)) throw new FileNotFoundException("找不到插件附带的 PixelCanvas 程序。", appPath);
        string bundledRuntime = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "runtime"));
        bool hasBundledRuntime = File.Exists(Path.Combine(bundledRuntime, "dotnet.exe"));
        var startInfo = new ProcessStartInfo(appPath) { UseShellExecute = !hasBundledRuntime, WorkingDirectory = Path.GetDirectoryName(appPath)! };
        if (hasBundledRuntime)
        {
            startInfo.Environment["DOTNET_ROOT_X64"] = bundledRuntime;
            startInfo.Environment["DOTNET_ROOT"] = bundledRuntime;
            startInfo.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        }
        startInfo.ArgumentList.Add(width.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(height.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 PixelCanvas。");
        int id = process.Id;
        _lastOpenedId = id; _lastOpenedWidth = width; _lastOpenedHeight = height; _lastOpenedAt = Environment.TickCount64;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!timeout.IsCancellationRequested)
        {
            if (process.HasExited) throw new InvalidOperationException("PixelCanvas 启动后已退出。");
            try
            {
                var reply = await CallCanvasAsync(id, "info", new JsonObject(), 500).ConfigureAwait(false);
                if (reply["ok"]?.GetValue<bool>() == true)
                {
                    var data = reply["data"];
                    bool ready = data?["ready"]?.GetValue<bool>() != false;
                    return new { processId = id, ready, canvas = data?.DeepClone() };
                }
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException) { }
            await Task.Delay(200).ConfigureAwait(false);
        }
        return new { processId = id, ready = false, message = "窗口已启动，但尚未响应；请检查窗口是否在等待操作。" };
    }

    private static async Task<List<int>> ListCanvasesAsync()
    {
        var candidates = new List<int>();
        foreach (string pipePath in Directory.EnumerateFiles(@"\\.\pipe\", "PixelCanvas.Mcp.*"))
        {
            string suffix = Path.GetFileName(pipePath)["PixelCanvas.Mcp.".Length..];
            if (!int.TryParse(suffix, out int id) || id <= 0) continue;
            candidates.Add(id);
        }
        var results = await Task.WhenAll(candidates.Distinct().Select(async id =>
        {
            try
            {
                var reply = await CallCanvasAsync(id, "info", new JsonObject(), 1000).ConfigureAwait(false);
                return reply["ok"]?.GetValue<bool>() == true ? id : 0;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException) { return 0; }
        })).ConfigureAwait(false);
        return results.Where(id => id > 0).ToList();
    }

    private static async Task<JsonObject> CallCanvasAsync(int id, string action, JsonObject args, int timeoutMs = 5000)
    {
        if (id <= 0) throw new ArgumentException("process_id 必须为正整数。");
        using var timeout = new CancellationTokenSource(timeoutMs);
        await using var pipe = new NamedPipeClientStream(".", $"PixelCanvas.Mcp.{id}", PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { action, arguments = args })).ConfigureAwait(false);
        string? line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        return JsonNode.Parse(line ?? throw new IOException("画布未返回响应。")) as JsonObject ?? throw new IOException("画布响应格式不正确。");
    }
}

