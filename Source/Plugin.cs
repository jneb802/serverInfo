using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Networking;
using HarmonyLib;

namespace ServerInfo
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class ServerInfoPlugin : BaseUnityPlugin
    {
        private const string ModName = "ServerInfo";
        private const string ModVersion = "1.0.0";
        private const string Author = "warpalicious";
        private const string ModGUID = Author + "." + ModName;
        private static readonly string ConfigFileName = ModGUID + ".cfg";
        private static readonly string ConfigFileFullPath =
            BepInEx.Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;

        public static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource(ModName);

        private static ConfigEntry<string> EndpointUrl = null!;
        private static ConfigEntry<string> ApiKey = null!;
        private static ConfigEntry<int> IntervalSeconds = null!;

        private Coroutine? _heartbeatCoroutine;
        private bool _started;
        private PlayerDirectoryReporter? _directoryReporter;
        internal static PlayerDirectoryReporter? DirectoryReporter;
        private Harmony? _directoryHarmony;

        public void Awake()
        {
            EndpointUrl = Config.Bind("General", "EndpointUrl", "",
                "URL to POST game state to (e.g. http://your-server:8099/api/game-state)");
            ApiKey = Config.Bind("General", "ApiKey", "",
                "API key sent in X-API-Key header");
            IntervalSeconds = Config.Bind("General", "IntervalSeconds", 30,
                "Seconds between heartbeat POSTs");

            SetupWatcher();
            _directoryReporter = new PlayerDirectoryReporter(Config, Log);
            DirectoryReporter = _directoryReporter;
            try
            {
                _directoryHarmony = new Harmony(ModGUID + ".player-directory");
                _directoryHarmony.PatchAll(typeof(ServerInfoPlugin).Assembly);
            }
            catch (Exception exception)
            {
                _directoryHarmony?.UnpatchSelf();
                _directoryReporter = null;
                DirectoryReporter = null;
                Log.LogWarning("Player directory hooks could not load: " + exception.GetType().Name);
            }
        }

        private void Update()
        {
            _directoryReporter?.Tick(this);
            if (_started) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (string.IsNullOrEmpty(EndpointUrl.Value) || string.IsNullOrEmpty(ApiKey.Value)) return;

            _started = true;
            _heartbeatCoroutine = StartCoroutine(HeartbeatLoop());
            Log.LogInfo("Heartbeat loop started");
        }

        private void OnApplicationQuit()
        {
            _directoryReporter?.SavePending();
            if (_heartbeatCoroutine != null)
            {
                StopCoroutine(_heartbeatCoroutine);
                _heartbeatCoroutine = null;
            }
        }

        private void OnDestroy()
        {
            _directoryReporter?.SavePending();
            DirectoryReporter = null;
            _directoryHarmony?.UnpatchSelf();
        }

        private static IEnumerator HeartbeatLoop()
        {
            // Let ZNet fully initialize
            yield return new WaitForSeconds(5f);

            int consecutiveFailures = 0;
            const int backoffThreshold = 3;
            const float backoffInterval = 60f;

            while (true)
            {
                if (ZNet.instance != null && ZNet.instance.IsServer())
                {
                    string json = BuildPayload();
                    bool success = false;
                    yield return PostGameState(json, result => success = result);

                    if (success)
                    {
                        consecutiveFailures = 0;
                    }
                    else
                    {
                        consecutiveFailures++;
                        if (consecutiveFailures == backoffThreshold)
                            Log.LogWarning($"Backing off to {backoffInterval}s after {backoffThreshold} failures");
                    }
                }

                float interval = consecutiveFailures >= backoffThreshold
                    ? backoffInterval
                    : IntervalSeconds.Value;
                yield return new WaitForSeconds(interval);
            }
        }

        private static string BuildPayload()
        {
            // Collect players
            var players = new List<string>();
            var peers = ZNet.instance.GetPeers();
            if (peers != null)
            {
                foreach (var peer in peers)
                {
                    if (peer.IsReady() && !string.IsNullOrEmpty(peer.m_playerName))
                        players.Add(peer.m_playerName);
                }
            }

            // Day number
            int day = EnvMan.instance.GetDay(ZNet.instance.GetTimeSeconds());

            // Game time HH:MM
            float fraction = EnvMan.instance.GetDayFraction();
            float totalHours = fraction * 24f;
            int hour = (int)totalHours;
            int minute = (int)((totalHours - hour) * 60f);
            string gameTime = $"{hour:D2}:{minute:D2}";

            // Day/night
            bool isDay = EnvMan.IsDay();

            // Build JSON manually (no Newtonsoft dependency)
            var sb = new StringBuilder();
            sb.Append("{\"players\":[");
            for (int i = 0; i < players.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("\"");
                sb.Append(EscapeJson(players[i]));
                sb.Append("\"");
            }
            sb.Append("],");
            sb.Append($"\"day\":{day},");
            sb.Append($"\"game_time\":\"{gameTime}\",");
            sb.Append($"\"is_day\":{(isDay ? "true" : "false")}");
            sb.Append("}");

            return sb.ToString();
        }

        private static string EscapeJson(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static IEnumerator PostGameState(string json, Action<bool> onComplete)
        {
            var request = new UnityWebRequest(EndpointUrl.Value, "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("X-API-Key", ApiKey.Value);
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = 10;

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                onComplete(true);
            }
            else
            {
                Log.LogWarning($"POST failed: {request.error}");
                onComplete(false);
            }

            request.Dispose();
        }

        // ── Config file watcher ──

        private DateTime _lastReloadTime;
        private const long RELOAD_DELAY = 10000000; // One second

        private void SetupWatcher()
        {
            _lastReloadTime = DateTime.Now;
            FileSystemWatcher watcher = new(BepInEx.Paths.ConfigPath, ConfigFileName);
            watcher.Changed += ReadConfigValues;
            watcher.Created += ReadConfigValues;
            watcher.Renamed += ReadConfigValues;
            watcher.IncludeSubdirectories = true;
            watcher.EnableRaisingEvents = true;
        }

        private void ReadConfigValues(object sender, FileSystemEventArgs e)
        {
            var now = DateTime.Now;
            var time = now.Ticks - _lastReloadTime.Ticks;
            if (!File.Exists(ConfigFileFullPath) || time < RELOAD_DELAY) return;

            try
            {
                Config.Reload();
                Log.LogInfo("Configuration reloaded");
            }
            catch
            {
                Log.LogError($"Failed to reload {ConfigFileName}");
                return;
            }

            _lastReloadTime = now;
        }
    }
}
