using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Shimeji.Core;
using Shimeji.Platform.Win32;

namespace Shimeji.Tests
{
    public class PointerInputTests
    {
        readonly List<PointerEvent> _out = new List<PointerEvent>();

        [SetUp]
        public void Clear() => _out.Clear();

        [Test]
        public void ShortPressIsClick_DoubleClickDetected()
        {
            var t = new PointerTracker();
            t.Update(0.00, new Vec2(10, 10), false, false, 1, _out);
            t.Update(0.05, new Vec2(10, 10), true, false, 1, _out);
            t.Update(0.10, new Vec2(11, 10), false, false, 1, _out);
            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(PointerEventType.Click, _out[0].Type);
            Assert.AreEqual(1, _out[0].TargetId);
            _out.Clear();
            t.Update(0.20, new Vec2(11, 10), true, false, 1, _out);
            t.Update(0.25, new Vec2(11, 10), false, false, 1, _out);
            Assert.AreEqual(PointerEventType.DoubleClick, _out.Single().Type);
        }

        [Test]
        public void DragStartMoveEndWithVelocity_TargetLatchedAtPress()
        {
            var t = new PointerTracker();
            t.Update(0.000, new Vec2(100, 100), true, false, 7, _out);
            double time = 0;
            for (int i = 1; i <= 10; i++)
            {
                time = i * 0.016;
                // 캐릭터 밖으로 벗어나도(hit=-1) 드래그 대상 유지
                t.Update(time, new Vec2(100 + i * 20, 100), true, false, -1, _out);
            }
            t.Update(time + 0.016, new Vec2(320, 100), false, false, -1, _out);
            Assert.AreEqual(PointerEventType.DragStart, _out.First().Type);
            Assert.IsTrue(_out.All(e => e.TargetId == 7));
            var end = _out.Last();
            Assert.AreEqual(PointerEventType.DragEnd, end.Type);
            Assert.Greater(end.Velocity.X, 800f); // 20px / 16ms ≈ 1250 px/s
        }

        [Test]
        public void PressOnEmptyDesktopProducesNothing_RightClickOnTarget()
        {
            var t = new PointerTracker();
            t.Update(0, new Vec2(0, 0), true, false, -1, _out);
            t.Update(0.1, new Vec2(50, 0), true, false, 3, _out);
            t.Update(0.2, new Vec2(50, 0), false, false, 3, _out);
            Assert.IsEmpty(_out);
            t.Update(0.3, new Vec2(50, 0), false, true, 3, _out);
            t.Update(0.4, new Vec2(50, 0), false, false, 3, _out);
            Assert.AreEqual(PointerEventType.RightClick, _out.Single().Type);
        }

        [Test]
        public void ClickThroughLatchesWhileButtonHeld()
        {
            var c = new ClickThroughController();
            Assert.IsFalse(c.Update(false, false));
            Assert.IsTrue(c.Update(true, false));             // 캐릭터 위 → 입력 받음
            Assert.IsTrue(c.Update(true, true));              // 눌림 시작 (캐릭터 위)
            Assert.IsTrue(c.Update(false, true));             // 빠르게 벗어나도 유지
            Assert.IsFalse(c.Update(false, false));           // 놓으면 해제
            Assert.IsFalse(c.Update(false, true));            // 바탕화면에서 누름 시작
            Assert.IsFalse(c.Update(true, true));             // 드래그 중 캐릭터 위를 지나도 가로채지 않음
            Assert.IsTrue(c.Update(true, false));
        }
    }

    public class Win32SmokeTests
    {
        [Test]
        public void DesktopApiReturnsMonitorsAndWindows()
        {
            var api = new Win32DesktopApi();
            var monitors = api.GetMonitors();
            Assert.GreaterOrEqual(monitors.Count, 1);
            foreach (var m in monitors)
            {
                Assert.Greater(m.Bounds.Width, 0);
                Assert.Greater(m.WorkArea.Height, 0);
                Assert.IsTrue(m.Bounds.ContainsInclusive(new Vec2(m.WorkArea.Left, m.WorkArea.Top)));
            }
            Assert.IsTrue(monitors.Any(m => m.Primary));
            var windows = api.GetWindows();
            Assert.IsNotNull(windows);
            var world = new DesktopWorld();
            world.Refresh(api, 0, 0);
            Assert.Greater(world.Surfaces.Count, 3);
            TestContext.WriteLine($"monitors={monitors.Count} rawWindows={windows.Count} filtered={world.Windows.Count} surfaces={world.Surfaces.Count}");
        }

        [Test]
        public void PointerApiDoesNotThrow()
        {
            var p = new Win32PointerApi();
            var pos = p.GetCursorPosition();
            Assert.IsFalse(float.IsNaN(pos.X));
            p.IsLeftButtonDown();
            p.IsRightButtonDown();
            OverlayWindow.IsFullscreenAppActive(System.IntPtr.Zero);
            var r = OverlayWindow.OverlayRectFor(new RectPx(-100, 0, 1920, 1080));
            Assert.AreEqual(new RectPx(-100, 1, 1920, 1079), r);
        }

        [Test]
        public void AutoStartRegistryRoundTrip()
        {
            const string name = "ShimejiUnitTest_DeleteMe";
            try
            {
                Assert.IsTrue(AutoStart.SetEnabled(true, @"C:\Test\Shimeji.exe", name));
                Assert.IsTrue(AutoStart.IsEnabled(name));
                Assert.AreEqual("\"C:\\Test\\Shimeji.exe\"", AutoStart.GetCommand(name));
            }
            finally
            {
                Assert.IsTrue(AutoStart.SetEnabled(false, null, name));
            }
            Assert.IsFalse(AutoStart.IsEnabled(name));
        }

        [Test]
        public void SingleInstanceMutex()
        {
            var name = "ShimejiTest_" + Guid.NewGuid().ToString("N");
            using (var a = new SingleInstance(name))
            {
                Assert.IsTrue(a.IsFirst);
                // 같은 프로세스여도 다른 스레드에서 두 번째 인스턴스는 실패해야 한다
                bool secondFirst = true;
                var th = new System.Threading.Thread(() => { using (var b = new SingleInstance(name)) secondFirst = b.IsFirst; });
                th.Start();
                th.Join();
                Assert.IsFalse(secondFirst);
            }
        }

        [Test]
        public void RollingLogKeepsFiveFiles()
        {
            var dir = Path.Combine(Path.GetTempPath(), "shimeji_log_" + Guid.NewGuid().ToString("N"));
            try
            {
                for (int i = 0; i < 8; i++)
                {
                    using (var s = new RollingFileLogSink(dir, 5)) s.Write(LogLevel.Info, "line " + i);
                    System.Threading.Thread.Sleep(5);
                }
                Assert.AreEqual(5, Directory.GetFiles(dir, "shimeji_*.log").Length);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
