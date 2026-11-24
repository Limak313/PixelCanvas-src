using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using PixelCanvas;

namespace PixelCanvas.Tests;

/// <summary>
/// UI 冒烟测试共享辅助：在 STA 线程内创建 WinForms 窗体与控件，并通过反射访问 MainForm 私有成员。
/// 用例所在类标记 [Trait("Category", "UI")]，不进无头 CI 门禁；需要在交互式桌面会话里手动执行：
/// dotnet test PixelCanvas.Tests/PixelCanvas.Tests.csproj -c Release --filter "Category=UI"
/// 或 .\构建与测试.ps1 -Ui。
/// </summary>
internal static class UiTestSupport
{
    public const BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>在独立 STA 线程执行动作，Join 后把线程内异常抛回测试线程。</summary>
    public static void RunInSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw error;
    }

    public static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, InstanceNonPublic)!.GetValue(target)!;

    public static object Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, InstanceNonPublic)!.Invoke(target, args)!;

    /// <summary>以匿名对象序列化为 JSON 对象后，调用 MainForm.HandleMcpRequest。</summary>
    public static object Handle(MainForm form, object request)
    {
        var node = JsonSerializer.SerializeToNode(request) as JsonObject
            ?? throw new ArgumentException("请求必须序列化为 JSON 对象。");
        return typeof(MainForm).GetMethod("HandleMcpRequest", InstanceNonPublic)!.Invoke(form, new object[] { node })!;
    }

    /// <summary>解包反射调用抛出的 TargetInvocationException 内部异常。</summary>
    public static T? Inner<T>(TargetInvocationException ex) where T : Exception => ex.InnerException as T;
}
