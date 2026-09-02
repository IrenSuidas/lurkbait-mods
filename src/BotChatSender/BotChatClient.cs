using System;
using System.Collections;
using System.IO;
using System.Text;
using BepInEx;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace LurkBait.BotChatSender
{
    // Owns the bot account's Twitch OAuth token (device-code grant, no client secret) and sends chat as
    // the bot via Helix. The game's client id and broadcaster id are read live off
    // TwitchConnectorEventSub. All web work runs as Unity coroutines on the main thread.
    internal sealed class BotChatClient
    {
        private const string Scope = "user:write:chat";
        private const string DeviceEndpoint = "https://id.twitch.tv/oauth2/device";
        private const string TokenEndpoint = "https://id.twitch.tv/oauth2/token";
        private const string UsersEndpoint = "https://api.twitch.tv/helix/users";
        private const string SendEndpoint = "https://api.twitch.tv/helix/chat/messages";
        private const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";
        private const string TokenFileName = "dev.irensuidas.lurkbait.botchatsender.token.json";

        private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(10);

        private readonly MonoBehaviour _host;
        private readonly string _tokenPath;

        private string _accessToken;
        private string _refreshToken;
        private DateTime _expiryUtc;
        private string _botUserId;
        private string _botLogin;

        private bool _authInProgress;
        private bool _refreshInProgress;
        private string _status = "not logged in";

        public BotChatClient(MonoBehaviour host)
        {
            _host = host;
            _tokenPath = Path.Combine(Paths.ConfigPath, TokenFileName);
        }

        public string StatusText => _status;

        public bool Ready =>
            !string.IsNullOrEmpty(_accessToken)
            && !string.IsNullOrEmpty(_botUserId)
            && DateTime.UtcNow < _expiryUtc;

        public string ButtonLabel =>
            Ready ? $"Bot: @{_botLogin} (log out)"
            : _authInProgress ? "Authorizing bot..."
            : "Connect bot account";

        public void LoadStoredToken()
        {
            try
            {
                if (!File.Exists(_tokenPath))
                    return;
                var stored = JsonConvert.DeserializeObject<StoredToken>(
                    File.ReadAllText(_tokenPath)
                );
                if (stored == null || string.IsNullOrEmpty(stored.refresh_token))
                    return;
                _accessToken = stored.access_token;
                _refreshToken = stored.refresh_token;
                _expiryUtc = DateTimeOffset.FromUnixTimeSeconds(stored.expires_at_unix).UtcDateTime;
                _botUserId = stored.user_id;
                _botLogin = stored.login;
                _status = Ready ? $"logged in as @{_botLogin}" : "restoring session...";
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not load stored bot token: " + e.Message);
            }
        }

        public void Tick()
        {
            if (_authInProgress || _refreshInProgress || string.IsNullOrEmpty(_refreshToken))
                return;

            bool needsRefresh =
                string.IsNullOrEmpty(_accessToken) || DateTime.UtcNow >= _expiryUtc - RefreshMargin;
            if (needsRefresh)
                _host.StartCoroutine(RefreshRoutine());
        }

        public void StartLogin()
        {
            if (_authInProgress)
            {
                Plugin.Log.LogInfo("Bot login is already in progress.");
                return;
            }
            _host.StartCoroutine(LoginRoutine());
        }

        public void Logout()
        {
            _accessToken = null;
            _refreshToken = null;
            _botUserId = null;
            _botLogin = null;
            _expiryUtc = default;
            _status = "logged out";
            try
            {
                if (File.Exists(_tokenPath))
                    File.Delete(_tokenPath);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not delete stored bot token: " + e.Message);
            }
            Plugin.Log.LogInfo("Bot account signed out; stored token deleted.");
            Notify("Bot account signed out.");
        }

        public bool TrySend(string message)
        {
            if (!Ready)
                return false;
            string clientId = GameClientId();
            string broadcasterId = GameBroadcasterId();
            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(broadcasterId))
                return false;
            _host.StartCoroutine(SendRoutine(clientId, broadcasterId, message));
            return true;
        }

        private IEnumerator SendRoutine(string clientId, string broadcasterId, string message)
        {
            string json = JsonConvert.SerializeObject(
                new SendBody
                {
                    broadcaster_id = broadcasterId,
                    sender_id = _botUserId,
                    message = message,
                }
            );

            var req = new UnityWebRequest(SendEndpoint, "POST")
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
                downloadHandler = new DownloadHandlerBuffer(),
            };
            req.SetRequestHeader("Authorization", "Bearer " + _accessToken);
            req.SetRequestHeader("Client-Id", clientId);
            req.SetRequestHeader("Content-Type", "application/json");
            yield return req.SendWebRequest();

            bool ok = IsOk(req);
            long code = req.responseCode;
            string body = req.downloadHandler != null ? req.downloadHandler.text : "";
            req.Dispose();

            if (!ok)
            {
                Plugin.Log.LogWarning($"Bot chat send failed ({code}): {body}");
                if (code == 401) // token rejected, force a refresh on the next tick
                    _expiryUtc = DateTime.MinValue;
                yield break;
            }
            // Helix returns 200 even when a message is dropped (e.g. by AutoMod). Surface that.
            if (body.Contains("\"is_sent\":false", StringComparison.Ordinal))
                Plugin.Log.LogWarning("Bot message accepted but not delivered by Twitch: " + body);
        }

        private IEnumerator LoginRoutine()
        {
            _authInProgress = true;
            try
            {
                string clientId = GameClientId();
                if (string.IsNullOrEmpty(clientId))
                {
                    _status = "waiting for the game's Twitch login";
                    Plugin.Log.LogWarning(
                        "Cannot start bot login yet: the game isn't connected to Twitch (no client id). "
                            + "Log into Twitch in-game first, then try again from Settings."
                    );
                    yield break;
                }

                _status = "requesting device code...";
                var device = PostForm(
                    DeviceEndpoint,
                    Form(("client_id", clientId), ("scopes", Scope))
                );
                yield return device.SendWebRequest();
                bool deviceOk = IsOk(device);
                string deviceBody =
                    device.downloadHandler != null ? device.downloadHandler.text : "";
                string deviceErr = Describe(device);
                device.Dispose();
                if (!deviceOk)
                {
                    _status = "device request failed";
                    Plugin.Log.LogError(
                        "Device code request failed: " + deviceErr + " " + deviceBody
                    );
                    yield break;
                }

                var dev = Parse<DeviceResp>(deviceBody);
                if (dev == null || string.IsNullOrEmpty(dev.device_code))
                {
                    _status = "bad device response";
                    Plugin.Log.LogError("Unexpected device code response: " + deviceBody);
                    yield break;
                }

                _status = "awaiting authorization";
                Plugin.Log.LogInfo(
                    $"Bot login: open {dev.verification_uri} in a browser signed into your BOT account "
                        + $"and enter code {dev.user_code}."
                );
                ShowLoginDialog(dev.verification_uri, dev.user_code);

                int interval = Mathf.Max(1, dev.interval);
                float deadline = Time.realtimeSinceStartup + Mathf.Max(30, dev.expires_in);
                while (Time.realtimeSinceStartup < deadline)
                {
                    yield return new WaitForSeconds(interval);

                    var poll = PostForm(
                        TokenEndpoint,
                        Form(
                            ("client_id", clientId),
                            ("scopes", Scope),
                            ("device_code", dev.device_code),
                            ("grant_type", DeviceGrant)
                        )
                    );
                    yield return poll.SendWebRequest();
                    bool pollOk = IsOk(poll);
                    long pollCode = poll.responseCode;
                    string pollBody = poll.downloadHandler != null ? poll.downloadHandler.text : "";
                    poll.Dispose();

                    if (pollOk)
                    {
                        var token = Parse<TokenResp>(pollBody);
                        if (token == null || string.IsNullOrEmpty(token.access_token))
                        {
                            _status = "bad token response";
                            Plugin.Log.LogError("Unexpected token response: " + pollBody);
                            yield break;
                        }
                        ApplyToken(token);
                        yield return _host.StartCoroutine(FetchBotIdentity(clientId));
                        if (string.IsNullOrEmpty(_botUserId))
                        {
                            _status = "couldn't read bot user";
                            yield break;
                        }
                        Save();
                        _status = $"logged in as @{_botLogin}";
                        Plugin.Log.LogInfo(
                            $"Bot account @{_botLogin} ({_botUserId}) is now sending LurkBait chat."
                        );
                        Notify($"Bot @{_botLogin} connected.");
                        yield break;
                    }

                    if (
                        pollCode == 400
                        && pollBody.Contains("authorization_pending", StringComparison.Ordinal)
                    )
                        continue;
                    if (pollBody.Contains("slow_down", StringComparison.Ordinal))
                    {
                        interval++;
                        continue;
                    }
                    _status = "authorization failed";
                    Plugin.Log.LogError("Bot authorization failed: " + pollBody);
                    yield break;
                }

                _status = "login timed out";
                Plugin.Log.LogWarning(
                    "Bot login timed out before authorization; open Settings to retry."
                );
            }
            finally
            {
                _authInProgress = false;
            }
        }

        private IEnumerator RefreshRoutine()
        {
            _refreshInProgress = true;
            try
            {
                string clientId = GameClientId();
                if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(_refreshToken))
                    yield break;

                var req = PostForm(
                    TokenEndpoint,
                    Form(
                        ("client_id", clientId),
                        ("grant_type", "refresh_token"),
                        ("refresh_token", _refreshToken)
                    )
                );
                yield return req.SendWebRequest();
                bool ok = IsOk(req);
                string body = req.downloadHandler != null ? req.downloadHandler.text : "";
                string err = Describe(req);
                req.Dispose();

                if (ok)
                {
                    var token = Parse<TokenResp>(body);
                    if (token != null && !string.IsNullOrEmpty(token.access_token))
                    {
                        ApplyToken(token);
                        if (string.IsNullOrEmpty(_botUserId))
                            yield return _host.StartCoroutine(FetchBotIdentity(clientId));
                        Save();
                        _status = $"logged in as @{_botLogin}";
                        Plugin.Log.LogInfo("Refreshed the bot token.");
                        yield break;
                    }
                }

                // Refresh token is dead: drop everything and require a fresh sign-in.
                Plugin.Log.LogWarning(
                    "Bot token refresh failed; sign in again from Settings. " + err + " " + body
                );
                _accessToken = null;
                _refreshToken = null;
                _expiryUtc = default;
                _status = "session expired - sign in again";
                Notify("Bot session expired - reconnect from Settings.");
            }
            finally
            {
                _refreshInProgress = false;
            }
        }

        private IEnumerator FetchBotIdentity(string clientId)
        {
            var req = UnityWebRequest.Get(UsersEndpoint);
            req.SetRequestHeader("Authorization", "Bearer " + _accessToken);
            req.SetRequestHeader("Client-Id", clientId);
            yield return req.SendWebRequest();
            bool ok = IsOk(req);
            string body = req.downloadHandler != null ? req.downloadHandler.text : "";
            string err = Describe(req);
            req.Dispose();

            if (!ok)
            {
                Plugin.Log.LogError("Could not read the bot's user id: " + err + " " + body);
                yield break;
            }
            var users = Parse<UsersResp>(body);
            if (users?.data != null && users.data.Length > 0)
            {
                _botUserId = users.data[0].id;
                _botLogin = users.data[0].login;
            }
        }

        private void ApplyToken(TokenResp token)
        {
            _accessToken = token.access_token;
            if (!string.IsNullOrEmpty(token.refresh_token))
                _refreshToken = token.refresh_token;
            int ttl = token.expires_in > 0 ? token.expires_in : 3600;
            _expiryUtc = DateTime.UtcNow.AddSeconds(ttl);
        }

        private void Save()
        {
            try
            {
                var stored = new StoredToken
                {
                    access_token = _accessToken,
                    refresh_token = _refreshToken,
                    expires_at_unix = new DateTimeOffset(
                        _expiryUtc,
                        TimeSpan.Zero
                    ).ToUnixTimeSeconds(),
                    user_id = _botUserId,
                    login = _botLogin,
                };
                File.WriteAllText(_tokenPath, JsonConvert.SerializeObject(stored));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not save the bot token: " + e.Message);
            }
        }

        private static void ShowLoginDialog(string uri, string code)
        {
            try
            {
                DialogueUIController.Instance?.ShowDialogue(
                    "Bot Login",
                    "Enter this code at the Twitch page:\n\n"
                        + code
                        + "\n\nSign in as your BOT account and approve.",
                    "Open page",
                    "Close",
                    () => Application.OpenURL(uri),
                    null
                );
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not show the bot login dialog: " + e.Message);
            }
        }

        private static string GameClientId()
        {
            try
            {
                return TwitchConnectorEventSub.Instance?.TwitchClientID;
            }
            catch
            {
                return null;
            }
        }

        private static string GameBroadcasterId()
        {
            try
            {
                var connector = TwitchConnectorEventSub.Instance;
                return connector != null ? connector.UsernameData.id : null;
            }
            catch
            {
                return null;
            }
        }

        private static UnityWebRequest PostForm(string url, string body)
        {
            var req = new UnityWebRequest(url, "POST")
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)),
                downloadHandler = new DownloadHandlerBuffer(),
            };
            req.SetRequestHeader("Content-Type", "application/x-www-form-urlencoded");
            return req;
        }

        private static string Form(params (string Key, string Value)[] fields)
        {
            var sb = new StringBuilder();
            foreach (var (key, value) in fields)
            {
                if (sb.Length > 0)
                    sb.Append('&');
                sb.Append(Uri.EscapeDataString(key))
                    .Append('=')
                    .Append(Uri.EscapeDataString(value));
            }
            return sb.ToString();
        }

        private static bool IsOk(UnityWebRequest req) =>
            req.result == UnityWebRequest.Result.Success;

        private static string Describe(UnityWebRequest req) => req.responseCode + " " + req.error;

        private static T Parse<T>(string json)
            where T : class
        {
            try
            {
                return JsonConvert.DeserializeObject<T>(json);
            }
            catch
            {
                return null;
            }
        }

        private static void Notify(string message)
        {
            try
            {
                NotificationController.Instance?.QueueNotif(message);
            }
            catch
            {
                /* toast is best-effort */
            }
        }

        private sealed class StoredToken
        {
            public string access_token { get; set; }
            public string refresh_token { get; set; }
            public long expires_at_unix { get; set; }
            public string user_id { get; set; }
            public string login { get; set; }
        }

        private sealed class DeviceResp
        {
            public string device_code { get; set; }
            public string user_code { get; set; }
            public string verification_uri { get; set; }
            public int expires_in { get; set; }
            public int interval { get; set; }
        }

        private sealed class TokenResp
        {
            public string access_token { get; set; }
            public string refresh_token { get; set; }
            public int expires_in { get; set; }
            public string token_type { get; set; }
        }

        private sealed class UsersResp
        {
            public UserObj[] data { get; set; }
        }

        private sealed class UserObj
        {
            public string id { get; set; }
            public string login { get; set; }
        }

        private sealed class SendBody
        {
            public string broadcaster_id { get; set; }
            public string sender_id { get; set; }
            public string message { get; set; }
        }
    }
}
