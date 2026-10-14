using System;
using System.IO;
using NUnit.Framework;
using Shimeji.App.UI;
using Shimeji.Characters;
using Shimeji.Core;
using Shimeji.EditorTools;
using UnityEngine;

namespace Shimeji.Tests
{
    /// <summary>관절 설정 화면을 오프스크린으로 렌더해 레이아웃을 확인한다 (화면에 창을 띄우지 않음).</summary>
    public class RigWizardPreviewTests
    {
        [Test]
        public void RendersWizardAndCompletesWithJoints()
        {
            var dir = Path.Combine(Path.GetTempPath(), "shimeji_wizard_" + Guid.NewGuid().ToString("N"));
            var img = Path.Combine(dir, "image.png");
            ShimejiEditorTools.RenderSpriteSample(img);
            var tex = SpriteModelBuilder.LoadTexture(img);
            var camGo = new GameObject("PreviewCam");
            var canvasGo = new GameObject("PreviewCanvas");
            var rt = new RenderTexture(1600, 1000, 24, RenderTextureFormat.ARGB32);
            try
            {
                var cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.55f, 0.6f, 0.65f, 1f); // 바탕화면 대신
                cam.targetTexture = rt;
                cam.transform.position = new Vector3(0, 0, -100);
                var canvas = canvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = cam;
                canvas.planeDistance = 10;
                var font = Font.CreateDynamicFontFromOSFont(new[] { "Malgun Gothic", "Arial" }, 15);

                var wizard = new RigSetupWizard(canvas.transform, font, cam);
                System.Collections.Generic.Dictionary<string, Vec2> result = null;
                wizard.Open(new RigSetupWizard.Request
                {
                    Name = "SampleSprite",
                    Folder = dir,
                    ImagePath = img,
                    Texture = tex,
                    Detected = SpriteModelBuilder.DetectJoints(tex, null),
                    OnDone = j => result = j,
                }, new Rect(0, 0, 1600, 1000));
                Assert.IsTrue(wizard.Active);
                Assert.AreEqual(SpriteJoints.Top, wizard.CurrentJoint);

                Canvas.ForceUpdateCanvases();
                cam.Render();
                var shot = new Texture2D(1600, 1000, TextureFormat.RGBA32, false);
                RenderTexture.active = rt;
                shot.ReadPixels(new Rect(0, 0, 1600, 1000), 0, 0);
                RenderTexture.active = null;
                var outPath = Path.Combine(Path.GetTempPath(), "shimeji_wizard_preview.png");
                File.WriteAllBytes(outPath, shot.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(shot);
                TestContext.WriteLine("preview: " + outPath);

                // 손 하나를 옮기고 완료 → 정규화 좌표로 결과가 나온다
                wizard.SetJointNormalized(SpriteJoints.HandA, new Vec2(0.1f, 0.55f));
                wizard.Finish();
                Assert.IsFalse(wizard.Active);
                Assert.IsNotNull(result);
                Assert.AreEqual(SpriteJoints.All.Length, result.Count);
                Assert.AreEqual(0.1f, result[SpriteJoints.HandA].X, 1e-3f);
                // 팔꿈치는 자동 중간점을 따라갔다
                var mid = (result[SpriteJoints.ShoulderA] + result[SpriteJoints.HandA]) * 0.5f;
                Assert.AreEqual(mid.X, result[SpriteJoints.ElbowA].X, 1e-3f);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(canvasGo);
                UnityEngine.Object.DestroyImmediate(camGo);
                UnityEngine.Object.DestroyImmediate(tex);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }
    }
}
