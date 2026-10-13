using System.Collections.Generic;
using NUnit.Framework;
using Shimeji.Core;

namespace Shimeji.Tests
{
    public class ScreenSpaceTests
    {
        [Test]
        public void RoundTrip_IncludesNegativeMonitorCoordinates()
        {
            // 주 모니터 왼쪽/위에 보조 모니터가 있어 가상 데스크톱이 음수에서 시작
            var space = new ScreenSpace(new RectPx(-1920, -300, 2560, 1440));
            var samples = new[] { new Vec2(-1920, -300), new Vec2(0, 0), new Vec2(2559, 1439), new Vec2(-777.5f, 123.25f), new Vec2(1000, -299) };
            foreach (var p in samples)
            {
                var w = space.DesktopToWorld(p);
                var back = space.WorldToDesktop(w);
                Assert.AreEqual(p.X, back.X, 1e-3f);
                Assert.AreEqual(p.Y, back.Y, 1e-3f);

                var s = space.DesktopToOverlayScreen(p);
                var back2 = space.OverlayScreenToDesktop(s);
                Assert.AreEqual(p.X, back2.X, 1e-3f);
                Assert.AreEqual(p.Y, back2.Y, 1e-3f);
            }
        }

        [Test]
        public void OverlayCenterIsWorldOrigin_AndYAxisFlips()
        {
            var space = new ScreenSpace(new RectPx(-1920, 0, 1920, 1080), 100f);
            var c = space.DesktopToWorld(new Vec2(0, 540));
            Assert.AreEqual(0f, c.X, 1e-5f);
            Assert.AreEqual(0f, c.Y, 1e-5f);
            var up = space.DesktopToWorld(new Vec2(0, 440)); // 화면에서 위로 100px
            Assert.AreEqual(1f, up.Y, 1e-5f);
            Assert.AreEqual(5.4f, space.OrthographicSize, 1e-5f);
            // 오버레이 스크린 좌표는 좌하단 원점
            var bl = space.DesktopToOverlayScreen(new Vec2(-1920, 1080));
            Assert.AreEqual(0f, bl.X, 1e-5f);
            Assert.AreEqual(0f, bl.Y, 1e-5f);
        }
    }

    public class IntervalsTests
    {
        [Test]
        public void SubtractAndIntersect()
        {
            var s = new List<Intervals.Span> { new Intervals.Span(0, 100) };
            s = Intervals.Subtract(s, 20, 30);
            s = Intervals.Subtract(s, 90, 200);
            Assert.AreEqual(2, s.Count);
            Assert.AreEqual(0, s[0].A); Assert.AreEqual(20, s[0].B);
            Assert.AreEqual(30, s[1].A); Assert.AreEqual(90, s[1].B);
            var clipped = Intervals.IntersectWithUnion(s, new List<Intervals.Span> { new Intervals.Span(10, 40) });
            Assert.AreEqual(2, clipped.Count);
            Assert.AreEqual(10, clipped[0].A); Assert.AreEqual(20, clipped[0].B);
            Assert.AreEqual(30, clipped[1].A); Assert.AreEqual(40, clipped[1].B);
        }
    }

    public class MiniJsonTests
    {
        [Test]
        public void ParseAndSerializeRoundTrip()
        {
            var json = "{ \"a\": 1.5, \"b\": [true, false, null, \"x\\n\\\"y\\\"\"], \"c\": { \"d\": -2e3 }, // 주석\n \"e\": \"한글\" }";
            var d = (Dictionary<string, object>)MiniJson.Parse(json);
            Assert.AreEqual(1.5, d["a"]);
            var b = (List<object>)d["b"];
            Assert.AreEqual(true, b[0]);
            Assert.IsNull(b[2]);
            Assert.AreEqual("x\n\"y\"", b[3]);
            Assert.AreEqual(-2000.0, ((Dictionary<string, object>)d["c"])["d"]);
            Assert.AreEqual("한글", d["e"]);

            var again = (Dictionary<string, object>)MiniJson.Parse(MiniJson.Serialize(d));
            Assert.AreEqual("x\n\"y\"", ((List<object>)again["b"])[3]);
            Assert.AreEqual("한글", again["e"]);
        }

        [Test]
        public void MalformedThrows()
        {
            Assert.Throws<MiniJson.JsonException>(() => MiniJson.Parse("{ \"a\": }"));
            Assert.Throws<MiniJson.JsonException>(() => MiniJson.Parse("[1, 2"));
            Assert.Throws<MiniJson.JsonException>(() => MiniJson.Parse("{} trailing"));
        }
    }

    public class CharacterConfigTests
    {
        [Test]
        public void EmptyOrMissingUsesDefaults()
        {
            var c = CharacterConfig.Parse(null, "Folder");
            Assert.AreEqual("Folder", c.DisplayName);
            Assert.AreEqual(160f, c.HeightPixels);
            Assert.AreEqual(90f, c.FacingYawDeg);
            Assert.AreEqual(RigType.Auto, c.RigType);
            Assert.AreEqual("default", c.BehaviorProfile);
            Assert.IsTrue(c.EnableSpringBones);
            Assert.AreEqual("chibi", c.ColliderPreset);
            Assert.AreEqual(0, c.BoneMapOverride.Count);
        }

        [Test]
        public void FullSpecExample()
        {
            var json = @"{
              ""displayName"": ""Chibi A"",
              ""heightPixels"": 200,
              ""facingYawDeg"": -90,
              ""rigType"": ""generic"",
              ""boneMapOverride"": { ""Hips"": ""Bip001 Pelvis"" },
              ""clipOverrides"": { ""Walk"": ""my_walk"" },
              ""behaviorProfile"": ""lazy"",
              ""enableSpringBones"": false,
              ""colliderPreset"": ""box""
            }";
            var warnings = new List<string>();
            var c = CharacterConfig.Parse(json, "F", warnings);
            Assert.IsEmpty(warnings);
            Assert.AreEqual("Chibi A", c.DisplayName);
            Assert.AreEqual(200f, c.HeightPixels);
            Assert.AreEqual(-90f, c.FacingYawDeg);
            Assert.AreEqual(RigType.Generic, c.RigType);
            Assert.AreEqual("Bip001 Pelvis", c.BoneMapOverride["hips"]); // 키 대소문자 무시
            Assert.AreEqual("my_walk", c.ClipOverrides["Walk"]);
            Assert.AreEqual("lazy", c.BehaviorProfile);
            Assert.IsFalse(c.EnableSpringBones);
            Assert.AreEqual("box", c.ColliderPreset);
        }

        [Test]
        public void InvalidValuesFallBackWithWarnings()
        {
            var warnings = new List<string>();
            var c = CharacterConfig.Parse(@"{ ""heightPixels"": 5, ""rigType"": ""robot"" }", "F", warnings);
            Assert.AreEqual(160f, c.HeightPixels);
            Assert.AreEqual(RigType.Auto, c.RigType);
            Assert.AreEqual(2, warnings.Count);

            warnings.Clear();
            var broken = CharacterConfig.Parse("{ not json", "F", warnings);
            Assert.AreEqual(160f, broken.HeightPixels);
            Assert.AreEqual(1, warnings.Count);
        }
    }

    public class ClipNameMatcherTests
    {
        [Test]
        public void MatchesKeysIgnoringCaseAndPrefixes()
        {
            var clips = new[] { "Armature|Idle", "chibi_WALK", "mixamo.com", "IdleLookAround_Loop", "Sit", "Character|SitEdge" };
            var m = ClipNameMatcher.Match(clips, AnimKeys.All);
            Assert.AreEqual("Armature|Idle", m["Idle"]);
            Assert.AreEqual("chibi_WALK", m["Walk"]);
            Assert.AreEqual("IdleLookAround_Loop", m["IdleLookAround"]);
            Assert.AreEqual("Sit", m["Sit"]);
            Assert.AreEqual("Character|SitEdge", m["SitEdge"]);
            Assert.IsFalse(m.ContainsKey("Run"));
            Assert.IsFalse(m.ContainsKey("Dance"));
        }
    }

    public class ProceduralPosesTests
    {
        [Test]
        public void AllKeysEvaluateToFiniteValues()
        {
            foreach (var key in AnimKeys.All)
            {
                Assert.IsNotNull(ProceduralPoses.GetSpec(key), "spec missing for " + key);
                for (float t = 0; t < 5f; t += 0.37f)
                {
                    var p = ProceduralPoses.Evaluate(key, t);
                    foreach (var v in new[] { p.HipsDrop, p.SpinePitch, p.HeadPitch, p.HeadYaw, p.ArmL.Spread, p.ArmR.Forward, p.LegL.Bend, p.LegR.Forward })
                        Assert.IsFalse(float.IsNaN(v) || float.IsInfinity(v), key);
                }
            }
        }
    }
}
