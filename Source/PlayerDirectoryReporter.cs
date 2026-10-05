using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Networking;

namespace ServerInfo
{
    // Platform accounts come from the server connection, not from client input.
    internal sealed class PlayerDirectoryReporter
    {
        private readonly ConfigEntry<bool> _enabled;
        private readonly ConfigEntry<string> _endpoint;
        private readonly ConfigEntry<string> _apiKey;
        private readonly ConfigEntry<int> _interval;
        private readonly ManualLogSource _log;
        private readonly string _pendingPath;
        private readonly Dictionary<string, Observation> _pending = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Observation> _latest = new(StringComparer.Ordinal);
        private float _nextCollect;
        private float _nextSend;
        private bool _sending;
        private bool _loaded;
        private bool _dirty;
        private bool _storageWarning;
        private string _queueEndpoint = "";

        internal PlayerDirectoryReporter(ConfigFile config, ManualLogSource log)
        {
            _enabled = config.Bind("PlayerDirectory", "Enabled", false,
                "Record player identities for the admin directory. Disabled until explicitly configured.");
            _endpoint = config.Bind("PlayerDirectory", "EndpointUrl", "",
                "Dedicated bot endpoint ending in /api/player-directory. Use the test bot during validation.");
            _apiKey = config.Bind("PlayerDirectory", "ApiKey", "",
                "Dedicated player directory API key. Do not reuse client-visible keys.");
            _interval = config.Bind("PlayerDirectory", "IntervalSeconds", 30,
                "Seconds between presence observations and delivery attempts. Minimum 5 seconds.");
            _log = log;
            _pendingPath = Path.Combine(Paths.ConfigPath, "ServerInfo", "player-directory-pending.json");
        }

        internal void Tick(MonoBehaviour owner)
        {
            if (!_enabled.Value || string.IsNullOrWhiteSpace(_endpoint.Value) ||
                string.IsNullOrWhiteSpace(_apiKey.Value) || ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return;
            }

            if (!_loaded)
            {
                _loaded = true;
                LoadPending();
            }
            // A config change must not send one server's saved observations to another endpoint.
            if (!string.Equals(_queueEndpoint, _endpoint.Value, StringComparison.Ordinal))
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (now >= _nextCollect)
            {
                _nextCollect = now + 1f;
                try
                {
                    Collect();
                    if (_dirty) SavePending();
                }
                catch (Exception exception)
                {
                    _log.LogWarning("Player directory collection failed: " + exception.GetType().Name);
                }
            }
            if (!_sending && !_dirty && _pending.Count > 0 && now >= _nextSend)
            {
                _nextSend = now + Math.Max(5, _interval.Value);
                _sending = true;
                owner.StartCoroutine(SendBatch());
            }
        }

        private void Collect()
        {
            ZNet? network = ZNet.instance;
            if (network == null) return;
            List<ZNetPeer> peers = network.GetPeers();
            if (peers == null) return;
            DateTime now = DateTime.UtcNow;
            foreach (ZNetPeer peer in peers)
            {
                ObservePeer(peer, now, false);
            }
        }

        internal void ObserveLifecycle(ZNetPeer peer)
        {
            if (!_enabled.Value || !_loaded || ZNet.instance == null || !ZNet.instance.IsServer() ||
                !string.Equals(_queueEndpoint, _endpoint.Value, StringComparison.Ordinal)) return;
            try
            {
                ObservePeer(peer, DateTime.UtcNow, true);
                SavePending();
            }
            catch (Exception exception)
            {
                _log.LogWarning("Player directory lifecycle observation failed: " + exception.GetType().Name);
            }
        }

        private void ObservePeer(ZNetPeer peer, DateTime now, bool force)
        {
            if (peer == null || !peer.IsReady() || peer.m_socket == null) return;
            string name = peer.m_playerName?.Trim() ?? "";
            string account = peer.m_socket.GetHostName()?.Trim() ?? "";
            if (name.Length == 0 || name.Length > 256 || !IsPlatformAccount(account)) return;

            // The player ZDO's playerID is the persistent character/creator ID.
            // The peer UID and character ZDOID are session/object IDs and must not be used here.
            string creatorId = "";
            if (ZDOMan.instance != null && !peer.m_characterID.IsNone())
            {
                ZDO? character = ZDOMan.instance.GetZDO(peer.m_characterID);
                if (character != null)
                {
                    long value = character.GetLong(ZDOVars.s_playerID);
                    if (value != 0L) creatorId = value.ToString(CultureInfo.InvariantCulture);
                }
            }

            string key = account + ":" + creatorId;
            if (!force && _latest.TryGetValue(key, out Observation? previous) && previous != null && previous.character_name == name &&
                DateTime.TryParse(previous.observed_at_utc, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTime observed) &&
                (now - observed.ToUniversalTime()).TotalSeconds < Math.Max(5, _interval.Value))
            {
                return;
            }
            Observation observation = new()
            {
                account_id = account,
                character_name = name,
                creator_id = creatorId,
                observed_at_utc = now.ToString("O", CultureInfo.InvariantCulture)
            };
            _latest[key] = observation;
            _pending[key] = observation;
            _dirty = true;
        }

        internal static bool IsPlatformAccount(string account)
        {
            if (string.IsNullOrWhiteSpace(account) || account.Length > 100) return false;
            string externalId = account.StartsWith("Steam_", StringComparison.OrdinalIgnoreCase)
                ? account.Substring(6) : account;
            if (externalId.Length == 17 && ulong.TryParse(externalId, NumberStyles.None,
                    CultureInfo.InvariantCulture, out ulong steamId) && steamId > 0)
            {
                return true;
            }
            if (!account.StartsWith("PlayFab_", StringComparison.OrdinalIgnoreCase) || account.Length <= 8)
            {
                return false;
            }
            foreach (char value in account.Substring(8))
            {
                if (!((value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z') ||
                    (value >= '0' && value <= '9'))) return false;
            }
            return true;
        }

        private IEnumerator SendBatch()
        {
            try
            {
                List<Observation> batch = new();
                foreach (Observation observation in _pending.Values)
                {
                    batch.Add(observation);
                    if (batch.Count == 256) break;
                }
                ObservationBatch payload = new() { players = batch.ToArray() };
                using UnityWebRequest request = new(_endpoint.Value, "POST")
                {
                    uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(Serialize(payload))),
                    downloadHandler = new DownloadHandlerBuffer(),
                    timeout = 10
                };
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("X-API-Key", _apiKey.Value);
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success && request.responseCode >= 200 && request.responseCode < 300)
                {
                    foreach (Observation observation in batch)
                    {
                        string key = observation.account_id + ":" + observation.creator_id;
                        if (_pending.TryGetValue(key, out Observation? current) && ReferenceEquals(current, observation))
                        {
                            _pending.Remove(key);
                        }
                    }
                    _dirty = true;
                    SavePending();
                }
                else
                {
                    // Do not log response bodies, URLs, credentials, or player identifiers.
                    _log.LogWarning("Player directory delivery failed (HTTP " + request.responseCode + "). Saved records will retry.");
                }
            }
            finally
            {
                _sending = false;
            }
        }

        private void LoadPending()
        {
            _queueEndpoint = _endpoint.Value;
            if (!File.Exists(_pendingPath)) return;
            try
            {
                SavedQueue? saved = Deserialize<SavedQueue>(File.ReadAllText(_pendingPath));
                if (saved == null || saved.players == null) throw new InvalidDataException();
                _queueEndpoint = saved.endpoint;
                if (!string.Equals(_queueEndpoint, _endpoint.Value, StringComparison.Ordinal))
                {
                    _log.LogWarning("Player directory endpoint differs from the saved queue. Resolve the queue before changing endpoints.");
                    return;
                }
                foreach (Observation observation in saved.players)
                {
                    if (observation == null || !IsPlatformAccount(observation.account_id)) throw new InvalidDataException();
                    _pending[observation.account_id + ":" + observation.creator_id] = observation;
                }
                _log.LogInfo("Loaded " + _pending.Count + " pending player directory observations.");
            }
            catch (Exception exception)
            {
                // Preserve a damaged file for operator recovery instead of overwriting it.
                _queueEndpoint = "";
                _log.LogWarning("Player directory queue could not load: " + exception.GetType().Name + ". Collection is paused.");
            }
        }

        internal void SavePending()
        {
            if (!_loaded || !_dirty || !string.Equals(_queueEndpoint, _endpoint.Value, StringComparison.Ordinal)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_pendingPath)!);
                SavedQueue saved = new() { endpoint = _queueEndpoint, players = new List<Observation>(_pending.Values).ToArray() };
                string temporaryPath = _pendingPath + ".tmp";
                File.WriteAllText(temporaryPath, Serialize(saved));
                if (File.Exists(_pendingPath)) File.Replace(temporaryPath, _pendingPath, null);
                else File.Move(temporaryPath, _pendingPath);
                _dirty = false;
                _storageWarning = false;
            }
            catch (Exception exception)
            {
                if (!_storageWarning)
                {
                    _storageWarning = true;
                    _log.LogWarning("Player directory queue could not save: " + exception.GetType().Name + ". Delivery is paused until storage recovers.");
                }
            }
        }

        private static string Serialize<T>(T value)
        {
            using MemoryStream stream = new();
            new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static T? Deserialize<T>(string value) where T : class
        {
            using MemoryStream stream = new(Encoding.UTF8.GetBytes(value));
            return new DataContractJsonSerializer(typeof(T)).ReadObject(stream) as T;
        }

        [DataContract]
        internal sealed class Observation
        {
            [DataMember]
            public string account_id = "";
            [DataMember]
            public string character_name = "";
            [DataMember]
            public string creator_id = "";
            [DataMember]
            public string observed_at_utc = "";
        }

        [DataContract]
        private sealed class ObservationBatch
        {
            [DataMember]
            public Observation[] players = Array.Empty<Observation>();
        }

        [DataContract]
        private sealed class SavedQueue
        {
            [DataMember]
            public string endpoint = "";
            [DataMember]
            public Observation[] players = Array.Empty<Observation>();
        }
    }
}
