using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Shimeji.Core;

namespace Shimeji.Tests
{
    /// <summary>테스트용 가짜 IDesktopApi.</summary>
    public sealed class FakeDesktopApi : IDesktopApi
    {
        public List<MonitorInfo> Monitors = new List<MonitorInfo>();
        public List<WindowInfo> Windows = new List<WindowInfo>();
        public IReadOnlyList<MonitorInfo> GetMonitors() => Monitors;
        public IReadOnlyList<WindowInfo> GetWindows() => Windows;

        public static FakeDesktopApi SingleMonitor(float w = 1920, float h = 1080, float taskbar = 40)
        {
            var api = new FakeDesktopApi();
            api.Monitors.Add(new MonitorInfo(new RectPx(0, 0, w, h), new RectPx(0, 0, w, h - taskbar), true));
            return api;
        }

        public WindowInfo AddWindow(long hwnd, RectPx r, string title = "Win")
        {
            var w = new WindowInfo { Hwnd = hwnd, Bounds = r, Title = title, ClassName = "App" };
            Windows.Add(w);
            return w;
        }
    }

    public class DesktopWorldTests
    {
        static Surface Find(DesktopWorld w, SurfaceType t, long owner) => w.Surfaces.FirstOrDefault(s => s.Type == t && s.OwnerId == owner);

        [Test]
        public void SingleMonitor_HasFloorWallsCeiling()
        {
            var api = FakeDesktopApi.SingleMonitor();
            var world = new DesktopWorld();
            Assert.IsTrue(world.Refresh(api, 0, 0));
            var floor = Find(world, SurfaceType.Floor, 0);
            Assert.AreEqual(1040f, floor.Fixed); // 작업표시줄 윗면
            Assert.AreEqual(0f, floor.Min);
            Assert.AreEqual(1920f, floor.Max);
            Assert.AreEqual(0f, Find(world, SurfaceType.WallLeft, 0).Fixed);
            Assert.AreEqual(1920f, Find(world, SurfaceType.WallRight, 0).Fixed);
            Assert.AreEqual(0f, Find(world, SurfaceType.Ceiling, 0).Fixed);
            Assert.IsFalse(world.Refresh(api, 0, 0), "no change → no rebuild");
        }

        [Test]
        public void WindowFilter_ExcludesHiddenMinimizedCloakedToolSelfShell()
        {
            var api = FakeDesktopApi.SingleMonitor();
            api.AddWindow(1, new RectPx(100, 100, 500, 400), "ok");
            api.AddWindow(2, new RectPx(100, 500, 500, 800)).Minimized = true;
            api.AddWindow(3, new RectPx(600, 100, 900, 400)).Cloaked = true;
            api.AddWindow(4, new RectPx(600, 500, 900, 800)).ToolWindow = true;
            api.AddWindow(5, new RectPx(1000, 100, 1300, 400)).Visible = false;
            api.AddWindow(6, new RectPx(1000, 500, 1300, 800), "self");
            api.AddWindow(7, new RectPx(0, 0, 1920, 1080), "Program Manager").ClassName = "Progman";
            api.AddWindow(8, new RectPx(1400, 100, 1430, 110), "tiny");
            api.AddWindow(9, new RectPx(1400, 300, 1800, 600), "");
            api.AddWindow(10, new RectPx(1400, 700, 1800, 900), "overlay").Transparent = true;
            var w = api.AddWindow(11, new RectPx(1400, 300, 1800, 600), "other process window");
            w.ProcessId = 1234;

            var world = new DesktopWorld();
            world.Refresh(api, selfHwnd: 6, selfProcessId: 999);
            CollectionAssert.AreEquivalent(new long[] { 1, 11 }, world.Windows.Select(x => x.Hwnd).ToArray());
        }

        [Test]
        public void WindowTopIsClippedByHigherWindows()
        {
            var api = FakeDesktopApi.SingleMonitor();
            api.AddWindow(1, new RectPx(400, 200, 800, 600), "front");   // 위 (Z-order 먼저)
            api.AddWindow(2, new RectPx(200, 400, 1000, 900), "back");   // 아래: 윗면 y=400 중 400~800은 가려짐
            var world = new DesktopWorld();
            world.Refresh(api, 0, 0);
            var tops = world.Surfaces.Where(s => s.Type == SurfaceType.WindowTop && s.OwnerId == 2).OrderBy(s => s.Min).ToList();
            Assert.AreEqual(2, tops.Count);
            Assert.AreEqual(200f, tops[0].Min); Assert.AreEqual(400f, tops[0].Max);
            Assert.AreEqual(800f, tops[1].Min); Assert.AreEqual(1000f, tops[1].Max);
            var front = Find(world, SurfaceType.WindowTop, 1);
            Assert.AreEqual(200f, front.Fixed);
            Assert.AreEqual(400f, front.Min); Assert.AreEqual(800f, front.Max);
            // 앞 창 옆면 = 벽, 아랫면 = 천장
            Assert.AreEqual(400f, Find(world, SurfaceType.WallRight, 1).Fixed);
            Assert.AreEqual(800f, Find(world, SurfaceType.WallLeft, 1).Fixed);
            Assert.AreEqual(600f, Find(world, SurfaceType.Ceiling, 1).Fixed);
        }

        [Test]
        public void MaximizedWindowHasNoSurfaces()
        {
            var api = FakeDesktopApi.SingleMonitor();
            api.AddWindow(1, new RectPx(0, 0, 1920, 1040), "max").Maximized = true;
            var world = new DesktopWorld();
            world.Refresh(api, 0, 0);
            Assert.IsFalse(world.Surfaces.Any(s => s.OwnerId == 1));
        }

        [Test]
        public void TwoMonitors_InnerWallsRemoved_NegativeCoords()
        {
            var api = new FakeDesktopApi();
            api.Monitors.Add(new MonitorInfo(new RectPx(0, 0, 1920, 1080), new RectPx(0, 0, 1920, 1040), true));
            api.Monitors.Add(new MonitorInfo(new RectPx(-1600, 180, 0, 1080), new RectPx(-1600, 180, 0, 1080), false));
            var world = new DesktopWorld();
            world.Refresh(api, 0, 0);
            Assert.AreEqual(new RectPx(-1600, 0, 1920, 1080), world.VirtualBounds);
            // 왼쪽 모니터의 오른쪽 벽과 주 모니터의 왼쪽 벽 중 맞닿은 부분은 없어야 한다
            Assert.IsFalse(world.Surfaces.Any(s => s.Type == SurfaceType.WallLeft && s.Fixed == 0f && s.Min < 1000f && s.Max > 200f));
            Assert.IsFalse(world.Surfaces.Any(s => s.Type == SurfaceType.WallRight && s.Fixed == 0f));
            // 주 모니터 왼쪽 벽 중 보조 모니터가 없는 위쪽(0~180)은 남는다
            Assert.IsTrue(world.Surfaces.Any(s => s.Type == SurfaceType.WallLeft && s.Fixed == 0f && s.Max <= 180f));
            Assert.IsTrue(world.Surfaces.Any(s => s.Type == SurfaceType.WallLeft && s.Fixed == -1600f));
            Assert.AreEqual(2, world.Surfaces.Count(s => s.Type == SurfaceType.Floor));
        }
    }
}
