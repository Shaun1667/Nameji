using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shimeji.App.UI;
using Shimeji.Characters;
using Shimeji.Core;
using Shimeji.Platform.Win32;
using UnityEngine;

namespace Shimeji.App
{
    /// <summary>
    /// 앱 부트스트랩: 오버레이 창, 데스크톱 월드, 캐릭터 로딩, 입력, 트레이, 설정을 묶는다.
    /// 업데이트 순서: 월드 갱신 → 입력/히트테스트/클릭통과 → 캐릭터(두뇌→물리→애니메이션→Transform) → UI
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class ShimejiApp : MonoBehaviour
    {
        [Header("Assets")]
        public RuntimeAnimatorController BaseController;
        public AnimationClip[] LibraryClips = Array.Empty<AnimationClip>();
        public Camera MainCamera;

        [Header("Editor")]
        public bool EditorFakeWindows = true;

        const int MenuTargetId = 1_000_000;
        const float WorldRefreshInterval = 1f / 15f;

        public static ShimejiApp Instance { get; private set; }

        public string AppRoot { get; private set; }
        public string CharactersRoot { get; private set; }
        public string LogsRoot { get; private set; }
        public AppSettings Settings { get; private set; }
        public ScreenSpace Space { get; private set; }
        public DesktopWorld World { get; } = new DesktopWorld();
        public IReadOnlyList<CharacterActor> Actors => _actors;
        public bool Ready { get; private set; }
        public bool PausedAll { get; private set; }
        public bool HiddenForFullscreen { get; private set; }
        /// <summary>현재 오버레이 창 백엔드 (빌드에서만, 에디터는 null).</summary>
        public IOverlayWindow Overlay => _window;
        public LayeredPresenter Presenter => _presenter;
        public bool MenuOpen => _ui != null && _ui.Menu.IsOpen;
        public bool TrayRunning => _tray != null && _tray.IsRunning;
        public ClickThroughController ClickThrough => _clickThrough;

        string _settingsPath;
        IDesktopApi _desktop;
        IPointerApi _pointer;
        OverlayWindow _unityWindow;      // Unity 플레이어 창 (DWM 백엔드에서는 이것이 오버레이)
        LayeredOverlayWindow _layered;   // ULW 백엔드 창
        LayeredPresenter _presenter;
        IOverlayWindow _window;          // 활성 백엔드
        bool _useLayered = true;
        bool _isRealOverlay;
        long _selfHwnd;
        int _selfPid;
        SingleInstance _singleInstance;
        RollingFileLogSink _fileLog;
        UnityLogSink _unityLog;
        TrayIcon _tray;
        CharacterFolderWatcher _watcher;
        OverlayUI _ui;
        DebugSurfaceRenderer _debug;
        TestHooks _testHooks;
        CharacterAssets _assets;

        readonly List<CharacterActor> _actors = new List<CharacterActor>();
        readonly Dictionary<int, SpeechBubble> _bubbles = new Dictionary<int, SpeechBubble>();
        readonly HashSet<string> _loading = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, List<BehaviorDef>> _profiles = new Dictionary<string, List<BehaviorDef>>(StringComparer.OrdinalIgnoreCase);
        readonly ClickThroughController _clickThrough = new ClickThroughController();
        readonly PointerTracker _tracker = new PointerTracker();
        readonly List<PointerEvent> _events = new List<PointerEvent>();
        readonly List<string> _knownFolders = new List<string>();

        float _worldTimer, _fullscreenTimer, _topmostTimer, _saveTimer, _trayMenuTimer;
        Vec2 _lastCursor;
        bool _leftWasDown;
        Texture2D _alphaProbe;
        float _cursorAlpha;
        int _cloneCounter;
        bool _quitting;
        RectPx? _testRect;
        bool _diagSkipOverlay, _diagNoResize;

        // ======================================================================
        // 시작
        // ======================================================================

        void Awake()
        {
            Instance = this;
            AppRoot = Path.GetDirectoryName(Application.dataPath) ?? ".";
            CharactersRoot = Path.Combine(AppRoot, "Characters");
            LogsRoot = Path.Combine(AppRoot, "Logs");

            _unityLog = new UnityLogSink();
            Log.AddSink(_unityLog);
            try
            {
                _fileLog = new RollingFileLogSink(LogsRoot, 5);
                Log.AddSink(_fileLog);
            }
            catch (Exception e) { Debug.LogWarning("file log unavailable: " + e.Message); }
            Log.UserVisibleError += OnUserVisibleError;
            Log.Info($"Shimeji start. Unity {Application.unityVersion}, root={AppRoot}");

#if !UNITY_EDITOR
            _singleInstance = new SingleInstance("ShimejiDesktopMascot");
            if (!_singleInstance.IsFirst)
            {
                Log.Warn("another instance is running, quitting");
                _quitting = true;
                Application.Quit();
                return;
            }
            Log.Info(OverlayWindow.EnsurePerMonitorDpiAwareness());
#endif
            _settingsPath = Path.Combine(Application.persistentDataPath, "settings.json");
            Settings = AppSettings.Load(_settingsPath);

            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = Settings.TargetFrameRate;

            if (MainCamera == null) MainCamera = Camera.main;
            ConfigureCamera();

            _assets = new CharacterAssets
            {
                BaseController = BaseController,
                LibraryClips = LibraryClips,
                PixelsPerUnit = ScreenSpace.DefaultPixelsPerUnit,
            };

#if UNITY_EDITOR
            var fake = new EditorFallbackDesktop { FakeWindows = EditorFakeWindows };
            _desktop = fake;
            _pointer = fake;
            _isRealOverlay = false;
#else
            _desktop = new Win32DesktopApi();
            _pointer = new Win32PointerApi();
            _unityWindow = new OverlayWindow();
            _isRealOverlay = true;
            _selfPid = (int)OverlayWindow.CurrentProcessId;
            ParseOverlayArgs();
#endif
            _testHooks = TestHooks.CreateIfRequested(this);
        }

        /// <summary>
        /// 실행 인자: -overlay-backend layered|dwm (기본 layered),
        /// 진단용: -overlay-mode N (DWM 백엔드 OverlayMode), -overlay-rect x,y,w,h (오버레이를 일부 영역으로 제한).
        /// </summary>
        void ParseOverlayArgs()
        {
            var args = Environment.GetCommandLineArgs();
            foreach (var a in args)
            {
                if (a == "-overlay-nohide") _unityWindow.HideWhileRestyling = false;
                if (a == "-overlay-keepstyle") _unityWindow.ChangeStyle = false;
                if (a == "-overlay-skip") _diagSkipOverlay = true;
                if (a == "-overlay-noresize") _diagNoResize = true;
            }
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-overlay-mode" && int.TryParse(args[i + 1], out int mode))
                    _unityWindow.Mode = (OverlayMode)mode;
                if (args[i] == "-overlay-backend")
                    _useLayered = !string.Equals(args[i + 1], "dwm", StringComparison.OrdinalIgnoreCase);
                if (args[i] == "-overlay-rect")
                {
                    var p = args[i + 1].Split(',');
                    if (p.Length == 4)
                        _testRect = RectPx.FromXYWH(float.Parse(p[0]), float.Parse(p[1]), float.Parse(p[2]), float.Parse(p[3]));
                }
            }
        }

        void ConfigureCamera()
        {
            var cam = MainCamera;
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
#if UNITY_EDITOR
            cam.backgroundColor = new Color(0.17f, 0.19f, 0.23f, 1f); // 에디터: 불투명 배경 폴백
#else
            cam.backgroundColor = new Color(0, 0, 0, 0);
#endif
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 2000f;
            cam.transform.SetPositionAndRotation(new Vector3(0, 0, -1000f), Quaternion.identity);
            _debug = cam.GetComponent<DebugSurfaceRenderer>() ?? cam.gameObject.AddComponent<DebugSurfaceRenderer>();

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.6f);
        }

        IEnumerator Start()
        {
            if (_quitting) yield break;
            _ui = new OverlayUI(MainCamera);
            LoadProfiles();

            // 플레이어 창이 완전히 만들어질 때까지 대기
            yield return null;
            yield return null;

            World.Refresh(_desktop, 0, _selfPid);
            var bounds = OverlayBounds();
            Space = new ScreenSpace(bounds);

            if (_isRealOverlay && _useLayered)
            {
                // ULW 백엔드: Unity 창은 숨기고, 카메라 → RenderTexture → 레이어드 창으로 올린다.
                Screen.SetResolution(64, 64, FullScreenMode.Windowed);
                yield return null;
                yield return null;
                _unityWindow.HideUnityWindow(bounds);
                _layered = new LayeredOverlayWindow();
                if (_layered.Create(bounds, Settings.AlwaysOnTop))
                {
                    _window = _layered;
                    _presenter = new LayeredPresenter(MainCamera, _layered);
                }
                else
                {
                    Log.UserError("레이어드 오버레이 창을 만들지 못해 DWM 방식으로 전환합니다.");
                    _layered.Dispose();
                    _layered = null;
                    _useLayered = false;
                }
            }
            if (_isRealOverlay && !_useLayered)
            {
                if (!_diagNoResize) Screen.SetResolution((int)bounds.Width, (int)bounds.Height, FullScreenMode.Windowed);
                yield return null;
                yield return null;
                if (_diagSkipOverlay) Log.Warn("diagnostic: overlay styling skipped");
                else if (_unityWindow.MakeOverlay(bounds, Settings.AlwaysOnTop)) _window = _unityWindow;
                else Log.UserError("투명 오버레이 창을 만들지 못했습니다. 일반 창으로 동작합니다.");
                yield return null;
                Log.Info($"Screen after overlay: {Screen.width}x{Screen.height} (expected {bounds.Width}x{bounds.Height})");
            }
            if (_window != null) _selfHwnd = _window.Hwnd.ToInt64();
            Log.Info($"overlay backend: {(_presenter != null ? "UpdateLayeredWindow" : _window != null ? "DWM" : "none")}");
            UpdateCamera();

            _debug.World = World;
            _debug.Space = Space;
            _debug.ShowSurfaces = Settings.ShowDebugSurfaces;
            _debug.ShowFakeWindows = !_isRealOverlay;

            _watcher = new CharacterFolderWatcher(CharactersRoot);
            _watcher.Start();
            LoadBuiltinPrefabs();

#if !UNITY_EDITOR
            StartTray();
#endif
            Ready = true;
            Log.Info("Ready");
        }

        RectPx OverlayBounds()
        {
            if (_testRect.HasValue) return _testRect.Value;
            var b = World.VirtualBounds;
            if (b.IsEmpty) b = new RectPx(0, 0, Screen.width, Screen.height);
            return _isRealOverlay ? OverlayWindow.OverlayRectFor(b) : b;
        }

        void UpdateCamera()
        {
            if (Space == null) return;
            MainCamera.orthographicSize = Space.OrthographicSize;
        }

        void LoadProfiles()
        {
            _profiles.Clear();
            var dir = Path.Combine(Application.streamingAssetsPath, "Behaviors");
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.GetFiles(dir, "*.json"))
                    {
                        var errors = new List<string>();
                        var defs = BehaviorProfile.Parse(File.ReadAllText(f), errors);
                        foreach (var e in errors) Log.Warn($"{Path.GetFileName(f)}: {e}");
                        if (defs.Count > 0) _profiles[Path.GetFileNameWithoutExtension(f)] = defs;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("behavior profiles load failed: " + e.Message);
            }
            if (!_profiles.ContainsKey("default"))
            {
                Log.UserError("기본 행동 프로필(StreamingAssets/Behaviors/default.json)을 찾지 못해 최소 행동 세트를 사용합니다.");
                _profiles["default"] = BehaviorProfile.Minimal();
            }
            Log.Info("behavior profiles: " + string.Join(", ", _profiles.Keys));
        }

        // ======================================================================
        // 매 프레임
        // ======================================================================

        void Update()
        {
            if (!Ready || _quitting) return;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);

            RefreshWorldIfDue(dt);
            CheckFullscreen(dt);
            HandleWatcher();
            HandleInput();
            StepCharacters(dt);
            UpdateUI();
            HandleTray(dt);
            PeriodicTasks(dt);
            _testHooks?.Update(dt);
            UpdateFrameRate();
        }

        void RefreshWorldIfDue(float dt)
        {
            _worldTimer += dt;
            if (_worldTimer < WorldRefreshInterval) return;
            _worldTimer = 0;
            if (!World.Refresh(_desktop, _selfHwnd, _selfPid)) return;

            var bounds = OverlayBounds();
            if (!bounds.Equals(Space.Overlay))
            {
                Log.Info($"display layout changed: {Space.Overlay} → {bounds}");
                Space.SetOverlay(bounds);
                if (_isRealOverlay && _window != null)
                {
                    if (_presenter == null) Screen.SetResolution((int)bounds.Width, (int)bounds.Height, FullScreenMode.Windowed);
                    _window.SetBounds(bounds);
                    _presenter?.Resize();
                }
                UpdateCamera();
            }
        }

        void CheckFullscreen(float dt)
        {
            if (!_isRealOverlay || _window == null) return;
            _fullscreenTimer += dt;
            if (_fullscreenTimer < 1f) return;
            _fullscreenTimer = 0;
            bool fs = Settings.HideOnFullscreen && OverlayWindow.IsFullscreenAppActive(_window.Hwnd);
            if (fs == HiddenForFullscreen) return;
            HiddenForFullscreen = fs;
            _window.SetVisible(!fs);
            MainCamera.enabled = !fs;
            Log.Info(fs ? "fullscreen app detected: hiding overlay" : "fullscreen app gone: showing overlay");
        }

        void HandleInput()
        {
            var cursor = _pointer.GetCursorPosition();
            bool left = _pointer.IsLeftButtonDown();
            bool right = _pointer.IsRightButtonDown();
            if ((cursor - _lastCursor).Length > 0.5f || left || right)
                foreach (var a in _actors) a.Brain.NotifyUserActivity();
            _lastCursor = cursor;

            if (HiddenForFullscreen)
            {
                _window?.SetClickThrough(true);
                return;
            }

            var overlayPt = Space.DesktopToOverlayScreen(cursor).ToVector2();
            bool overMenu = _ui.Menu.Contains(overlayPt);
            int hit = overMenu ? MenuTargetId : HitTestCharacters(cursor);

            bool capture = _clickThrough.Update(hit >= 0, left || right);
            _window?.SetClickThrough(!capture);

            // 메뉴 바깥을 누르면 메뉴 닫기
            if (left && !_leftWasDown && _ui.Menu.IsOpen && !overMenu) _ui.Menu.Close();
            _leftWasDown = left;

            _events.Clear();
            _tracker.Update(Time.unscaledTimeAsDouble, cursor, left, right, hit, _events);
            foreach (var e in _events) Dispatch(e, overlayPt);
            _ui.Menu.UpdateHover(overlayPt);
        }

        public int HitTestCharacters(Vec2 cursor)
        {
            if (_actors.Count == 0) return -1;
            if (Settings.HitTest == HitTestMode.Alpha && _isRealOverlay)
            {
                float alpha = _presenter != null
                    ? _presenter.AlphaAt((int)(cursor.X - Space.Overlay.Left), (int)(cursor.Y - Space.Overlay.Top))
                    : _cursorAlpha;
                if (alpha < 0.05f) return -1;
                CharacterActor best = null;
                foreach (var a in _actors)
                    if (a.ScreenRect.Expanded(10).Contains(cursor) && (best == null || a.Depth < best.Depth)) best = a;
                return best != null ? best.RuntimeId : -1;
            }

            Physics.SyncTransforms();
            var w = Space.DesktopToWorld(cursor);
            var ray = new Ray(new Vector3(w.X, w.Y, -900f), Vector3.forward);
            var hits = Physics.RaycastAll(ray, 2000f, 1 << UnityGlue.CharacterLayer, QueryTriggerInteraction.Collide);
            float bestDist = float.MaxValue;
            int id = -1;
            foreach (var h in hits)
            {
                var a = h.collider.GetComponentInParent<CharacterActor>();
                if (a == null || h.distance >= bestDist) continue;
                bestDist = h.distance;
                id = a.RuntimeId;
            }
            return id;
        }

        void Dispatch(PointerEvent e, Vector2 overlayPt)
        {
            if (e.TargetId == MenuTargetId)
            {
                if (e.Type == PointerEventType.Click || e.Type == PointerEventType.DoubleClick) _ui.Menu.Click(overlayPt);
                return;
            }
            var actor = FindActor(e.TargetId);
            if (actor == null) { _tracker.Cancel(); return; }
            switch (e.Type)
            {
                case PointerEventType.DragStart:
                    _ui.Menu.Close();
                    actor.BeginDrag(e.Position);
                    break;
                case PointerEventType.DragMove:
                    actor.Drag(e.Position);
                    break;
                case PointerEventType.DragEnd:
                    actor.EndDrag(e.Velocity);
                    break;
                case PointerEventType.Click:
                    actor.Brain.OnClick();
                    ShowBubble(actor, RandomReaction(), 1.4f);
                    break;
                case PointerEventType.DoubleClick:
                    actor.Brain.OnDoubleClick();
                    ShowBubble(actor, "♪", 2f);
                    break;
                case PointerEventType.RightClick:
                    OpenMenu(actor, overlayPt);
                    break;
            }
        }

        static readonly string[] Reactions = { "!", "?", "♪", "헤헤", "앗!", "응?" };
        string RandomReaction() => Reactions[UnityEngine.Random.Range(0, Reactions.Length)];

        void StepCharacters(float dt)
        {
            Vector3? look = null;
            if (Settings.LookAtCursor)
            {
                var cw = Space.DesktopToWorld(_lastCursor);
                look = new Vector3(cw.X, cw.Y, -3f);
            }
            _debug.CharacterRects.Clear();
            foreach (var a in _actors.ToArray())
            {
                a.Brain.Paused = PausedAll || HiddenForFullscreen;
                Vector3? lt = null;
                if (look.HasValue && (a.Body.Position - _lastCursor).Length < 700f) lt = look.Value + Vector3.forward * a.Depth;
                try
                {
                    a.Step(dt, World, Space, lt);
                }
                catch (Exception ex)
                {
                    Log.Error($"[{a.Id}] step failed: {ex}");
                }
                if (Settings.ShowDebugSurfaces) _debug.CharacterRects.Add(a.ScreenRect);
            }
        }

        void UpdateUI()
        {
            float now = Time.unscaledTime;
            foreach (var a in _actors)
            {
                if (!_bubbles.TryGetValue(a.RuntimeId, out var b)) continue;
                var head = new Vec2(a.Body.Position.X, a.Body.Position.Y - a.Body.Height);
                if (a.Brain.AnimationKey == AnimKeys.Sleep && !b.Visible) b.Show("Zzz", 3f, now);
                b.Update(Space.DesktopToOverlayScreen(head).ToVector2(), now);
            }
        }

        void ShowBubble(CharacterActor a, string text, float seconds)
        {
            if (!_bubbles.TryGetValue(a.RuntimeId, out var b))
            {
                b = _ui.CreateBubble();
                _bubbles[a.RuntimeId] = b;
            }
            b.Show(text, seconds, Time.unscaledTime);
        }

        void PeriodicTasks(float dt)
        {
            _topmostTimer += dt;
            if (_topmostTimer > 2f)
            {
                _topmostTimer = 0;
                if (_window != null && Settings.AlwaysOnTop && !HiddenForFullscreen) _window.SetTopmost(true);
            }
            _saveTimer += dt;
            if (_saveTimer > 30f)
            {
                _saveTimer = 0;
                SaveSettings();
            }
        }

        void UpdateFrameRate()
        {
            int target = Settings.TargetFrameRate;
            if (HiddenForFullscreen) target = 5;
            else if (_actors.Count == 0 || PausedAll) target = 10;
            else if (_actors.All(a => a.Brain.AnimationKey == AnimKeys.Sleep) && !_clickThrough.Capturing) target = 15;
            if (Application.targetFrameRate != target) Application.targetFrameRate = target;
        }

        // 알파 히트테스트: 프레임 끝에 커서 아래 픽셀 1개만 읽는다.
        void LateUpdate()
        {
            if (!Ready) return;
            UpdatePresenterRegions();
            if (_presenter == null && Settings.HitTest == HitTestMode.Alpha && _isRealOverlay && !HiddenForFullscreen)
                StartCoroutine(SampleAlpha());
        }

        readonly List<RectInt> _regions = new List<RectInt>();

        /// <summary>ULW 백엔드가 읽어 올릴 영역: 캐릭터 주변 + 말풍선 + 메뉴 (창 좌표, 위가 0).</summary>
        void UpdatePresenterRegions()
        {
            if (_presenter == null) return;
            _regions.Clear();
            var ov = Space.Overlay;
            foreach (var a in _actors)
            {
                float h = a.Body.Height;
                float half = h * 0.85f + 8f;
                float cx = a.Body.Position.X - ov.Left, cy = a.Body.Position.Y - h * 0.5f - ov.Top;
                _regions.Add(new RectInt((int)(cx - half), (int)(cy - half), (int)(half * 2), (int)(half * 2)));
            }
            int H = (int)ov.Height;
            foreach (var b in _bubbles.Values)
            {
                if (!b.Visible) continue;
                var r = b.ScreenRect;
                _regions.Add(new RectInt((int)r.xMin - 4, H - (int)r.yMax - 4, (int)r.width + 8, (int)r.height + 8));
            }
            if (_ui.Menu.IsOpen)
            {
                var m = _ui.Menu.ScreenRectOverlay;
                _regions.Add(new RectInt((int)m.Left - 4, H - (int)m.Bottom - 4, (int)m.Width + 8, (int)m.Height + 8));
            }
            _presenter.SetRegions(_regions, Settings.ShowDebugSurfaces);
        }

        IEnumerator SampleAlpha()
        {
            yield return new WaitForEndOfFrame();
            if (_alphaProbe == null) _alphaProbe = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            var p = Space.DesktopToOverlayScreen(_lastCursor);
            int x = Mathf.Clamp((int)p.X, 0, Screen.width - 1), y = Mathf.Clamp((int)p.Y, 0, Screen.height - 1);
            _alphaProbe.ReadPixels(new Rect(x, y, 1, 1), 0, 0, false);
            _cursorAlpha = _alphaProbe.GetPixel(0, 0).a;
        }

        // ======================================================================
        // 캐릭터 로딩/관리
        // ======================================================================

        void HandleWatcher()
        {
            if (_watcher == null) return;
            var changes = _watcher.Poll(Time.unscaledTimeAsDouble, Settings.DisabledCharacters);
            foreach (var c in changes)
            {
                Log.Info("characters folder change: " + c);
                switch (c.Kind)
                {
                    case FolderChangeKind.Added:
                        _ = LoadCharacterAsync(c.Name, c.Path);
                        break;
                    case FolderChangeKind.Changed:
                        RemoveCharactersOfFolder(c.Name);
                        _ = LoadCharacterAsync(c.Name, c.Path);
                        break;
                    case FolderChangeKind.Removed:
                        RemoveCharactersOfFolder(c.Name);
                        break;
                }
            }
            RefreshKnownFolders();
        }

        void RefreshKnownFolders()
        {
            if (Time.frameCount % 60 != 0) return;
            _knownFolders.Clear();
            try
            {
                if (Directory.Exists(CharactersRoot))
                    foreach (var d in Directory.GetDirectories(CharactersRoot))
                        if (ModelImporterRegistry.FindModelFile(d) != null) _knownFolders.Add(Path.GetFileName(d));
            }
            catch (Exception) { /* 다음 기회 */ }
        }

        public async Task<CharacterActor> LoadCharacterAsync(string name, string folder, string instanceId = null, Vec2? at = null)
        {
            var id = instanceId ?? name;
            if (!_loading.Add(id)) return null;
            try
            {
                var actor = await CharacterFactory.CreateFromFolderAsync(folder, _assets);
                if (this == null || _quitting) { actor.DestroyCharacter(); return null; }
                actor.gameObject.name = "Character:" + id;
                Register(actor, id, name, at);
                return actor;
            }
            catch (Exception e)
            {
                Log.UserError($"캐릭터 '{name}' 로드 실패: {e.Message}");
                Log.Error(e.ToString());
                return null;
            }
            finally
            {
                _loading.Remove(id);
            }
        }

        void LoadBuiltinPrefabs()
        {
            // 에디터 FBX 임포트 경로로 만든 프리팹 (Resources/BuiltinCharacters)
            var prefabs = Resources.LoadAll<GameObject>("BuiltinCharacters");
            foreach (var p in prefabs)
            {
                if (Settings.DisabledCharacters.Contains(p.name)) continue;
                var json = Resources.Load<TextAsset>("BuiltinCharacters/" + p.name + ".character");
                try
                {
                    var actor = CharacterFactory.CreateFromPrefab(p, json != null ? json.text : null, _assets);
                    Register(actor, p.name, p.name, null);
                }
                catch (Exception e)
                {
                    Log.UserError($"내장 캐릭터 '{p.name}' 로드 실패: {e.Message}");
                }
            }
        }

        void Register(CharacterActor actor, string instanceId, string folderName, Vec2? at)
        {
            actor.InstanceKey = instanceId;
            actor.FolderName = folderName;
            var defs = _profiles.TryGetValue(actor.Config.BehaviorProfile, out var d) ? d : _profiles["default"];
            if (!_profiles.ContainsKey(actor.Config.BehaviorProfile))
                Log.Warn($"[{instanceId}] behavior profile '{actor.Config.BehaviorProfile}' not found, using default");
            var rng = new System.Random(unchecked(Environment.TickCount * 31 + instanceId.GetHashCode()));
            var selector = new BehaviorSelector(defs, rng) { Activity = AppSettings.ActivityMultiplier(Settings.Activity) };
            actor.AttachBrain(selector);
            actor.Depth = NextDepth();
            if (!Mathf.Approximately(Settings.GlobalScale, 1f)) actor.SetUserScale(Settings.GlobalScale);
            actor.Anim.LookAtEnabled = actor.Config.LookAtCursor && Settings.LookAtCursor;

            Vec2 spawn;
            if (at.HasValue) spawn = at.Value;
            else if (Settings.RestorePositions && Settings.Positions.TryGetValue(instanceId, out var saved) && IsOnScreen(saved)) spawn = saved;
            else spawn = RandomSpawn(actor);
            actor.Body.PlaceAt(spawn);
            _actors.Add(actor);
            Log.Info($"[{instanceId}] spawned at {spawn}");
        }

        float NextDepth()
        {
            // 서로 겹쳐도 Z 충돌이 없도록 캐릭터마다 깊이를 다르게
            float d = 0;
            while (_actors.Any(a => Mathf.Approximately(a.Depth, d))) d -= 4f;
            return d;
        }

        bool IsOnScreen(Vec2 p)
        {
            foreach (var m in World.Monitors) if (m.WorkArea.ContainsInclusive(p)) return true;
            return false;
        }

        Vec2 RandomSpawn(CharacterActor a)
        {
            var m = DesktopWorld.PrimaryOrFirst(World.Monitors);
            var w = m?.WorkArea ?? new RectPx(0, 0, Screen.width, Screen.height);
            float x = UnityEngine.Random.Range(w.Left + a.Body.Width, w.Right - a.Body.Width);
            return new Vec2(x, w.Top + a.Body.Height + 10);
        }

        public CharacterActor FindActor(int runtimeId)
        {
            foreach (var a in _actors) if (a.RuntimeId == runtimeId) return a;
            return null;
        }

        public CharacterActor FindActor(string instanceKey)
        {
            foreach (var a in _actors) if (string.Equals(a.InstanceKey, instanceKey, StringComparison.OrdinalIgnoreCase)) return a;
            return null;
        }

        public void RemoveActor(CharacterActor a)
        {
            if (a == null) return;
            if (_ui.Menu.TargetRuntimeId == a.RuntimeId) _ui.Menu.Close();
            if (_tracker.DragTarget == a.RuntimeId) _tracker.Cancel();
            if (_bubbles.TryGetValue(a.RuntimeId, out var b)) { b.Destroy(); _bubbles.Remove(a.RuntimeId); }
            _actors.Remove(a);
            a.DestroyCharacter();
        }

        void RemoveCharactersOfFolder(string folder)
        {
            foreach (var a in _actors.Where(x => string.Equals(x.FolderName, folder, StringComparison.OrdinalIgnoreCase)).ToList())
                RemoveActor(a);
        }

        // ======================================================================
        // 컨텍스트 메뉴
        // ======================================================================

        void OpenMenu(CharacterActor a, Vector2 overlayPt)
        {
            var items = new List<ContextMenuView.Item>
            {
                new ContextMenuView.Item("쓰다듬기", () => { a.Brain.OnClick(); ShowBubble(a, "헤헤", 1.5f); }),
                new ContextMenuView.Item("앉기", () => Force(a, "Sit")),
                new ContextMenuView.Item("자기", () => Force(a, "Sleep")),
                new ContextMenuView.Item("손 흔들기", () => Force(a, "Wave")),
                new ContextMenuView.Item("춤추기", () => a.Brain.OnDoubleClick()),
                new ContextMenuView.Item("크게", () => a.SetUserScale(a.UserScale * 1.25f)),
                new ContextMenuView.Item("작게", () => a.SetUserScale(a.UserScale / 1.25f)),
                new ContextMenuView.Item("복제 (소환)", () => Clone(a)),
                new ContextMenuView.Item("이 캐릭터 제거", () => RemoveFromMenu(a)),
                new ContextMenuView.Item(PausedAll ? "전체 재개" : "전체 일시정지", () => SetPaused(!PausedAll)),
                new ContextMenuView.Item("닫기", null),
            };
            _ui.Menu.Open(a.RuntimeId, overlayPt, items);
        }

        void Force(CharacterActor a, string behavior)
        {
            if (!a.Brain.ForceBehavior(behavior)) ShowBubble(a, "지금은 못 해요", 1.5f);
        }

        public void Clone(CharacterActor a)
        {
            var folder = Path.Combine(CharactersRoot, a.FolderName);
            if (!Directory.Exists(folder)) { ShowBubble(a, "복제할 수 없어요", 1.5f); return; }
            _cloneCounter++;
            var pos = new Vec2(a.Body.Position.X + a.Body.Width, a.Body.Position.Y - a.Body.Height);
            _ = LoadCharacterAsync(a.FolderName, folder, $"{a.FolderName}#{_cloneCounter}", pos);
        }

        void RemoveFromMenu(CharacterActor a)
        {
            // 원본(폴더 이름과 같은 인스턴스)을 지우면 다음 실행에도 나오지 않도록 비활성 목록에 넣는다.
            if (a.InstanceKey == a.FolderName && !Settings.DisabledCharacters.Contains(a.FolderName))
            {
                Settings.DisabledCharacters.Add(a.FolderName);
                SaveSettings();
            }
            RemoveActor(a);
        }

        public void SetPaused(bool paused)
        {
            PausedAll = paused;
            Log.Info(paused ? "paused" : "resumed");
        }

        // ======================================================================
        // 트레이
        // ======================================================================

        enum Cmd
        {
            CharacterBase = 1000,
            Scale50 = 2001, Scale75, Scale100, Scale150, Scale200,
            ActivitySlow = 2101, ActivityNormal, ActivityActive,
            AlwaysOnTop = 2201, Pause, DebugSurfaces, OpenCharacters, OpenLogs, AutoStart, AlphaHitTest, LookAt, Reload, Fps60,
            Quit = 2299,
        }

        void StartTray()
        {
            try
            {
                var exe = Path.Combine(AppRoot, Application.productName + ".exe");
                _tray = new TrayIcon("Shimeji 데스크톱 마스코트", File.Exists(exe) ? exe : null);
                _tray.Start();
                RebuildTrayMenu();
            }
            catch (Exception e)
            {
                Log.Error("tray start failed: " + e.Message);
            }
        }

        void HandleTray(float dt)
        {
            if (_tray == null) return;
            _trayMenuTimer += dt;
            if (_trayMenuTimer > 1f) { _trayMenuTimer = 0; RebuildTrayMenu(); }
            while (_tray.TryDequeueCommand(out int id)) ExecuteTrayCommand(id);
        }

        void RebuildTrayMenu()
        {
            if (_tray == null) return;
            var chars = new List<TrayMenuItem>();
            for (int i = 0; i < _knownFolders.Count && i < 900; i++)
            {
                var n = _knownFolders[i];
                chars.Add(TrayMenuItem.Item((int)Cmd.CharacterBase + i, n, !Settings.DisabledCharacters.Contains(n)));
            }
            if (chars.Count == 0) chars.Add(TrayMenuItem.Item(0, "(Characters 폴더가 비어 있음)", false, false));
            var s = Settings.GlobalScale;
            var items = new List<TrayMenuItem>
            {
                TrayMenuItem.Sub("캐릭터", chars),
                TrayMenuItem.Sub("전체 크기", new List<TrayMenuItem>
                {
                    TrayMenuItem.Item((int)Cmd.Scale50, "50%", Mathf.Approximately(s, 0.5f)),
                    TrayMenuItem.Item((int)Cmd.Scale75, "75%", Mathf.Approximately(s, 0.75f)),
                    TrayMenuItem.Item((int)Cmd.Scale100, "100%", Mathf.Approximately(s, 1f)),
                    TrayMenuItem.Item((int)Cmd.Scale150, "150%", Mathf.Approximately(s, 1.5f)),
                    TrayMenuItem.Item((int)Cmd.Scale200, "200%", Mathf.Approximately(s, 2f)),
                }),
                TrayMenuItem.Sub("활동량", new List<TrayMenuItem>
                {
                    TrayMenuItem.Item((int)Cmd.ActivitySlow, "느림", Settings.Activity == ActivityLevel.Slow),
                    TrayMenuItem.Item((int)Cmd.ActivityNormal, "보통", Settings.Activity == ActivityLevel.Normal),
                    TrayMenuItem.Item((int)Cmd.ActivityActive, "활발", Settings.Activity == ActivityLevel.Active),
                }),
                TrayMenuItem.Item((int)Cmd.AlwaysOnTop, "항상 위", Settings.AlwaysOnTop),
                TrayMenuItem.Item((int)Cmd.Pause, "일시정지", PausedAll),
                TrayMenuItem.Item((int)Cmd.LookAt, "커서 바라보기", Settings.LookAtCursor),
                TrayMenuItem.Item((int)Cmd.AlphaHitTest, "정밀 클릭 판정 (알파)", Settings.HitTest == HitTestMode.Alpha),
                TrayMenuItem.Item((int)Cmd.Fps60, "60 FPS", Settings.TargetFrameRate == 60),
                TrayMenuItem.Item((int)Cmd.DebugSurfaces, "디버그: 표면 표시", Settings.ShowDebugSurfaces),
                TrayMenuItem.Sep(),
                TrayMenuItem.Item((int)Cmd.OpenCharacters, "캐릭터 폴더 열기"),
                TrayMenuItem.Item((int)Cmd.Reload, "캐릭터 다시 읽기"),
                TrayMenuItem.Item((int)Cmd.OpenLogs, "로그 폴더 열기"),
                TrayMenuItem.Item((int)Cmd.AutoStart, "Windows 시작 시 실행", AutoStart.IsEnabled()),
                TrayMenuItem.Sep(),
                TrayMenuItem.Item((int)Cmd.Quit, "종료"),
            };
            _tray.SetMenu(items);
        }

        public void ExecuteTrayCommand(int id)
        {
            Log.Info("tray command " + id);
            if (id >= (int)Cmd.CharacterBase && id < (int)Cmd.CharacterBase + 900)
            {
                int i = id - (int)Cmd.CharacterBase;
                if (i < _knownFolders.Count) ToggleCharacter(_knownFolders[i]);
                return;
            }
            switch ((Cmd)id)
            {
                case Cmd.Scale50: SetGlobalScale(0.5f); break;
                case Cmd.Scale75: SetGlobalScale(0.75f); break;
                case Cmd.Scale100: SetGlobalScale(1f); break;
                case Cmd.Scale150: SetGlobalScale(1.5f); break;
                case Cmd.Scale200: SetGlobalScale(2f); break;
                case Cmd.ActivitySlow: SetActivity(ActivityLevel.Slow); break;
                case Cmd.ActivityNormal: SetActivity(ActivityLevel.Normal); break;
                case Cmd.ActivityActive: SetActivity(ActivityLevel.Active); break;
                case Cmd.AlwaysOnTop:
                    Settings.AlwaysOnTop = !Settings.AlwaysOnTop;
                    _window?.SetTopmost(Settings.AlwaysOnTop);
                    break;
                case Cmd.Pause: SetPaused(!PausedAll); break;
                case Cmd.LookAt:
                    Settings.LookAtCursor = !Settings.LookAtCursor;
                    foreach (var a in _actors) a.Anim.LookAtEnabled = a.Config.LookAtCursor && Settings.LookAtCursor;
                    break;
                case Cmd.AlphaHitTest:
                    Settings.HitTest = Settings.HitTest == HitTestMode.Alpha ? HitTestMode.Collider : HitTestMode.Alpha;
                    break;
                case Cmd.Fps60:
                    Settings.TargetFrameRate = Settings.TargetFrameRate == 60 ? 30 : 60;
                    break;
                case Cmd.DebugSurfaces: SetDebugSurfaces(!Settings.ShowDebugSurfaces); break;
                case Cmd.OpenCharacters: OpenFolder(CharactersRoot); break;
                case Cmd.OpenLogs: OpenFolder(LogsRoot); break;
                case Cmd.Reload:
                    foreach (var a in _actors.ToList()) RemoveActor(a);
                    _watcher?.Dispose();
                    _watcher = new CharacterFolderWatcher(CharactersRoot);
                    _watcher.Start();
                    break;
                case Cmd.AutoStart:
                {
                    var exe = Path.Combine(AppRoot, Application.productName + ".exe");
                    bool on = !AutoStart.IsEnabled();
                    if (!AutoStart.SetEnabled(on, exe)) Log.UserError("자동 시작 설정을 바꾸지 못했습니다.");
                    break;
                }
                case Cmd.Quit:
                    Application.Quit();
                    break;
            }
            SaveSettings();
            RebuildTrayMenu();
        }

        public void SetDebugSurfaces(bool on)
        {
            Settings.ShowDebugSurfaces = on;
            if (_debug != null) _debug.ShowSurfaces = on;
        }

        void ToggleCharacter(string name)
        {
            if (Settings.DisabledCharacters.Remove(name))
            {
                _watcher?.Forget(name);
            }
            else
            {
                Settings.DisabledCharacters.Add(name);
                RemoveCharactersOfFolder(name);
                _watcher?.Forget(name);
            }
        }

        public void SetGlobalScale(float s)
        {
            Settings.GlobalScale = s;
            foreach (var a in _actors) a.SetUserScale(s);
        }

        void SetActivity(ActivityLevel level)
        {
            Settings.Activity = level;
            foreach (var a in _actors) a.Brain.Selector.Activity = AppSettings.ActivityMultiplier(level);
        }

        static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception e) { Log.Warn("open folder failed: " + e.Message); }
        }

        void OnUserVisibleError(string message)
        {
            _tray?.ShowBalloon("Shimeji", message, true);
        }

        // ======================================================================
        // 종료
        // ======================================================================

        void SaveSettings()
        {
            if (Settings == null) return;
            foreach (var a in _actors)
                if (a.InstanceKey == a.FolderName && a.Body.State != BodyState.Held)
                    Settings.Positions[a.InstanceKey] = a.Body.Position;
            Settings.Save(_settingsPath);
        }

        void OnApplicationQuit()
        {
            if (_quitting && Settings == null) return;
            _quitting = true;
            SaveSettings();
            Log.Info("quit");
            _tray?.Dispose();
            _watcher?.Dispose();
            _presenter?.Dispose();
            _layered?.Dispose();
            _testHooks?.Dispose();
            _singleInstance?.Dispose();
            Log.UserVisibleError -= OnUserVisibleError;
            if (_fileLog != null) { Log.RemoveSink(_fileLog); _fileLog.Dispose(); }
            if (_unityLog != null) Log.RemoveSink(_unityLog);
        }
    }
}
