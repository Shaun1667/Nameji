using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Shimeji.Core;
using UnityEngine;
using Random = System.Random;

namespace Shimeji.Tests
{
    public class BehaviorSelectorTests
    {
        static BehaviorDef Def(string id, float weight = 1, BodyState state = BodyState.Grounded, params string[] conditions) =>
            new BehaviorDef { Id = id, Weight = weight, RequiredState = state, Conditions = conditions.Length > 0 ? conditions : null };

        static BehaviorContext Ctx(BodyState s = BodyState.Grounded, SurfaceType? surface = SurfaceType.Floor) =>
            new BehaviorContext { State = s, Surface = surface };

        [Test]
        public void WeightedSelection_IsDeterministicWithSeed_AndProportional()
        {
            var defs = new[] { Def("A", 1), Def("B", 3) };
            var counts = new Dictionary<string, int> { ["A"] = 0, ["B"] = 0 };
            var sel = new BehaviorSelector(defs, new Random(1234));
            for (int i = 0; i < 4000; i++) counts[sel.Select(Ctx(), null).Id]++;
            Assert.AreEqual(0.25, counts["A"] / 4000.0, 0.03);

            // 같은 시드 → 같은 순서
            var s1 = new BehaviorSelector(defs, new Random(7));
            var s2 = new BehaviorSelector(defs, new Random(7));
            for (int i = 0; i < 50; i++) Assert.AreEqual(s1.Select(Ctx(), null).Id, s2.Select(Ctx(), null).Id);
        }

        [Test]
        public void ConditionsStateAndSurfaceFilter()
        {
            var edge = Def("SitOnEdge", 100, BodyState.Grounded, "NearEdge");
            edge.AllowedSurfaces = new[] { SurfaceType.WindowTop };
            var walkL = Def("WalkLeft", 100, BodyState.Grounded, "!BlockedLeft");
            var wall = Def("Climb", 100, BodyState.WallAttached);
            var idle = Def("Idle", 1);
            var sel = new BehaviorSelector(new[] { edge, walkL, wall, idle }, new Random(1));

            var ctx = Ctx(BodyState.Grounded, SurfaceType.Floor).Set("NearEdge").Set("BlockedLeft");
            for (int i = 0; i < 50; i++) Assert.AreEqual("Idle", sel.Select(ctx, null).Id);

            ctx = Ctx(BodyState.Grounded, SurfaceType.WindowTop).Set("NearEdge").Set("BlockedLeft");
            Assert.IsTrue(sel.IsEligible(edge, ctx));
            Assert.IsFalse(sel.IsEligible(walkL, ctx));
            Assert.IsFalse(sel.IsEligible(wall, ctx));
            Assert.AreEqual("Climb", sel.Select(Ctx(BodyState.WallAttached, SurfaceType.WallLeft), null).Id);
        }

        [Test]
        public void NoImmediateRepeat_NextBehaviors_Cooldown()
        {
            var a = Def("A", 1);
            var b = Def("B", 1);
            var c = Def("C", 1000);
            a.NextBehaviors = new[] { "B" };
            var sel = new BehaviorSelector(new[] { a, b, c }, new Random(3));
            // nextBehaviors 우선
            for (int i = 0; i < 20; i++) Assert.AreEqual("B", sel.Select(Ctx(), a).Id);
            // 직전과 같은 행동은 다른 후보가 있으면 제외
            for (int i = 0; i < 20; i++) Assert.AreNotEqual("C", sel.Select(Ctx(), c).Id);

            c.Cooldown = 10;
            sel.MarkUsed(c, 0);
            var ctx = Ctx(); ctx.Time = 5;
            Assert.IsFalse(sel.IsEligible(c, ctx));
            ctx.Time = 11;
            Assert.IsTrue(sel.IsEligible(c, ctx));
        }

        [Test]
        public void IdleTimeBoostsSleep_AndActivityScalesMovement()
        {
            var sleep = Def("Sleep", 0.1f);
            sleep.IdleWeightPerMinute = 1f;
            var sel = new BehaviorSelector(new[] { sleep }, new Random(1));
            var ctx = Ctx();
            float w0 = sel.EffectiveWeight(sleep, ctx);
            ctx.IdleSeconds = 600;
            Assert.Greater(sel.EffectiveWeight(sleep, ctx), w0 * 50);

            var walk = new BehaviorDef { Id = "W", Weight = 1, Motion = MotionType.Walk };
            sel.Activity = 2f;
            Assert.AreEqual(2f, sel.EffectiveWeight(walk, Ctx()), 1e-4f);
            Assert.AreEqual(0.1f / 2f, sel.EffectiveWeight(sleep, Ctx()), 1e-4f);
        }

        [Test]
        public void ZeroWeightSystemBehaviorsAreNeverRandomlySelected()
        {
            var sel = new BehaviorSelector(new[] { Def("Fall", 0, BodyState.Airborne) }, new Random(1));
            Assert.IsNull(sel.Select(Ctx(BodyState.Airborne, null), null));
        }

        [Test]
        public void DefaultProfileFileParsesWithoutErrors()
        {
            var path = Path.Combine(Application.streamingAssetsPath, "Behaviors", "default.json");
            Assert.IsTrue(File.Exists(path), path);
            var errors = new List<string>();
            var defs = BehaviorProfile.Parse(File.ReadAllText(path), errors);
            Assert.IsEmpty(errors, string.Join("\n", errors));
            var ids = defs.Select(d => d.Id).ToList();
            foreach (var sys in new[] { "Fall", "Land", "Held", "Thrown", "ClickReact", "DoubleClickReact", "Idle", "Sit", "Sleep", "Wave" })
                CollectionAssert.Contains(ids, sys);
            // 모든 상태에 랜덤 후보가 하나 이상 있어야 한다
            foreach (var st in new[] { BodyState.Grounded, BodyState.WallAttached, BodyState.CeilingAttached })
                Assert.IsTrue(defs.Any(d => d.RequiredState == st && d.Weight > 0), st.ToString());
            foreach (var d in defs) CollectionAssert.Contains(AnimKeys.All, d.AnimationKey, d.Id);
        }
    }

    public class CharacterBrainTests
    {
        static List<BehaviorDef> DefaultDefs()
        {
            var path = Path.Combine(Application.streamingAssetsPath, "Behaviors", "default.json");
            return BehaviorProfile.Parse(File.ReadAllText(path));
        }

        [Test]
        public void GrabDragThrow_FlowAndInterrupts()
        {
            var api = FakeDesktopApi.SingleMonitor();
            var world = new DesktopWorld();
            world.Refresh(api, 0, 0);
            var body = new CharacterBody();
            var brain = new CharacterBrain(body, new BehaviorSelector(DefaultDefs(), new Random(5)));
            body.PlaceAt(new Vec2(500, 300));
            for (int i = 0; i < 120; i++) brain.Tick(1 / 60f, world);
            Assert.AreEqual(BodyState.Grounded, body.State);

            brain.OnGrab();
            Assert.AreEqual(BodyState.Held, body.State);
            Assert.AreEqual("Held", brain.Current.Id);
            brain.Tick(1 / 60f, world);
            Assert.AreEqual(AnimKeys.Dragged, brain.AnimationKey);
            brain.OnDrag(new Vec2(900, 400));
            brain.Tick(1 / 60f, world);
            Assert.AreEqual(900f, body.Position.X, 0.01f);

            brain.OnRelease(new Vec2(1500, -500));
            Assert.AreEqual("Thrown", brain.Current.Id);
            Assert.IsTrue(body.ClingOnContact);
            bool sawLand = false;
            for (int i = 0; i < 600; i++)
            {
                brain.Tick(1 / 60f, world);
                if (brain.Current.Id == "Land") sawLand = true;
                if (body.State == BodyState.WallAttached) break;
            }
            Assert.IsTrue(body.State == BodyState.WallAttached || sawLand, "thrown character should land or cling");
        }

        [Test]
        public void ClickReactAndForcedBehaviors()
        {
            var api = FakeDesktopApi.SingleMonitor();
            var world = new DesktopWorld();
            world.Refresh(api, 0, 0);
            var body = new CharacterBody();
            var brain = new CharacterBrain(body, new BehaviorSelector(DefaultDefs(), new Random(9)));
            body.PlaceAt(new Vec2(800, 1000));
            for (int i = 0; i < 60; i++) brain.Tick(1 / 60f, world);
            brain.OnClick();
            Assert.AreEqual("ClickReact", brain.Current.Id);
            brain.Tick(1 / 60f, world);
            Assert.AreEqual(AnimKeys.ClickReact, brain.AnimationKey);
            Assert.IsTrue(brain.ForceBehavior("Sit"));
            brain.Tick(1 / 60f, world);
            Assert.AreEqual(AnimKeys.Sit, brain.AnimationKey);
            Assert.IsFalse(brain.ForceBehavior("HangCeiling")); // 상태가 맞지 않음
        }
    }

    /// <summary>
    /// M5 수용 기준: 20분 방치해도 화면 밖으로 사라지거나 끼지 않음.
    /// 창이 움직이고/나타나고/사라지는 가짜 데스크톱에서 20분(가상 시간)을 돌린다.
    /// </summary>
    public class SoakTests
    {
        [Test]
        public void TwentyMinutes_StaysOnScreen_NeverStuck_UsesManyBehaviors([Values(1, 2, 3)] int seed)
        {
            var api = new FakeDesktopApi();
            api.Monitors.Add(new MonitorInfo(new RectPx(0, 0, 1920, 1080), new RectPx(0, 0, 1920, 1040), true));
            api.Monitors.Add(new MonitorInfo(new RectPx(-1280, 200, 0, 1224), new RectPx(-1280, 200, 0, 1224), false));
            var w1 = api.AddWindow(1, new RectPx(200, 500, 900, 900), "editor");
            var w2 = api.AddWindow(2, new RectPx(1100, 300, 1700, 700), "browser");
            var w3 = api.AddWindow(3, new RectPx(-1000, 600, -300, 1100), "chat");
            var world = new DesktopWorld();
            var rng = new Random(seed);
            var defs = BehaviorProfile.Parse(File.ReadAllText(Path.Combine(Application.streamingAssetsPath, "Behaviors", "default.json")));
            var brains = new List<CharacterBrain>();
            for (int i = 0; i < 3; i++)
            {
                var body = new CharacterBody { Height = 120 + i * 30, Width = 50 + i * 10 };
                var brain = new CharacterBrain(body, new BehaviorSelector(defs, new Random(seed * 10 + i)) { Activity = 1.8f });
                body.PlaceAt(new Vec2(300 + i * 500, 100));
                brains.Add(brain);
            }

            const float dt = 1f / 30f;
            int steps = (int)(20 * 60 / dt);
            var used = new HashSet<string>();
            var states = new HashSet<BodyState>();
            var airborneSince = new double[brains.Count];
            double t = 0;
            for (int s = 0; s < steps; s++, t += dt)
            {
                // 창 이동/최소화/복원
                if (s % 90 == 0)
                {
                    w1.Bounds = w1.Bounds.Offset((float)(rng.NextDouble() * 60 - 30), (float)(rng.NextDouble() * 40 - 20));
                    if (w1.Bounds.Left < 0) w1.Bounds = w1.Bounds.Offset(100, 0);
                    if (w1.Bounds.Right > 1900) w1.Bounds = w1.Bounds.Offset(-100, 0);
                    if (w1.Bounds.Top < 50) w1.Bounds = w1.Bounds.Offset(0, 80);
                    if (w1.Bounds.Bottom > 1030) w1.Bounds = w1.Bounds.Offset(0, -80);
                }
                if (s % 2400 == 0) w2.Minimized = !w2.Minimized;
                if (s % 5000 == 1234) { api.Windows.Remove(w3); api.Windows.Insert(0, w3); } // Z-order 변경
                if (s % 2 == 0) world.Refresh(api, 0, 0); // 15Hz

                // 가끔 사용자가 집어 던짐
                if (s % 7000 == 3000)
                {
                    var b = brains[rng.Next(brains.Count)];
                    b.OnGrab();
                    b.OnDrag(new Vec2((float)rng.NextDouble() * 1800 + 60, 400));
                    b.OnRelease(new Vec2((float)(rng.NextDouble() * 4000 - 2000), (float)(rng.NextDouble() * -1500)));
                }

                for (int i = 0; i < brains.Count; i++)
                {
                    var br = brains[i];
                    br.Tick(dt, world);
                    var p = br.Body.Position;
                    Assert.IsFalse(float.IsNaN(p.X) || float.IsNaN(p.Y), "NaN position");
                    bool onScreen = world.Monitors.Any(m => m.Bounds.Expanded(br.Body.P.OutOfBoundsMargin + 1).Contains(new Vec2(p.X, p.Y - br.Body.Height * 0.5f)));
                    Assert.IsTrue(onScreen, $"character {i} left screen at t={t:0.0}s pos={p} state={br.Body.State}");
                    if (br.Body.State == BodyState.Airborne)
                    {
                        if (airborneSince[i] < 0) airborneSince[i] = t;
                        Assert.Less(t - airborneSince[i], br.Body.P.MaxAirTime + 1.0, $"character {i} stuck airborne");
                    }
                    else airborneSince[i] = -1;
                    used.Add(br.Current.Id);
                    states.Add(br.Body.State);
                }
            }
            // 주요 행동/상태가 실제로 일어나는지
            foreach (var id in new[] { "Idle", "WalkLeft", "WalkRight", "Fall", "Land" })
                CollectionAssert.Contains(used, id);
            Assert.IsTrue(states.Contains(BodyState.Grounded));
            Assert.IsTrue(states.Contains(BodyState.Airborne));
            Assert.IsTrue(states.Contains(BodyState.WallAttached), "should climb walls sometimes. used: " + string.Join(",", used));
            TestContext.WriteLine("behaviors used: " + string.Join(", ", used.OrderBy(x => x)));
            TestContext.WriteLine("states: " + string.Join(", ", states));
        }
    }
}
