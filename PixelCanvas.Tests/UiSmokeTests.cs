using Xunit;

namespace PixelCanvas.Tests;

/// <summary>
/// 交互式 WinForms 冒烟测试：需要桌面 STA 会话，默认跳过。
/// 历史断言覆盖帧切换、独立撤销历史、播放定时器、批量绘画、双轴镜像、选区复制粘贴、
/// 洋葱皮、导出预设与参考图等界面路径（原控制台程序的 --ui 分支）。
/// 手动运行说明见 README；后续可迁入独立 STA 集成测试后再启用。
/// </summary>
public class UiSmokeTests
{
    [Fact(Skip = "需要交互式桌面 STA 会话，不在无头或 CI 环境运行")]
    public void InteractiveCanvas_Smoke()
    {
        // 占位：完整界面反射断言待迁入 STA 集成测试。
    }
}
