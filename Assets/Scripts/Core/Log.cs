using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Shimeji.Core
{
    public enum LogLevel { Info, Warn, Error }

    public interface ILogSink
    {
        void Write(LogLevel level, string message);
    }

    /// <summary>엔진 독립 로거. Unity 콘솔/파일 싱크는 바깥에서 등록한다.</summary>
    public static class Log
    {
        static readonly List<ILogSink> Sinks = new List<ILogSink>();
        static readonly object Gate = new object();

        /// <summary>사용자에게 알려야 하는 오류(트레이 알림 등).</summary>
        public static event Action<string> UserVisibleError;

        public static void AddSink(ILogSink sink) { lock (Gate) Sinks.Add(sink); }
        public static void RemoveSink(ILogSink sink) { lock (Gate) Sinks.Remove(sink); }
        public static void ClearSinks() { lock (Gate) Sinks.Clear(); }

        public static void Info(string m) => Write(LogLevel.Info, m);
        public static void Warn(string m) => Write(LogLevel.Warn, m);
        public static void Error(string m) => Write(LogLevel.Error, m);

        /// <summary>오류를 기록하고 사용자 알림 이벤트도 발생시킨다.</summary>
        public static void UserError(string m)
        {
            Write(LogLevel.Error, m);
            try { UserVisibleError?.Invoke(m); } catch { /* 알림 실패는 무시 */ }
        }

        static void Write(LogLevel level, string m)
        {
            ILogSink[] sinks;
            lock (Gate) sinks = Sinks.ToArray();
            foreach (var s in sinks)
            {
                try { s.Write(level, m); } catch { /* 로거는 절대 예외를 던지지 않는다 */ }
            }
        }
    }

    /// <summary>실행마다 새 파일을 만들고 최근 N개만 유지하는 롤링 파일 로그.</summary>
    public sealed class RollingFileLogSink : ILogSink, IDisposable
    {
        readonly StreamWriter _writer;
        readonly object _gate = new object();
        public string FilePath { get; }

        public RollingFileLogSink(string directory, int keep = 5, string prefix = "shimeji")
        {
            Directory.CreateDirectory(directory);
            var old = Directory.GetFiles(directory, prefix + "_*.log")
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .Skip(Math.Max(0, keep - 1))
                .ToList();
            foreach (var f in old)
            {
                try { File.Delete(f); } catch { /* 다른 프로세스가 열고 있으면 다음 기회에 */ }
            }
            FilePath = Path.Combine(directory, $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.log");
            _writer = new StreamWriter(new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
            {
                AutoFlush = true
            };
        }

        public void Write(LogLevel level, string message)
        {
            lock (_gate) _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}");
        }

        public void Dispose()
        {
            lock (_gate) _writer.Dispose();
        }
    }
}
