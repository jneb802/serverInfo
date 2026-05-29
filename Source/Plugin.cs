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

        private FileSystemWatcher? _configWatcher;
        private Coroutine? _heartbeatCoroutine;
        private bool _started;

        public void Awake()
        {
            EndpointUrl = Config.Bind("General", "EndpointUrl", "",
                "URL to POST game state to (e.g. http://your-server:8099/api/game-state)");
            ApiKey = Config.Bind("General", "ApiKey", "",
                "API key sent in X-API-Key header");
            IntervalSeconds = Config.Bind("General", "IntervalSeconds", 30,
                "Seconds between heartbeat POSTs");

            SetupWatcher();
        }

        private void Update()
        {
            if (_started) return;

            try
            {
                var znet = ZNet.instance;
                if (znet == null || !znet.IsServer()) return;
                if (string.IsNullOrEmpty(EndpointUrl.Value) || string.IsNullOrEmpty(ApiKey.Value)) return;

                _started = true;
                _heartbeatCoroutine = StartCoroutine(HeartbeatLoop());
                Log.LogInfo("Heartbeat loop started");
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to start heartbeat loop: {ex}");
            }
        }

        private void OnApplicationQuit()
        {
            StopHeartbeat();
            DisposeConfigWatcher();
        }

        private void OnDestroy()
        {
            StopHeartbeat();
            DisposeConfigWatcher();
        }

        private void StopHeartbeat()
        {
            if (_heartbeatCoroutine == null) return;

            try
            {
                StopCoroutine(_heartbeatCoroutine);
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Failed to stop heartbeat coroutine cleanly: {ex}");
            }
            finally
            {
                _heartbeatCoroutine = null;
            }
        }

        private static IEnumerator HeartbeatLoop()
        {
            yield return new WaitForSeconds(5f);

            int consecutiveFailures = 0;
            const int backoffThreshold = 3;
            const float backoffInterval = 60f;

            while (true)
            {
                bool success = false;

                if (IsServerReady())
                {
                    if (TryBuildPayload(out string json))
                    {
                        yield return PostGameState(json, result => success = result);
                    }
                    else
                    {
                        Log.LogWarning("Skipping heartbeat because server state is not ready");
                    }

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
                    : Math.Max(5, IntervalSeconds.Value);
                yield return new WaitForSeconds(interval);
            }
        }

        private static bool IsServerReady()
        {
            try
            {
                var znet = ZNet.instance;
                return znet != null && znet.IsServer();
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Unable to read server state: {ex}");
                return false;
            }
        }

        private static bool TryBuildPayload(out string json)
        {
            json = "{}";

            try
            {
                var znet = ZNet.instance;
                var envMan = EnvMan.instance;
                if (znet == null || envMan == null)
                {
                    Log.LogWarning("Cannot build heartbeat payload before ZNet and EnvMan are ready");
                    return false;
                }

                var players = new List<string>();
                var peers = znet.GetPeers();
                if (peers != null)
                {
                    foreach (var peer in peers)
                    {
                        if (peer == null) continue;
                        if (peer.IsReady() && !string.IsNullOrEmpty(peer.m_playerName))
                            players.Add(peer.m_playerName);
                    }
                }

                int day = envMan.GetDay(znet.GetTimeSeconds());
                float fraction = envMan.GetDayFraction();
                float totalHours = fraction * 24f;
                int hour = (int)totalHours;
                int minute = (int)((totalHours - hour) * 60f);
                string gameTime = $"{hour:D2}:{minute:D2}";
                bool isDay = EnvMan.IsDay();

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

                json = sb.ToString();
                return true;
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to build heartbeat payload: {ex}");
                return false;
            }
        }

        private static string EscapeJson(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static IEnumerator PostGameState(string json, Action<bool> onComplete)
        {
            UnityWebRequest? request = null;
            UnityWebRequestAsyncOperation? operation = null;

            try
            {
                if (string.IsNullOrEmpty(EndpointUrl.Value))
                {
                    Log.LogWarning("Skipping heartbeat because EndpointUrl is empty");
                    onComplete(false);
                    yield break;
                }

                request = new UnityWebRequest(EndpointUrl.Value, "POST")
                {
                    uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
                    downloadHandler = new DownloadHandlerBuffer(),
                    timeout = 10
                };
                request.SetRequestHeader("X-API-Key", ApiKey.Value ?? "");
                request.SetRequestHeader("Content-Type", "application/json");
                operation = request.SendWebRequest();
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to construct or send heartbeat request: {ex}");
                onComplete(false);
                DisposeRequest(request);
                yield break;
            }

            try
            {
                yield return operation;

                try
                {
                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        onComplete(true);
                    }
                    else
                    {
                        Log.LogWarning($"POST failed: {request.error}");
                        onComplete(false);
                    }
                }
                catch (Exception ex)
                {
                    Log.LogError($"Failed while processing heartbeat response: {ex}");
                    onComplete(false);
                }
            }
            finally
            {
                DisposeRequest(request);
            }
        }

        private static void DisposeRequest(UnityWebRequest? request)
        {
            if (request == null) return;

            try
            {
                request.Dispose();
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Failed to dispose heartbeat request: {ex}");
            }
        }

        private DateTime _lastReloadTime;
        private const long RELOAD_DELAY = 10000000; // One second

        private void SetupWatcher()
        {
            _lastReloadTime = DateTime.Now;

            try
            {
                DisposeConfigWatcher();

                _configWatcher = new FileSystemWatcher(BepInEx.Paths.ConfigPath, ConfigFileName);
                _configWatcher.Changed += ReadConfigValues;
                _configWatcher.Created += ReadConfigValues;
                _configWatcher.Renamed += ReadConfigValues;
                _configWatcher.IncludeSubdirectories = true;
                _configWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to create config watcher for {ConfigFileName}: {ex}");
                DisposeConfigWatcher();
            }
        }

        private void DisposeConfigWatcher()
        {
            if (_configWatcher == null) return;

            try
            {
                _configWatcher.EnableRaisingEvents = false;
                _configWatcher.Changed -= ReadConfigValues;
                _configWatcher.Created -= ReadConfigValues;
                _configWatcher.Renamed -= ReadConfigValues;
                _configWatcher.Dispose();
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Failed to dispose config watcher cleanly: {ex}");
            }
            finally
            {
                _configWatcher = null;
            }
        }

        private void ReadConfigValues(object sender, FileSystemEventArgs e)
        {
            var now = DateTime.Now;
            var time = now.Ticks - _lastReloadTime.Ticks;
            if (!File.Exists(ConfigFileFullPath) || time < RELOAD_DELAY) return;

            try
            {
                Config.Reload();
                _lastReloadTime = now;
                Log.LogInfo("Configuration reloaded");
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to reload {ConfigFileName}: {ex}");
            }
        }
    }
}
