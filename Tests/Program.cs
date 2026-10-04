using System;
using System.IO;
using System.Text.Json;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ServerInfo;
using UnityEngine;
using UnityEngine.Networking;

internal static class Program
{
    private static int _passed;
    private static void Main()
    {
        Test("account IDs reject IP addresses and session IDs", AccountIds);
        Test("disabled reporter and client do not collect or send", DisabledAndClient);
        Test("failed deliveries survive restart and retry", RetryAfterRestart);
        Test("creator ID comes from character data and late data is added", LateCreator);
        Test("new observation during delivery is retained", ConcurrentObservation);
        Test("corrupt queue is preserved and collection pauses", CorruptQueue);
        Test("endpoint change cannot send saved data to another bot", EndpointChange);
        Test("short session captured through lifecycle observation", ShortSession);
        Console.WriteLine($"Passed {_passed} reporter tests. Game and transport adapters are simulated.");
    }

    private static void Test(string name, Action test)
    {
        string root = Path.Combine(Path.GetTempPath(), "player-directory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Paths.ConfigPath = root;
        Time.realtimeSinceStartup = 0;
        ZNet.instance = new ZNet();
        ZDOMan.instance = new ZDOMan();
        UnityWebRequest.Bodies.Clear();
        UnityWebRequest.NextStatus = 200;
        UnityWebRequest.DuringSend = null;
        try { test(); _passed++; Console.WriteLine("PASS: " + name); }
        finally { Directory.Delete(root, true); }
    }

    private static void Check(bool result, string message)
    {
        if (!result) throw new Exception(message);
    }

    private static ConfigFile Config(bool enabled = true, string endpoint = "http://127.0.0.1:8099/api/player-directory")
    {
        ConfigFile config = new();
        config.Set("Enabled", enabled);
        config.Set("EndpointUrl", endpoint);
        config.Set("ApiKey", "test-only-key");
        config.Set("IntervalSeconds", 5);
        return config;
    }

    private static string QueuePath => Path.Combine(Paths.ConfigPath, "ServerInfo", "player-directory-pending.json");
    private static int PendingCount()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(QueuePath));
        return document.RootElement.GetProperty("players").GetArrayLength();
    }

    private static void AccountIds()
    {
        foreach (string valid in new[] { "76561198000000001", "Steam_76561198000000001", "PlayFab_A12B" })
            Check(PlayerDirectoryReporter.IsPlatformAccount(valid), "Rejected platform ID");
        foreach (string invalid in new[] { "127.0.0.1", "12345", "Steam_123", "PlayFab_", "PlayFab_bad/id" })
            Check(!PlayerDirectoryReporter.IsPlatformAccount(invalid), "Accepted non-account ID");
    }

    private static void DisabledAndClient()
    {
        ZNet.instance!.Peers.Add(new ZNetPeer());
        MonoBehaviour owner = new();
        new PlayerDirectoryReporter(Config(false), new ManualLogSource()).Tick(owner);
        Check(owner.Routines.Count == 0 && !File.Exists(QueuePath), "Disabled feature performed work");
        ZNet.instance.Server = false;
        new PlayerDirectoryReporter(Config(), new ManualLogSource()).Tick(owner);
        Check(owner.Routines.Count == 0 && !File.Exists(QueuePath), "Client collected server identities");
    }

    private static void RetryAfterRestart()
    {
        ZNet.instance!.Peers.Add(new ZNetPeer());
        MonoBehaviour owner = new();
        UnityWebRequest.NextStatus = 503;
        new PlayerDirectoryReporter(Config(), new ManualLogSource()).Tick(owner);
        owner.Drain();
        Check(PendingCount() == 1, "Failure discarded an observation");
        string first = UnityWebRequest.Bodies[0];
        ZNet.instance.Peers.Clear();
        UnityWebRequest.NextStatus = 200;
        Time.realtimeSinceStartup = 10;
        new PlayerDirectoryReporter(Config(), new ManualLogSource()).Tick(owner);
        owner.Drain();
        Check(UnityWebRequest.Bodies.Count == 2 && UnityWebRequest.Bodies[1] == first, "Restart changed original observation");
        Check(PendingCount() == 0, "Acknowledged observation stayed pending");
    }

    private static void LateCreator()
    {
        ZNetPeer peer = new();
        peer.m_characterID = new ZDOID { Missing = true };
        ZNet.instance!.Peers.Add(peer);
        PlayerDirectoryReporter reporter = new(Config(), new ManualLogSource());
        MonoBehaviour owner = new();
        UnityWebRequest.NextStatus = 503;
        reporter.Tick(owner);
        owner.Drain();
        using JsonDocument first = JsonDocument.Parse(UnityWebRequest.Bodies[0]);
        Check(first.RootElement.GetProperty("players")[0].GetProperty("creator_id").GetString() == "", "Fabricated creator ID");
        peer.m_characterID = new ZDOID();
        reporter.ObserveLifecycle(peer);
        Check(PendingCount() == 2, "Late character data was lost");
        string queue = File.ReadAllText(QueuePath);
        Check(queue.Contains("8001"), "Creator ID did not come from the character ZDO");
    }

    private static void ConcurrentObservation()
    {
        ZNetPeer peer = new();
        ZNet.instance!.Peers.Add(peer);
        PlayerDirectoryReporter reporter = new(Config(), new ManualLogSource());
        MonoBehaviour owner = new();
        reporter.Tick(owner);
        UnityWebRequest.DuringSend = () => { peer.m_playerName = "Renamed"; reporter.ObserveLifecycle(peer); };
        owner.Drain();
        Check(PendingCount() == 1 && File.ReadAllText(QueuePath).Contains("Renamed"), "Acknowledgment deleted newer data");
    }

    private static void CorruptQueue()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(QueuePath)!);
        File.WriteAllText(QueuePath, "broken-json");
        ZNet.instance!.Peers.Add(new ZNetPeer());
        MonoBehaviour owner = new();
        new PlayerDirectoryReporter(Config(), new ManualLogSource()).Tick(owner);
        Check(File.ReadAllText(QueuePath) == "broken-json" && owner.Routines.Count == 0, "Damaged queue overwritten");
    }

    private static void EndpointChange()
    {
        ZNet.instance!.Peers.Add(new ZNetPeer());
        UnityWebRequest.NextStatus = 503;
        MonoBehaviour owner = new();
        new PlayerDirectoryReporter(Config(), new ManualLogSource()).Tick(owner);
        owner.Drain();
        string queue = File.ReadAllText(QueuePath);
        Time.realtimeSinceStartup = 10;
        new PlayerDirectoryReporter(Config(endpoint: "http://different-test-bot/api/player-directory"), new ManualLogSource()).Tick(owner);
        Check(owner.Routines.Count == 0 && File.ReadAllText(QueuePath) == queue, "Queue crossed endpoints");
    }

    private static void ShortSession()
    {
        PlayerDirectoryReporter reporter = new(Config(), new ManualLogSource());
        MonoBehaviour owner = new();
        reporter.Tick(owner); // Initialize before a player connects.
        reporter.ObserveLifecycle(new ZNetPeer());
        Check(PendingCount() == 1, "Short session was not saved");
        Time.realtimeSinceStartup = 10;
        reporter.Tick(owner);
        owner.Drain();
        Check(UnityWebRequest.Bodies.Count == 1 && PendingCount() == 0, "Lifecycle record not delivered");
    }
}
