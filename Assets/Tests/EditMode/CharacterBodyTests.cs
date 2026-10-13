using NUnit.Framework;
using Shimeji.Core;

namespace Shimeji.Tests
{
    public class CharacterBodyTests
    {
        FakeDesktopApi _api;
        DesktopWorld _world;

        [SetUp]
        public void Setup()
        {
            _api = FakeDesktopApi.SingleMonitor(); // 1920x1080, 바닥 y=1040
            _world = new DesktopWorld();
        }

        void Rebuild() => _world.Refresh(_api, 0, 0);

        CharacterBody NewBody(float x, float y)
        {
            Rebuild();
            var b = new CharacterBody { Width = 60, Height = 160 };
            b.PlaceAt(new Vec2(x, y));
            return b;
        }

        BodyEvents Run(CharacterBody b, float seconds)
        {
            var all = BodyEvents.None;
            for (float t = 0; t < seconds; t += 1f / 60f) all |= b.Update(1f / 60f, _world);
            return all;
        }

        [Test]
        public void FallsAndLandsOnFloor()
        {
            var b = NewBody(500, 300);
            var ev = Run(b, 3f);
            Assert.AreEqual(BodyState.Grounded, b.State);
            Assert.AreEqual(SurfaceType.Floor, b.Surface.Type);
            Assert.AreEqual(1040f, b.Position.Y, 0.01f);
            Assert.IsTrue((ev & BodyEvents.Landed) != 0);
        }

        [Test]
        public void LandsOnWindowTop_AndFollowsWindowMove_ThenFallsWhenMinimized()
        {
            var win = _api.AddWindow(42, new RectPx(300, 600, 900, 900));
            var b = NewBody(500, 300);
            Run(b, 2f);
            Assert.AreEqual(BodyState.Grounded, b.State);
            Assert.AreEqual(SurfaceType.WindowTop, b.Surface.Type);
            Assert.AreEqual(42, b.Surface.OwnerId);
            Assert.AreEqual(600f, b.Position.Y, 0.01f);

            // 창을 오른쪽 아래로 옮기면 같이 이동
            win.Bounds = win.Bounds.Offset(150, 100);
            Rebuild();
            Run(b, 0.1f);
            Assert.AreEqual(650f, b.Position.X, 0.5f);
            Assert.AreEqual(700f, b.Position.Y, 0.5f);
            Assert.AreEqual(BodyState.Grounded, b.State);

            // 최소화 → 표면이 사라져 낙하 → 바닥 착지
            win.Minimized = true;
            Rebuild();
            var ev = Run(b, 2f);
            Assert.IsTrue((ev & BodyEvents.StartedFalling) != 0);
            Assert.AreEqual(SurfaceType.Floor, b.Surface.Type);
            Assert.AreEqual(1040f, b.Position.Y, 0.01f);
        }

        [Test]
        public void WindowCoveredByAnotherWindow_Falls()
        {
            var back = _api.AddWindow(2, new RectPx(300, 600, 900, 900));
            var b = NewBody(500, 300);
            Run(b, 2f);
            Assert.AreEqual(2, b.Surface.OwnerId);
            // 위에 다른 창이 덮음 (Z-order 앞)
            _api.Windows.Insert(0, new WindowInfo { Hwnd = 3, Title = "front", Bounds = new RectPx(400, 500, 700, 800) });
            Rebuild();
            var ev = Run(b, 0.2f);
            Assert.IsTrue((ev & BodyEvents.StartedFalling) != 0, "가려진 표면에서 떨어져야 한다");
            Run(b, 2f);
            Assert.AreEqual(SurfaceType.Floor, b.Surface.Type);
        }

        [Test]
        public void WindowMovingOverCharacter_DoesNotPushUp()
        {
            var b = NewBody(500, 900);
            Run(b, 2f);
            Assert.AreEqual(1040f, b.Position.Y, 0.01f);
            _api.AddWindow(5, new RectPx(300, 700, 900, 1000)); // 캐릭터와 겹치는 위치에 창이 나타남
            Rebuild();
            Run(b, 1f);
            Assert.AreEqual(1040f, b.Position.Y, 0.01f);
            Assert.AreEqual(SurfaceType.Floor, b.Surface.Type);
        }

        [Test]
        public void WalkOffWindowEdge_Falls()
        {
            _api.AddWindow(7, new RectPx(300, 600, 900, 900));
            var b = NewBody(850, 300);
            Run(b, 2f);
            b.MoveSpeed = 200;
            var ev = Run(b, 1.5f);
            Assert.IsTrue((ev & BodyEvents.StartedFalling) != 0);
            Run(b, 2f);
            Assert.AreEqual(SurfaceType.Floor, b.Surface.Type);
        }

        [Test]
        public void BlockedByScreenEdgeWall()
        {
            var b = NewBody(1800, 900);
            Run(b, 2f);
            b.MoveSpeed = 300;
            var ev = Run(b, 2f);
            Assert.IsTrue((ev & BodyEvents.BlockedByWall) != 0);
            Assert.AreEqual(1920f - 30f, b.Position.X, 0.5f);
        }

        [Test]
        public void BlockedByWindowSide_ThenClimbsOntoWindowTop()
        {
            // 바닥에 붙은 창 (아래 끝이 작업표시줄)
            _api.AddWindow(9, new RectPx(800, 700, 1300, 1040));
            var b = NewBody(500, 900);
            Run(b, 2f);
            b.MoveSpeed = 200;
            var ev = Run(b, 3f);
            Assert.IsTrue((ev & BodyEvents.BlockedByWall) != 0);
            Assert.AreEqual(800f - 30f, b.Position.X, 0.5f);

            Assert.IsTrue(b.TryAttachWall(_world, 1));
            Assert.AreEqual(BodyState.WallAttached, b.State);
            b.MoveSpeed = 120; // 위로
            ev = Run(b, 6f);
            Assert.IsTrue((ev & BodyEvents.ClimbedOntoTop) != 0);
            Assert.AreEqual(BodyState.Grounded, b.State);
            Assert.AreEqual(SurfaceType.WindowTop, b.Surface.Type);
            Assert.AreEqual(700f, b.Position.Y, 0.01f);
        }

        [Test]
        public void ClimbScreenEdge_ReachesCeiling_CrawlEnd_ThenDrop()
        {
            var b = NewBody(1850, 900);
            Run(b, 2f);
            b.MoveSpeed = 200;
            Run(b, 1f);
            Assert.IsTrue(b.TryAttachWall(_world, 1));
            b.MoveSpeed = 400;
            var ev = Run(b, 5f);
            Assert.IsTrue((ev & BodyEvents.ReachedCeiling) != 0);
            Assert.AreEqual(BodyState.CeilingAttached, b.State);
            Assert.AreEqual(160f, b.Position.Y, 0.01f); // 머리가 천장(y=0)

            b.MoveSpeed = 500; // 오른쪽 끝(벽)으로 기어감 → 멈춤
            ev = Run(b, 1f);
            Assert.IsTrue((ev & BodyEvents.ReachedCeilingEnd) != 0);
            Assert.AreEqual(BodyState.CeilingAttached, b.State);

            b.Drop();
            Run(b, 3f);
            Assert.AreEqual(BodyState.Grounded, b.State);
        }

        [Test]
        public void ThrownIntoWall_Clings()
        {
            var b = NewBody(1000, 500);
            b.Grab();
            b.SetHeldPosition(new Vec2(1500, 600));
            b.ClingOnContact = true;
            b.Release(new Vec2(2500, -300));
            var ev = Run(b, 2f);
            Assert.IsTrue((ev & BodyEvents.AttachedToWall) != 0);
            Assert.AreEqual(BodyState.WallAttached, b.State);
            Assert.AreEqual(SurfaceType.WallRight, b.Surface.Type);
        }

        [Test]
        public void ThrownWithoutCling_BouncesAndLands()
        {
            var b = NewBody(1000, 500);
            b.Grab();
            b.Release(new Vec2(-2500, -800));
            var ev = Run(b, 4f);
            Assert.IsTrue((ev & BodyEvents.BouncedOffWall) != 0);
            Assert.AreEqual(BodyState.Grounded, b.State);
            Assert.GreaterOrEqual(b.Position.X, 30f);
        }

        [Test]
        public void OutOfBounds_Respawns()
        {
            var b = NewBody(500, 300);
            b.PlaceAt(new Vec2(-5000, -5000));
            var ev = Run(b, 0.1f);
            Assert.IsTrue((ev & BodyEvents.Respawned) != 0);
            Run(b, 3f);
            Assert.AreEqual(BodyState.Grounded, b.State);
            Assert.IsTrue(_world.Monitors[0].Bounds.Contains(b.Position - new Vec2(0, 1)));
        }

        [Test]
        public void TaskbarGrows_CharacterBelowFloorIsLifted()
        {
            var b = NewBody(500, 300);
            Run(b, 2f);
            Assert.AreEqual(1040f, b.Position.Y, 0.01f);
            _api.Monitors[0].WorkArea = new RectPx(0, 0, 1920, 1000); // 작업표시줄이 커짐
            Rebuild();
            Run(b, 1f);
            Assert.AreEqual(BodyState.Grounded, b.State);
            Assert.AreEqual(1000f, b.Position.Y, 0.01f);
        }

        [Test]
        public void SmallStepUpBetweenMonitorsOfDifferentWorkHeight()
        {
            _api = new FakeDesktopApi();
            _api.Monitors.Add(new MonitorInfo(new RectPx(0, 0, 1920, 1080), new RectPx(0, 0, 1920, 1080), true));
            _api.Monitors.Add(new MonitorInfo(new RectPx(1920, 0, 3840, 1080), new RectPx(1920, 0, 3840, 1048), false));
            var b = NewBody(1800, 500);
            Run(b, 2f);
            Assert.AreEqual(1080f, b.Position.Y, 0.01f);
            b.MoveSpeed = 200;
            Run(b, 2f);
            Assert.Greater(b.Position.X, 1920f);
            Assert.AreEqual(1048f, b.Position.Y, 0.01f);
        }
    }
}
