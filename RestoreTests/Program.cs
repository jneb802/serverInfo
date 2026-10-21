using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ServerInfo;

internal static class Program
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static void Main(string[] args)
    {
        // Optional isolated fixture runner for the Python-to-C# contract test.
        // This executable is never packaged in the game plugin.
        if (args.Length == 3)
        {
            RestoreFiles.Process(args[0], args[1], args[2], (_, _) => { }, (_, _) => { });
            return;
        }
        foreach (string scenario in new[] { "success", "online", "pending", "stale", "expired", "world", "changed-backup", "invalidate-failure" })
            Run(scenario);
        Console.WriteLine("8 character restore transaction scenarios passed.");
    }

    private static void Run(string scenario)
    {
        string root = Path.Combine(Path.GetTempPath(), "dashboard-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string folder = Path.Combine(root, "Characters", "76561198000000001");
            Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, "Viking.yaml");
            byte[] current = Encoding.UTF8.GetBytes("name: Viking\nhostID: 76561198000000001\nsaveSequence: 100\nplayerItems: []\n");
            byte[] backup = Encoding.UTF8.GetBytes("name: Viking\nhostID: 76561198000000001\nsaveSequence: 10\nplayerItems: []\n");
            File.WriteAllBytes(target, current);
            string operation = Path.Combine(root, "DashboardRestores", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(operation);
            File.WriteAllBytes(Path.Combine(operation, "backup.yaml"), backup);
            File.WriteAllText(Path.Combine(operation, "request.json"), JsonSerializer.Serialize(new
            {
                account = "76561198000000001", character = "Viking", world = "test",
                before_sha256 = scenario == "stale" ? "wrong" : Hash(current),
                backup_sha256 = scenario == "changed-backup" ? "wrong" : Hash(backup),
                expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (scenario == "expired" ? -60 : 60)
            }));
            File.WriteAllText(Path.Combine(operation, "pending"), "ready");
            bool invalidated = false;
            RestoreFiles.Process(root, operation, scenario == "world" ? "other" : "test",
                (_, _) => {
                    if (scenario == "online" || scenario == "pending") throw new InvalidOperationException(scenario);
                }, (_, _) => {
                    if (scenario == "invalidate-failure") throw new InvalidOperationException("cache invalidation failed");
                    invalidated = true;
                });
            using JsonDocument result = JsonDocument.Parse(File.ReadAllText(Path.Combine(operation, "result.json")));
            Check(result.RootElement.GetProperty("success").GetBoolean() == (scenario == "success"), scenario + " result");
            if (scenario == "success" || scenario == "invalidate-failure")
            {
                Check(File.ReadAllText(target).Contains("saveSequence: 101"), "recovery sequence must advance");
                Check(Hash(File.ReadAllBytes(Path.Combine(operation, "before.yaml"))) == Hash(current), "preimage preserved");
                Check(invalidated == (scenario == "success"), "invalidation");
            }
            else Check(Hash(File.ReadAllBytes(target)) == Hash(current), scenario + " must preserve current");
            Check(!File.Exists(Path.Combine(operation, "pending")), "request must be consumed");
            bool replayRefused = false;
            try { RestoreFiles.Process(root, operation, "test", (_, _) => { }, (_, _) => { }); }
            catch (FileNotFoundException) { replayRefused = true; }
            Check(replayRefused, "must not replay a request");
        }
        finally { Directory.Delete(root, true); }
    }
}
