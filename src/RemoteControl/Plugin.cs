using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace LurkBait.RemoteControl
{
    // Adds the inbound direction the game lacks: a tiny localhost HTTP endpoint that
    // external software (Streamer.bot, SAMMI, ...) can call to adjust a player's gold.
    //
    // TcpListener (not HttpListener) is used on purpose: HttpListener sits on Windows
    // HTTP.sys and needs a URL-ACL reservation / elevation, which a Steam game running as
    // a normal user doesn't have. TcpListener + minimal HTTP parsing avoids all of that.
    [BepInPlugin(PluginGuid, "LurkBait Remote Control", "1.0.1")]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.irensuidas.lurkbait.remotecontrol";

        internal static ManualLogSource Log;
        internal static bool Running;
        internal static int Port;
        internal static bool Notify;

        private GoldServer _server;

        private void Awake()
        {
            Log = Logger;

            var port = Config.Bind(
                "Server",
                "Port",
                30500,
                new ConfigDescription(
                    "TCP port to listen on. Bound to 127.0.0.1 only (never exposed off-machine).",
                    new AcceptableValueRange<int>(1024, 65535)
                )
            );
            Port = port.Value;

            Notify = Config
                .Bind(
                    "Server",
                    "ShowNotifications",
                    true,
                    "Show an in-game toast whenever an endpoint adds/removes/sets a player's gold."
                )
                .Value;

            _server = new GoldServer(Port, LoadOrCreateToken());
            _server.Start();
            Running = _server.IsRunning;

            // Post an in-game toast right after the game's "connected to Twitch" one.
            new Harmony(PluginGuid).PatchAll(typeof(Plugin).Assembly);
        }

        private void Update() => _server?.DrainMainThread();

        private void OnDestroy() => _server?.Stop();

        // Every request must carry this token. It's auto-managed: read from a file if one
        // exists, otherwise generated and written so the user just opens the file to get it.
        // Delete the file to rotate the token on next launch.
        private static string LoadOrCreateToken()
        {
            string path = Path.Combine(
                Paths.ConfigPath,
                "dev.irensuidas.lurkbait.remotecontrol.token"
            );
            try
            {
                if (File.Exists(path))
                {
                    string existing = File.ReadAllText(path).Trim();
                    if (existing.Length > 0)
                    {
                        Log.LogInfo($"Auth token loaded from {path}");
                        return existing;
                    }
                }

                string token =
                    System.Guid.NewGuid().ToString("N") + System.Guid.NewGuid().ToString("N");
                File.WriteAllText(path, token);
                Log.LogInfo($"Generated a new auth token at {path}");
                return token;
            }
            catch (Exception e)
            {
                // Fall back to a session-only token so the server never runs unauthenticated.
                Log.LogError(
                    $"Token file unavailable ({e.Message}); using a temporary in-memory token."
                );
                return System.Guid.NewGuid().ToString("N");
            }
        }
    }

    // Posts an in-game toast right after the game's "connected to Twitch" one, so it's
    // obvious the endpoint is live. Once per session, gated on the server actually running.
    [HarmonyPatch(typeof(TwitchConnectorEventSub), nameof(TwitchConnectorEventSub.LoggedIn))]
    internal static class LoggedInPatch
    {
        private static bool _shown;

        private static void Postfix()
        {
            if (_shown || NotificationController.Instance == null)
                return;
            _shown = true;
            try
            {
                NotificationController.Instance.QueueNotif(
                    Plugin.Running
                        ? $"Remote Control enabled on port {Plugin.Port}!"
                        : "Remote Control failed to start (check the BepInEx log)."
                );
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("Notification failed: " + e);
            }
        }
    }

    internal sealed class GoldServer
    {
        private readonly int _port;
        private readonly string _token;
        private readonly ConcurrentQueue<Command> _queue = new ConcurrentQueue<Command>();
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;

        public GoldServer(int port, string token)
        {
            _port = port;
            _token = token ?? "";
        }

        public void Start()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, _port);
                _listener.Start();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"Could not listen on 127.0.0.1:{_port} - {e.Message}");
                return;
            }

            _running = true;
            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "LurkBaitRemoteControl",
            };
            _acceptThread.Start();
            Plugin.Log?.LogInfo(
                $"Listening on http://127.0.0.1:{_port}/  (auth: {(_token.Length > 0 ? "token required" : "none")})"
            );
        }

        public bool IsRunning => _running;

        public void Stop()
        {
            _running = false;
            try
            {
                _listener?.Stop();
            }
            catch
            { /* ignore */
            }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch
                {
                    break;
                }
                ThreadPool.QueueUserWorkItem(HandleClient, client);
            }
        }

        public void DrainMainThread()
        {
            while (_queue.TryDequeue(out var cmd))
            {
                try
                {
                    Execute(cmd);
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogError("Command failed: " + e);
                    cmd.Complete(
                        HttpStatusCode.ServiceUnavailable,
                        Json.Result(false, cmd.User, message: "Internal error.")
                    );
                }
            }
        }

        private static void Execute(Command cmd)
        {
            var pm = PlayersManager.Instance;
            if (pm == null || pm.Players == null)
            {
                cmd.Complete(
                    HttpStatusCode.ServiceUnavailable,
                    Json.Result(false, cmd.User, message: "Game not ready yet.")
                );
                return;
            }

            if (!pm.Players.TryGetValue(cmd.User, out var data) || data == null)
            {
                cmd.Complete(
                    HttpStatusCode.NotFound,
                    Json.Result(
                        false,
                        cmd.User,
                        existed: false,
                        message: $"No record for user '{cmd.User}'."
                    )
                );
                return;
            }

            string display = string.IsNullOrEmpty(data.displayName) ? cmd.User : data.displayName;
            int before = data.gold;

            if (cmd.Op == Op.Get)
            {
                cmd.Complete(
                    HttpStatusCode.OK,
                    Json.Result(
                        true,
                        cmd.User,
                        display,
                        existed: true,
                        gold: before,
                        message: $"{display} has {before} gold."
                    )
                );
                return;
            }

            if (cmd.Op == Op.Subtract && cmd.Strict && before < cmd.Amount)
            {
                cmd.Complete(
                    HttpStatusCode.Conflict,
                    Json.Result(
                        false,
                        cmd.User,
                        display,
                        existed: true,
                        gold: before,
                        requested: cmd.Amount,
                        applied: 0,
                        message: $"{display} has {before} gold but {cmd.Amount} was requested."
                    )
                );
                return;
            }

            switch (cmd.Op)
            {
                case Op.Add:
                    data.gold = before + cmd.Amount;
                    break;
                case Op.Set:
                    data.gold = cmd.Amount;
                    break;
                case Op.Subtract:
                    data.gold = Math.Max(0, before - cmd.Amount);
                    break;
            }
            if (data.gold < 0)
                data.gold = 0;

            if (data.goldSnapshot > data.gold)
                data.goldSnapshot = data.gold;

            int after = data.gold;
            pm.SaveData();

            int applied = Math.Abs(after - before);
            string message =
                cmd.Op == Op.Add ? $"Added {applied} gold to {display}; now {after}."
                : cmd.Op == Op.Set ? $"Set {display} to {after} gold."
                : after == 0 ? $"{display} is out of gold (removed {applied})."
                : $"Removed {applied} gold from {display}; {after} left.";

            if (Plugin.Notify && NotificationController.Instance != null)
            {
                string toast =
                    cmd.Op == Op.Add ? $"{applied} gold was added to @{display}!"
                    : cmd.Op == Op.Set ? $"@{display}'s gold was set to {after}!"
                    : after == 0 ? $"@{display} is out of gold!"
                    : $"{applied} gold was removed from @{display}!";
                NotificationController.Instance.QueueNotif(toast);
            }

            cmd.Complete(
                HttpStatusCode.OK,
                Json.Result(
                    true,
                    cmd.User,
                    display,
                    existed: true,
                    gold: after,
                    requested: cmd.Amount,
                    applied: applied,
                    message: message
                )
            );
        }

        private void HandleClient(object state)
        {
            var client = (TcpClient)state;
            try
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                client.NoDelay = true;
                using (client)
                using (var stream = client.GetStream())
                {
                    var request = HttpRequest.Read(stream);
                    if (request == null)
                    {
                        Respond(
                            stream,
                            HttpStatusCode.BadRequest,
                            Json.Result(false, message: "Malformed request.")
                        );
                        return;
                    }

                    if (
                        _token.Length > 0
                        && !string.Equals(request.Token, _token, StringComparison.Ordinal)
                    )
                    {
                        Respond(
                            stream,
                            HttpStatusCode.Unauthorized,
                            Json.Result(false, message: "Invalid or missing token.")
                        );
                        return;
                    }

                    if (request.Path == "/ping")
                    {
                        Respond(
                            stream,
                            HttpStatusCode.OK,
                            Json.Result(true, message: "LurkBait Remote Control is running.")
                        );
                        return;
                    }

                    if (!TryRouteGold(request.Path, out var op))
                    {
                        Respond(
                            stream,
                            HttpStatusCode.NotFound,
                            Json.Result(false, message: $"Unknown endpoint '{request.Path}'.")
                        );
                        return;
                    }

                    string user = request.Query.TryGetValue("user", out var u)
                        ? u.Trim().ToLowerInvariant()
                        : null;
                    if (string.IsNullOrEmpty(user))
                    {
                        Respond(
                            stream,
                            HttpStatusCode.BadRequest,
                            Json.Result(false, message: "Missing 'user' parameter.")
                        );
                        return;
                    }

                    int amount = 0;
                    if (op != Op.Get)
                    {
                        if (
                            !request.Query.TryGetValue("amount", out var a)
                            || !int.TryParse(a, out amount)
                            || amount < 0
                        )
                        {
                            Respond(
                                stream,
                                HttpStatusCode.BadRequest,
                                Json.Result(
                                    false,
                                    user,
                                    message: "'amount' must be a non-negative integer."
                                )
                            );
                            return;
                        }
                    }

                    bool strict =
                        request.Query.TryGetValue("strict", out var s)
                        && (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase));

                    var cmd = new Command
                    {
                        Op = op,
                        User = user,
                        Amount = amount,
                        Strict = strict,
                    };
                    _queue.Enqueue(cmd);

                    if (!cmd.Wait(8000))
                    {
                        Respond(
                            stream,
                            HttpStatusCode.ServiceUnavailable,
                            Json.Result(false, user, message: "Timed out waiting for the game.")
                        );
                        return;
                    }

                    Respond(stream, cmd.Status, cmd.Body);
                    cmd.Dispose();
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("Request handling failed: " + e.Message);
            }
        }

        private static bool TryRouteGold(string path, out Op op)
        {
            switch (path)
            {
                case "/gold/get":
                    op = Op.Get;
                    return true;
                case "/gold/add":
                    op = Op.Add;
                    return true;
                case "/gold/subtract":
                    op = Op.Subtract;
                    return true;
                case "/gold/set":
                    op = Op.Set;
                    return true;
                default:
                    op = Op.Get;
                    return false;
            }
        }

        private static void Respond(NetworkStream stream, HttpStatusCode code, string json)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var header =
                $"HTTP/1.1 {(int)code} {Reason(code)}\r\n"
                + "Content-Type: application/json; charset=utf-8\r\n"
                + $"Content-Length: {body.Length}\r\n"
                + "Connection: close\r\n\r\n";
            var head = Encoding.ASCII.GetBytes(header);
            stream.Write(head, 0, head.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static string Reason(HttpStatusCode code)
        {
            switch (code)
            {
                case HttpStatusCode.OK:
                    return "OK";
                case HttpStatusCode.BadRequest:
                    return "Bad Request";
                case HttpStatusCode.Unauthorized:
                    return "Unauthorized";
                case HttpStatusCode.NotFound:
                    return "Not Found";
                case HttpStatusCode.Conflict:
                    return "Conflict";
                case HttpStatusCode.ServiceUnavailable:
                    return "Service Unavailable";
                default:
                    return code.ToString();
            }
        }
    }

    internal enum Op
    {
        Get,
        Add,
        Subtract,
        Set,
    }

    internal sealed class Command : IDisposable
    {
        private readonly ManualResetEventSlim _done = new ManualResetEventSlim(false);

        public void Dispose() => _done.Dispose();

        public Op Op;
        public string User;
        public int Amount;
        public bool Strict;

        public HttpStatusCode Status { get; private set; }
        public string Body { get; private set; }

        public void Complete(HttpStatusCode status, string body)
        {
            Status = status;
            Body = body;
            _done.Set();
        }

        public bool Wait(int millis) => _done.Wait(millis);
    }

    internal sealed class HttpRequest
    {
        public string Path;
        public Dictionary<string, string> Query;
        public string Token;

        public static HttpRequest Read(Stream stream)
        {
            var reader = new StreamReader(stream, Encoding.ASCII);
            string requestLine = reader.ReadLine();
            if (string.IsNullOrEmpty(requestLine))
                return null;

            string authHeader = null,
                line;
            int contentLength = 0;
            while (!string.IsNullOrEmpty(line = reader.ReadLine()))
            {
                int c = line.IndexOf(':');
                if (c <= 0)
                    continue;
                string name = line.Substring(0, c).Trim();
                string value = line.Substring(c + 1).Trim();
                if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                    authHeader = value;
                else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(value, out contentLength))
                        contentLength = 0;
                }
            }
            if (contentLength > 0 && contentLength <= 1_000_000)
            {
                var buf = new char[Math.Min(contentLength, 8192)];
                int remaining = contentLength;
                while (remaining > 0)
                {
                    int n = reader.Read(buf, 0, Math.Min(buf.Length, remaining));
                    if (n <= 0)
                        break;
                    remaining -= n;
                }
            }

            var parts = requestLine.Split(' ');
            if (parts.Length < 2)
                return null;

            string target = parts[1];
            string path = target,
                query = "";
            int q = target.IndexOf('?');
            if (q >= 0)
            {
                path = target.Substring(0, q);
                query = target.Substring(q + 1);
            }

            path = path.TrimEnd('/').ToLowerInvariant();
            if (path.Length == 0)
                path = "/";

            var qp = ParseQuery(query);
            string token = qp.TryGetValue("token", out var t) ? t : null;
            if (
                token == null
                && authHeader != null
                && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            )
                token = authHeader.Substring(7).Trim();

            return new HttpRequest
            {
                Path = path,
                Query = qp,
                Token = token,
            };
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query))
                return d;
            foreach (var pair in query.Split('&'))
            {
                if (pair.Length == 0)
                    continue;
                int eq = pair.IndexOf('=');
                string k = eq >= 0 ? pair.Substring(0, eq) : pair;
                string v = eq >= 0 ? pair.Substring(eq + 1) : "";
                d[Uri.UnescapeDataString(k)] = Uri.UnescapeDataString(v);
            }
            return d;
        }
    }

    internal static class Json
    {
        public static string Result(
            bool ok,
            string user = null,
            string display = null,
            bool? existed = null,
            int? gold = null,
            int? requested = null,
            int? applied = null,
            string message = null
        )
        {
            var sb = new StringBuilder("{");
            sb.Append("\"ok\":").Append(ok ? "true" : "false");
            if (user != null)
                Field(sb, "user", user);
            if (display != null)
                Field(sb, "display", display);
            if (existed.HasValue)
                sb.Append(",\"existed\":").Append(existed.Value ? "true" : "false");
            if (gold.HasValue)
                sb.Append(",\"gold\":").Append(gold.Value);
            if (requested.HasValue)
                sb.Append(",\"requested\":").Append(requested.Value);
            if (applied.HasValue)
                sb.Append(",\"applied\":").Append(applied.Value);
            if (message != null)
                Field(sb, "message", message);
            return sb.Append('}').ToString();
        }

        private static void Field(StringBuilder sb, string key, string value)
        {
            sb.Append(",\"").Append(key).Append("\":\"").Append(Escape(value)).Append('"');
        }

        private static string Escape(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                            sb.Append("\\u")
                                .Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
