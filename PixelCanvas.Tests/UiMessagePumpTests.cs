using System.Windows.Forms;
using PixelCanvas;
using Xunit;

namespace PixelCanvas.Tests;

/// <summary>
/// 消息泵烟测：用 Application.Run 搭一个真实 WinForms 消息循环，而不是只调用 DoEvents。
/// 用例仍标 Category=InteractiveUI，由 ui-probe 非阻塞测量托管 runner 是否支持；
/// 在探针稳定前不进入 PR 阻塞门禁。
/// </summary>
public sealed class UiMessagePumpTests
{
    [Fact]
    [Trait("Category", "InteractiveUI")]
    public void PlaybackTimer_AdvancesUnderApplicationRun()
    {
        UiTestSupport.RunInSta(() =>
        {
            using var form = new MainForm(4, 4);
            form.CreateControl();

            UiTestSupport.Handle(form, new
            {
                action = "paint",
                arguments = new { pixels = new[] { new { x = 0, y = 0, color = "#FF0000" } } }
            });
            UiTestSupport.Invoke(form, "AddAnimationFrame", true);
            UiTestSupport.Invoke(form, "SwitchFrame", 0);
            UiTestSupport.Field<NumericUpDown>(form, "_fpsControl")!.Value = 1;
            UiTestSupport.Invoke(form, "ToggleAnimation");
            Assert.True(UiTestSupport.Field<bool>(form, "_playing"));

            var animationTimer = UiTestSupport.Field<System.Windows.Forms.Timer>(form, "_animationTimer");
            int ticks = 0;
            animationTimer.Tick += (_, _) => ticks++;

            bool finished = false;
            bool timedOut = false;
            using var watchdog = new System.Threading.Timer(_ =>
            {
                if (finished) return;
                timedOut = true;
                try { Application.ExitThread(); } catch (InvalidOperationException) { }
            }, null, TimeSpan.FromMilliseconds(6000), Timeout.InfiniteTimeSpan);

            using var poll = new System.Windows.Forms.Timer { Interval = 50 };
            poll.Tick += (_, _) =>
            {
                if (UiTestSupport.Field<int>(form, "_activeFrame") == 0) return;
                finished = true;
                poll.Stop();
                Application.ExitThread();
            };

            using var context = new ApplicationContext();
            poll.Start();
            try
            {
                Application.Run(context);
            }
            finally
            {
                poll.Stop();
                if (UiTestSupport.Field<bool>(form, "_playing")) UiTestSupport.Invoke(form, "ToggleAnimation");
            }

            Assert.False(timedOut, "Application.Run 消息泵在 6 秒内没有完成播放切帧。");
            Assert.True(ticks > 0, "真实消息泵没有触发播放定时器。");
            Assert.Equal(1, UiTestSupport.Field<int>(form, "_activeFrame"));
        });
    }
}
