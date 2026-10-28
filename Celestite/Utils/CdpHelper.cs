using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using ZeroLog;

namespace Celestite.Utils
{
    public class CdpCookie
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string Domain { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public bool HttpOnly { get; set; }
    }

    /// <summary>
    /// 通过 Chrome DevTools Protocol 从运行中的 DMMGamePlayer（Electron）读取明文 cookie。
    /// DMMGamePlayer 需以 --remote-debugging-port 参数启动；由调用方负责重启带参。
    /// </summary>
    public static class CdpHelper
    {
        private const int DefaultPort = 9222;
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
        private static readonly Log Logger = LogManager.GetLogger("CDP");

        public static int Port => DefaultPort;

        /// <summary>调试端口是否在线。</summary>
        public static async Task<bool> IsDebugPortAlive(int port = DefaultPort)
        {
            try
            {
                using var resp = await Http.GetAsync($"http://127.0.0.1:{port}/json/version");
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private sealed class WsMessage
        {
            public int Id { get; set; }
            public string? Method { get; set; }
            public JsonElement? Result { get; set; }
            public JsonElement? Error { get; set; }
        }

        private static async UniTask<JsonElement?> SendCdpAsync(ClientWebSocket ws, string method, object? parameters, int id, CancellationToken ct)
        {
            var request = parameters == null
                ? JsonSerializer.Serialize(new { id, method })
                : JsonSerializer.Serialize(new { id, method, parameters });
            var bytes = Encoding.UTF8.GetBytes(request);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);

            var buffer = new byte[1024 * 1024];
            while (!ct.IsCancellationRequested)
            {
                var total = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer.AsMemory(total), ct);
                    total += result.Count;
                    if (result.EndOfMessage) break;
                    if (total >= buffer.Length) throw new IOException("CDP message too large");
                } while (!result.EndOfMessage);

                var text = Encoding.UTF8.GetString(buffer, 0, total);
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var idEl) && idEl.GetInt32() == id)
                {
                    if (root.TryGetProperty("result", out var resultEl))
                        return resultEl.Clone();
                    if (root.TryGetProperty("error", out var errorEl))
                        throw new InvalidOperationException("CDP error: " + errorEl.ToString());
                    return null;
                }
                // 忽略事件消息，继续收
            }
            return null;
        }

        /// <summary>
        /// 通过 CDP 读取全部 cookie。先尝试 browser 端 Storage.getCookies，
        /// 失败则遍历页面 target 用 Network.getAllCookies。
        /// </summary>
        public static async UniTask<List<CdpCookie>> GetAllCookiesAsync(int port = DefaultPort)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            string? browserWs = null;
            try
            {
                var versionJson = await Http.GetStringAsync($"http://127.0.0.1:{port}/json/version", cts.Token);
                using var vdoc = JsonDocument.Parse(versionJson);
                browserWs = vdoc.RootElement.GetProperty("webSocketDebuggerUrl").GetString();
            }
            catch (Exception ex)
            {
                Logger.Warn($"CDP /json/version failed: {ex.Message}");
            }

            if (!string.IsNullOrEmpty(browserWs))
            {
                try
                {
                    using var ws = new ClientWebSocket();
                    await ws.ConnectAsync(new Uri(browserWs), cts.Token);
                    var result = await SendCdpAsync(ws, "Storage.getCookies", null, 1, cts.Token);
                    if (result.HasValue)
                    {
                        var cookies = ParseCookies(result.Value, "cookies");
                        if (cookies.Count > 0) return cookies;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"CDP browser Storage.getCookies failed: {ex.Message}");
                }
            }

            // 兜底：遍历 page targets
            try
            {
                var listJson = await Http.GetStringAsync($"http://127.0.0.1:{port}/json/list", cts.Token);
                using var ldoc = JsonDocument.Parse(listJson);
                foreach (var target in ldoc.RootElement.EnumerateArray())
                {
                    if (!target.TryGetProperty("webSocketDebuggerUrl", out var wsUrlEl)) continue;
                    var wsUrl = wsUrlEl.GetString();
                    if (string.IsNullOrEmpty(wsUrl)) continue;
                    try
                    {
                        using var ws = new ClientWebSocket();
                        await ws.ConnectAsync(new Uri(wsUrl), cts.Token);
                        var result = await SendCdpAsync(ws, "Network.getAllCookies", null, 2, cts.Token);
                        if (result.HasValue)
                        {
                            var cookies = ParseCookies(result.Value, "cookies");
                            if (cookies.Count > 0) return cookies;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"CDP page target failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"CDP /json/list failed: {ex.Message}");
            }

            return [];
        }

        private static List<CdpCookie> ParseCookies(JsonElement result, string propertyName)
        {
            var list = new List<CdpCookie>();
            if (!result.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var c in arr.EnumerateArray())
            {
                try
                {
                    list.Add(new CdpCookie
                    {
                        Name = c.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                        Value = c.TryGetProperty("value", out var v) ? v.GetString() ?? string.Empty : string.Empty,
                        Domain = c.TryGetProperty("domain", out var d) ? d.GetString() ?? string.Empty : string.Empty,
                        Path = c.TryGetProperty("path", out var p) ? p.GetString() ?? string.Empty : string.Empty,
                        HttpOnly = c.TryGetProperty("httpOnly", out var h) && h.GetBoolean()
                    });
                }
                catch
                {
                    // 单条 cookie 解析失败忽略
                }
            }
            return list;
        }

        /// <summary>
        /// 从 CDP cookie 集合中提取 DMM 登录会话。
        /// 返回 (secureId, sessionId)；任一缺失返回 null 元组。
        /// </summary>
        public static (string? SecureId, string? SessionId) ExtractDmmSession(IEnumerable<CdpCookie> cookies)
        {
            string? secureId = null, sessionId = null;
            foreach (var c in cookies)
            {
                var isDmm = c.Domain.EndsWith("dmm.com", StringComparison.OrdinalIgnoreCase) ||
                            c.Domain.EndsWith("dmm.co.jp", StringComparison.OrdinalIgnoreCase);
                if (!isDmm) continue;
                if (c.Name == "login_secure_id" && string.IsNullOrEmpty(secureId)) secureId = c.Value;
                if (c.Name == "login_session_id" && string.IsNullOrEmpty(sessionId)) sessionId = c.Value;
            }
            return (secureId, sessionId);
        }
    }
}
