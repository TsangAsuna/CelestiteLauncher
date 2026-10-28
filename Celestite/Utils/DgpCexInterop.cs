using System;
using System.IO;
using System.Runtime.InteropServices;
using ZeroLog;

namespace Celestite.Utils
{
    /// <summary>
    /// P/Invoke 桥接 DMMGamePlayer 的 Go 网络层（resources/cextend/windows_amd64_cex.dll）。
    /// DMM 已作废旧 OAuth Basic 凭据（E210002），新协议的客户端凭据与请求签名
    /// 全部由 cex.dll（dgp5go）内部持有。通过 cgo 导出函数可以直接复用同一登录协议。
    ///
    /// 已实测确认（本机抓取验证）：
    /// - LoadLibraryEx(LOAD_WITH_ALTERED_SEARCH_PATH) 可在任意进程加载
    /// - DgpInit("{\"execute_args\":\"\"}") => environment JSON（含 grpcPort）
    /// - DgpLogin 为单 JSON 字符串参数、返回 JSON 字符串（cdecl）
    /// </summary>
    public static class DgpCexInterop
    {
        private const string CexRelativePath = @"resources\cextend\windows_amd64_cex.dll";
        private static readonly Log Logger = LogManager.GetLogger("DgpCex");

        private static IntPtr _module;
        private static OneArg? _dgpInit;
        private static OneArg? _dgpLogin;
        private static OneArg? _dgpGetUserInfo;
        private static bool _initialized;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private delegate IntPtr OneArg([MarshalAs(UnmanagedType.LPStr)] string json);

        private static string? ResolveCexPath()
        {
            // 1) 运行中的 DMMGamePlayer 进程定位安装目录
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("DMMGamePlayer"))
                {
                    try
                    {
                        var f = p.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(f))
                        {
                            var dir = Path.GetDirectoryName(f)!;
                            var candidate = Path.Combine(dir, CexRelativePath);
                            if (File.Exists(candidate)) return candidate;
                        }
                    }
                    catch { /* MainModule 权限失败，继续 */ }
                }
            }
            catch { /* 忽略 */ }

            // 2) WMI
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT ExecutablePath FROM Win32_Process WHERE Name = 'DMMGamePlayer.exe'");
                foreach (var o in searcher.Get())
                {
                    if (o["ExecutablePath"] is string path && !string.IsNullOrEmpty(path))
                    {
                        var dir = Path.GetDirectoryName(path)!;
                        var candidate = Path.Combine(dir, CexRelativePath);
                        if (File.Exists(candidate)) return candidate;
                    }
                }
            }
            catch { /* WMI 不可用 */ }

            // 3) 默认安装路径
            string[] defaults =
            [
                @"C:\Program Files\DMMGamePlayer\" + CexRelativePath,
                @"C:\Program Files (x86)\DMMGamePlayer\" + CexRelativePath,
            ];
            foreach (var d in defaults)
                if (File.Exists(d)) return d;
            return null;
        }

        /// <summary>
        /// 加载 cex.dll 并初始化 Go 层。失败返回 false（例如未安装 DMMGamePlayer）。
        /// </summary>
        public static bool EnsureLoaded()
        {
            if (_initialized) return _module != IntPtr.Zero;
            lock (typeof(DgpCexInterop))
            {
                if (_initialized) return _module != IntPtr.Zero;
                _initialized = true;
                try
                {
                    var cexPath = ResolveCexPath();
                    if (string.IsNullOrEmpty(cexPath))
                    {
                        Logger.Warn("cex.dll not found (DMMGamePlayer not installed?)");
                        return false;
                    }
                    // LOAD_WITH_ALTERED_SEARCH_PATH：依赖从 DLL 自身目录解析
                    _module = LoadLibraryEx(cexPath, IntPtr.Zero, 0x8);
                    if (_module == IntPtr.Zero)
                    {
                        Logger.Warn($"LoadLibraryEx cex failed: {Marshal.GetLastWin32Error()}");
                        return false;
                    }
                    _dgpInit = GetDelegate(_module, "DgpInit");
                    _dgpLogin = GetDelegate(_module, "DgpLogin");
                    _dgpGetUserInfo = GetDelegate(_module, "DgpGetUserInfo");
                    var env = Call(_dgpInit, "{\"execute_args\":\"\"}");
                    Logger.Info($"cex DgpInit => {Truncate(env, 200)}");
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Error("cex load failed", ex);
                    return false;
                }
            }
        }

        private static OneArg? GetDelegate(IntPtr module, string name)
        {
            var p = GetProcAddress(module, name);
            return p == IntPtr.Zero ? null : (OneArg?)Marshal.GetDelegateForFunctionPointer(p, typeof(OneArg));
        }

        private static string Call(OneArg? f, string json)
        {
            if (f == null) throw new InvalidOperationException("cex function not resolved");
            var ptr = f.Invoke(json);
            var result = Marshal.PtrToStringAnsi(ptr) ?? string.Empty;
            return result;
        }

        private static string Truncate(string s, int len) => s.Length <= len ? s : s[..len];

        /// <summary>
        /// 通过 cex.dll 的 DgpLogin 用 DMM 当前登录协议完成账号密码登录。
        /// 返回原始 JSON 响应（内含 access_token / id_token / refresh_token 等，字段由 DMM 决定）。
        /// </summary>
        public static string? Login(string email, string password)
        {
            if (!EnsureLoaded() || _dgpLogin == null) return null;
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(new { email, password });
                var raw = Call(_dgpLogin, json);
                Logger.Info($"DgpLogin => {Truncate(raw, 300)}");
                return raw;
            }
            catch (Exception ex)
            {
                Logger.Error("DgpLogin failed", ex);
                return null;
            }
        }

        /// <summary>调用 DgpGetUserInfo（需已 DgpLogin 的进程内会话）。</summary>
        public static string? GetUserInfo()
        {
            if (!EnsureLoaded() || _dgpGetUserInfo == null) return null;
            try { return Call(_dgpGetUserInfo, "{}"); }
            catch (Exception ex) { Logger.Warn("DgpGetUserInfo failed: " + ex.Message); return null; }
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr res, uint flags);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
    }
}
