using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Shimeji.Core;

namespace Shimeji.Characters
{
    public enum FolderChangeKind { Added, Changed, Removed }

    public struct FolderChange
    {
        public FolderChangeKind Kind;
        public string Name;
        public string Path;
        public override string ToString() => $"{Kind} {Name}";
    }

    /// <summary>
    /// Characters/ 폴더 감시. FileSystemWatcher 이벤트(백그라운드 스레드)는 플래그만 세우고,
    /// 메인 스레드 Poll에서 디바운스(0.5초) + 파일 잠금 확인 후 변경 목록을 만든다.
    /// 감시 이벤트를 놓치는 경우에 대비해 주기적으로 전체 스캔도 한다.
    /// </summary>
    public sealed class CharacterFolderWatcher : IDisposable
    {
        public string Root { get; }
        public float Debounce = 0.5f;
        public float FullRescanInterval = 5f;

        FileSystemWatcher _watcher;
        long _lastEventTicks;   // Interlocked
        int _dirty;             // Interlocked
        double _lastScanTime = double.NegativeInfinity;
        readonly Dictionary<string, string> _known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _pendingLocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public CharacterFolderWatcher(string root)
        {
            Root = root;
        }

        public void Start()
        {
            try
            {
                Directory.CreateDirectory(Root);
                _watcher = new FileSystemWatcher(Root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                _watcher.Changed += OnFsEvent;
                _watcher.Created += OnFsEvent;
                _watcher.Deleted += OnFsEvent;
                _watcher.Renamed += OnFsEvent;
                _watcher.Error += (s, e) => MarkDirty();
                _watcher.EnableRaisingEvents = true;
                Log.Info("Watching characters folder: " + Root);
            }
            catch (Exception e)
            {
                Log.Warn("FileSystemWatcher unavailable, falling back to polling: " + e.Message);
            }
            MarkDirty();
        }

        void OnFsEvent(object sender, FileSystemEventArgs e) => MarkDirty();

        void MarkDirty()
        {
            Interlocked.Exchange(ref _lastEventTicks, DateTime.UtcNow.Ticks);
            Interlocked.Exchange(ref _dirty, 1);
        }

        /// <summary>현재 로드되어야 하는 상태와 비교해 변경 목록을 반환 (없으면 빈 목록).</summary>
        public List<FolderChange> Poll(double now, ICollection<string> disabled = null)
        {
            var changes = new List<FolderChange>();
            bool dirty = Interlocked.CompareExchange(ref _dirty, 0, 0) == 1;
            var sinceEvent = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastEventTicks)).TotalSeconds;
            bool periodic = now - _lastScanTime >= FullRescanInterval;
            bool retryLocked = _pendingLocked.Count > 0 && now - _lastScanTime >= Debounce;
            if (dirty && sinceEvent < Debounce) return changes; // 아직 파일이 바뀌는 중 (디바운스)
            if (!dirty && !periodic && !retryLocked) return changes;

            Interlocked.Exchange(ref _dirty, 0);
            _lastScanTime = now;
            Scan(changes, disabled);
            return changes;
        }

        /// <summary>강제 재스캔 (테스트/트레이 메뉴용).</summary>
        public List<FolderChange> ScanNow(ICollection<string> disabled = null)
        {
            var changes = new List<FolderChange>();
            Scan(changes, disabled);
            return changes;
        }

        /// <summary>로드 실패 등으로 기록을 지워 다음 스캔에서 다시 시도하게 한다.</summary>
        public void Forget(string name) => _known.Remove(name);

        void Scan(List<FolderChange> changes, ICollection<string> disabled)
        {
            var present = new Dictionary<string, (string path, string sig)>(StringComparer.OrdinalIgnoreCase);
            _pendingLocked.Clear();
            try
            {
                if (Directory.Exists(Root))
                {
                    foreach (var dir in Directory.GetDirectories(Root))
                    {
                        var name = Path.GetFileName(dir);
                        if (disabled != null && disabled.Contains(name)) continue;
                        var model = ModelImporterRegistry.FindModelFile(dir);
                        if (model == null) continue;
                        if (IsLocked(model))
                        {
                            // 복사 중: 지금 상태를 유지하고 다음 폴링에서 다시 본다.
                            _pendingLocked.Add(name);
                            if (_known.TryGetValue(name, out var oldSig)) present[name] = (dir, oldSig);
                            continue;
                        }
                        present[name] = (dir, Signature(dir, model));
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn("Characters scan failed: " + e.Message);
                return;
            }

            foreach (var kv in present)
            {
                if (!_known.TryGetValue(kv.Key, out var sig))
                {
                    if (_pendingLocked.Contains(kv.Key)) continue;
                    changes.Add(new FolderChange { Kind = FolderChangeKind.Added, Name = kv.Key, Path = kv.Value.path });
                }
                else if (sig != kv.Value.sig)
                    changes.Add(new FolderChange { Kind = FolderChangeKind.Changed, Name = kv.Key, Path = kv.Value.path });
                _known[kv.Key] = kv.Value.sig;
            }
            var removed = new List<string>();
            foreach (var k in _known.Keys) if (!present.ContainsKey(k)) removed.Add(k);
            foreach (var k in removed)
            {
                _known.Remove(k);
                changes.Add(new FolderChange { Kind = FolderChangeKind.Removed, Name = k, Path = Path.Combine(Root, k) });
            }
        }

        static string Signature(string dir, string model)
        {
            var sb = new System.Text.StringBuilder();
            void Add(string f)
            {
                if (!File.Exists(f)) return;
                var fi = new FileInfo(f);
                sb.Append(fi.Name).Append(':').Append(fi.Length).Append(':').Append(fi.LastWriteTimeUtc.Ticks).Append(';');
            }
            Add(model);
            Add(Path.Combine(dir, "character.json"));
            var anim = Path.Combine(dir, "animations");
            if (Directory.Exists(anim))
                foreach (var f in Directory.GetFiles(anim)) Add(f);
            return sb.ToString();
        }

        /// <summary>다른 프로세스가 쓰는 중이면 true (복사 중인 파일을 읽지 않기 위함).</summary>
        public static bool IsLocked(string path)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                return false;
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        public void Dispose()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }
    }
}
