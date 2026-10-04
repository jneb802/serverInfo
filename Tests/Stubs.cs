// Test adapters only. The production project excludes this directory.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace BepInEx
{
    public static class Paths { public static string ConfigPath = ""; }
}
namespace BepInEx.Configuration
{
    public sealed class ConfigEntry<T> { public T Value; public ConfigEntry(T value) { Value = value; } }
    public sealed class ConfigFile
    {
        public readonly Dictionary<string, object> Entries = new();
        public ConfigEntry<T> Bind<T>(string section, string key, T value, string description)
        {
            string name = section + ":" + key;
            if (Entries.TryGetValue(name, out object? entry)) return (ConfigEntry<T>)entry;
            ConfigEntry<T> result = new(value);
            Entries[name] = result;
            return result;
        }
        public void Set<T>(string key, T value) { Entries["PlayerDirectory:" + key] = new ConfigEntry<T>(value); }
    }
}
namespace BepInEx.Logging
{
    public sealed class ManualLogSource
    {
        public readonly List<string> Warnings = new();
        public void LogWarning(string value) { Warnings.Add(value); }
        public void LogInfo(string value) { }
    }
}
namespace UnityEngine
{
    public static class Time { public static float realtimeSinceStartup; }
    public static class JsonUtility
    {
        private static readonly JsonSerializerOptions Options = new() { IncludeFields = true };
        public static string ToJson(object value) { return JsonSerializer.Serialize(value, Options); }
        public static T? FromJson<T>(string value) { return JsonSerializer.Deserialize<T>(value, Options); }
    }
    public sealed class Coroutine { }
    public sealed class MonoBehaviour
    {
        public readonly Queue<IEnumerator> Routines = new();
        public Coroutine StartCoroutine(IEnumerator routine) { Routines.Enqueue(routine); return new Coroutine(); }
        public void Drain()
        {
            while (Routines.Count > 0) Run(Routines.Dequeue());
        }
        private static void Run(IEnumerator routine)
        {
            while (routine.MoveNext()) if (routine.Current is IEnumerator nested) Run(nested);
        }
    }
}
namespace UnityEngine.Networking
{
    public sealed class UploadHandlerRaw
    {
        public byte[] Bytes;
        public UploadHandlerRaw(byte[] bytes) { Bytes = bytes; }
    }
    public sealed class DownloadHandlerBuffer { }
    public sealed class UnityWebRequest : IDisposable
    {
        public enum Result { Success, ConnectionError }
        public static long NextStatus = 200;
        public static Action? DuringSend;
        public static readonly List<string> Bodies = new();
        public UploadHandlerRaw uploadHandler = null!;
        public DownloadHandlerBuffer downloadHandler = null!;
        public int timeout;
        public Result result;
        public long responseCode;
        public UnityWebRequest(string endpoint, string method) { }
        public void SetRequestHeader(string key, string value) { }
        public IEnumerator SendWebRequest()
        {
            Bodies.Add(Encoding.UTF8.GetString(uploadHandler.Bytes));
            DuringSend?.Invoke();
            responseCode = NextStatus;
            result = NextStatus >= 200 && NextStatus < 300 ? Result.Success : Result.ConnectionError;
            yield break;
        }
        public void Dispose() { }
    }
}
public sealed class FakeSocket
{
    public string Host = "76561198000000001";
    public string GetHostName() { return Host; }
}
public struct ZDOID
{
    public bool Missing;
    public bool IsNone() { return Missing; }
}
public sealed class ZNetPeer
{
    public bool Ready = true;
    public string m_playerName = "Viking";
    public FakeSocket m_socket = new();
    public ZDOID m_characterID;
    public bool IsReady() { return Ready; }
}
public sealed class ZNet
{
    public static ZNet? instance;
    public bool Server = true;
    public List<ZNetPeer> Peers = new();
    public bool IsServer() { return Server; }
    public List<ZNetPeer> GetPeers() { return Peers; }
}
public static class ZDOVars { public static int s_playerID = 123; }
public sealed class ZDO
{
    public long CreatorId = 8001;
    public long GetLong(int key) { return CreatorId; }
}
public sealed class ZDOMan
{
    public static ZDOMan? instance;
    public ZDO? Character = new();
    public ZDO? GetZDO(ZDOID id) { return Character; }
}
