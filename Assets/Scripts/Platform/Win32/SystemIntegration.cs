using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Shimeji.Core;

namespace Shimeji.Platform.Win32
{
    /// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion\Run 등록/해제 (advapi32 직접 호출).</summary>
    public static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string DefaultValueName = "ShimejiDesktopMascot";
        static readonly UIntPtr HKEY_CURRENT_USER = new UIntPtr(0x80000001u);
        const int KEY_QUERY_VALUE = 0x0001, KEY_SET_VALUE = 0x0002;
        const int REG_SZ = 1;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegOpenKeyExW(UIntPtr hKey, string subKey, int options, int sam, out UIntPtr result);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegSetValueExW(UIntPtr hKey, string name, int reserved, int type, byte[] data, int size);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegQueryValueExW(UIntPtr hKey, string name, IntPtr reserved, out int type, byte[] data, ref int size);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegDeleteValueW(UIntPtr hKey, string name);
        [DllImport("advapi32.dll")]
        static extern int RegCloseKey(UIntPtr hKey);

        public static bool IsEnabled(string valueName = DefaultValueName) => GetCommand(valueName) != null;

        public static string GetCommand(string valueName = DefaultValueName)
        {
            try
            {
                if (RegOpenKeyExW(HKEY_CURRENT_USER, RunKey, 0, KEY_QUERY_VALUE, out var key) != 0) return null;
                try
                {
                    int size = 0;
                    if (RegQueryValueExW(key, valueName, IntPtr.Zero, out _, null, ref size) != 0 || size <= 0) return null;
                    var buf = new byte[size];
                    if (RegQueryValueExW(key, valueName, IntPtr.Zero, out _, buf, ref size) != 0) return null;
                    return Encoding.Unicode.GetString(buf, 0, size).TrimEnd('\0');
                }
                finally { RegCloseKey(key); }
            }
            catch (Exception e)
            {
                Log.Warn("AutoStart query failed: " + e.Message);
                return null;
            }
        }

        public static bool SetEnabled(bool enabled, string exePath, string valueName = DefaultValueName)
        {
            try
            {
                if (RegOpenKeyExW(HKEY_CURRENT_USER, RunKey, 0, KEY_SET_VALUE | KEY_QUERY_VALUE, out var key) != 0) return false;
                try
                {
                    if (enabled)
                    {
                        var data = Encoding.Unicode.GetBytes("\"" + exePath + "\"\0");
                        return RegSetValueExW(key, valueName, 0, REG_SZ, data, data.Length) == 0;
                    }
                    int r = RegDeleteValueW(key, valueName);
                    return r == 0 || r == 2; // 2 = 이미 없음
                }
                finally { RegCloseKey(key); }
            }
            catch (Exception e)
            {
                Log.Warn("AutoStart update failed: " + e.Message);
                return false;
            }
        }
    }

    /// <summary>이름 있는 Mutex로 단일 인스턴스 보장.</summary>
    public sealed class SingleInstance : IDisposable
    {
        Mutex _mutex;
        public bool IsFirst { get; }

        public SingleInstance(string name)
        {
            try
            {
                _mutex = new Mutex(true, @"Local\" + name, out bool created);
                IsFirst = created;
                if (!created) { _mutex.Dispose(); _mutex = null; }
            }
            catch (Exception e)
            {
                Log.Warn("SingleInstance mutex failed, continuing: " + e.Message);
                IsFirst = true;
            }
        }

        public void Dispose()
        {
            try { _mutex?.ReleaseMutex(); } catch (Exception) { /* 다른 스레드 소유 등 */ }
            _mutex?.Dispose();
            _mutex = null;
        }
    }
}
