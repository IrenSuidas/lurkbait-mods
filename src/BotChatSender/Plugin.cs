using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LurkBait.BotChatSender
{
    // Sends LurkBait's chat lines from a separate bot account instead of the streamer's, via the Helix
    // Send Chat Message endpoint. It restores a feature the dev disabled when Twitch retired IRC bot
    // sends. A "Connect bot account" button in the Settings panel drives login. With no bot connected,
    // messages send from the main account like vanilla.
    [BepInPlugin(PluginGuid, "LurkBait Bot Chat Sender", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.irensuidas.lurkbait.botchatsender";

        private const string BotButtonName = "BotChatSenderLoginButton";

        private static readonly AccessTools.FieldRef<SettingsUIController, Button> LoginButtonRef =
            AccessTools.FieldRefAccess<SettingsUIController, Button>("loginButton");

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        private ConfigEntry<bool> _enabled;

        private BotChatClient _client;
        private Button _botButton;
        private TextMeshProUGUI _botButtonLabel;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            _enabled = Config.Bind(
                "General",
                "Enabled",
                true,
                "Route LurkBait's outgoing chat through the bot account once it is signed in. When off - "
                    + "or before a bot signs in - messages send from your main account like vanilla."
            );

            _client = new BotChatClient(this);
            _client.LoadStoredToken();

            new Harmony(PluginGuid).PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo(
                $"Loaded - add a bot from the Settings panel. Routing "
                    + $"{(_enabled.Value ? "enabled" : "disabled")}; {_client.StatusText}."
            );
        }

        private void Update()
        {
            _client.Tick();
            RefreshBotButtonLabel();
        }

        internal bool TryRouteViaBot(string message)
        {
            return _enabled.Value && _client != null && _client.TrySend(message);
        }

        internal void InjectBotButton(SettingsUIController controller)
        {
            try
            {
                Button template = LoginButtonRef(controller);
                if (template == null)
                    return;

                Transform parent = template.transform.parent;
                Transform existing = parent.Find(BotButtonName);
                if (existing != null)
                {
                    CacheBotButton(existing.GetComponent<Button>());
                    return;
                }

                GameObject clone = Object.Instantiate(template.gameObject, parent);
                clone.name = BotButtonName;
                clone.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);

                var button = clone.GetComponent<Button>();
                button.onClick = new Button.ButtonClickedEvent();
                button.interactable = true;
                button.onClick.AddListener(ToggleBotLogin);
                CacheBotButton(button);
            }
            catch (System.Exception e)
            {
                Log.LogWarning("Could not add the bot login button: " + e.Message);
            }
        }

        private void CacheBotButton(Button button)
        {
            _botButton = button;
            _botButtonLabel =
                button != null ? button.GetComponentInChildren<TextMeshProUGUI>() : null;
            RefreshBotButtonLabel();
        }

        private void RefreshBotButtonLabel()
        {
            if (_botButton != null && _botButtonLabel != null)
                _botButtonLabel.text = _client.ButtonLabel;
        }

        private void ToggleBotLogin()
        {
            if (_client.Ready)
                _client.Logout();
            else
                _client.StartLogin();
        }
    }
}
