using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ServerInfo
{
    // Requests are local files written by the authenticated Discord bot. No
    // client RPC or public HTTP endpoint can submit a restore.
    internal sealed class CharacterRestoreCoordinator
    {
        private readonly ConfigEntry<bool> _enabled;
        private readonly ManualLogSource _log;
        private float _nextPoll;

        internal CharacterRestoreCoordinator(ConfigFile config, ManualLogSource log)
        {
            _enabled = config.Bind("PlayerDashboard", "EnableCharacterRestore", false,
                "Process local Discord character restore requests. Requires compatible ValheimEnforcer. Players must log out first.");
            _log = log;
        }

        internal void Tick()
        {
            if (!_enabled.Value || ZNet.instance == null || !ZNet.instance.IsServer()
                || Time.realtimeSinceStartup < _nextPoll) return;
            _nextPoll = Time.realtimeSinceStartup + 0.5f;
            string root = Path.Combine(Paths.ConfigPath, "ValheimEnforcer");
            string requests = Path.Combine(root, "DashboardRestores");
            if (!Directory.Exists(requests)) return;
            try
            {
                foreach (string operation in Directory.EnumerateDirectories(requests))
                {
                    if (!File.Exists(Path.Combine(operation, "pending"))) continue;
                    RestoreFiles.Process(root, operation, WorldName(), Prepare, Invalidate);
                    _log.LogInfo("Character restore processed: " + Path.GetFileName(operation));
                    break; // At most one restore per tick, including failures.
                }
            }
            catch (Exception exception)
            {
                _log.LogWarning("Character restore failed: " + exception.GetType().Name);
            }
        }

        private static string WorldName()
        {
            object world = AccessTools.Field(typeof(ZNet), "m_world")?.GetValue(ZNet.instance)
                ?? throw new InvalidOperationException("Cannot verify the active world");
            return AccessTools.Field(world.GetType(), "m_name")?.GetValue(world) as string
                ?? throw new InvalidOperationException("Cannot verify the active world name");
        }

        private static Type StoreType() => AccessTools.TypeByName("ValheimEnforcer.modules.character.CharacterStore")
            ?? throw new InvalidOperationException("Compatible Enforcer character storage is not loaded");

        private static MethodInfo Method(string name, Type[] arguments) => AccessTools.Method(StoreType(), name, arguments)
            ?? throw new InvalidOperationException("Required Enforcer storage method is unavailable");

        private static void Prepare(string account, string character)
        {
            // Runs on Unity's main thread. No login or disconnect callback can
            // interleave between this check and replacement/cache invalidation.
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                string identity = peer.m_socket?.GetHostName() ?? "";
                if (identity.StartsWith("Steam_", StringComparison.OrdinalIgnoreCase)) identity = identity.Substring(6);
                // Unknown peers might be this account in the middle of login.
                if (string.IsNullOrWhiteSpace(identity) || identity == account)
                    throw new InvalidOperationException("The player is online or a connection is still being identified. Log out and try again");
            }
            Type config = AccessTools.TypeByName("ValheimEnforcer.ValConfig")
                ?? throw new InvalidOperationException("Enforcer configuration is unavailable");
            ConfigEntry<bool> internalStorage = AccessTools.Field(config, "InternalStorageMode")?.GetValue(null) as ConfigEntry<bool>
                ?? throw new InvalidOperationException("Cannot verify Enforcer storage mode");
            if (internalStorage.Value) throw new InvalidOperationException("Internal Enforcer storage is not supported");
            string path = AccessTools.Field(config, "CharacterFilePath")?.GetValue(null) as string ?? "";
            if (Path.GetFullPath(path) != Path.GetFullPath(Path.Combine(Paths.ConfigPath, "ValheimEnforcer", "Characters")))
                throw new InvalidOperationException("Enforcer uses a different character storage folder");
            // Verify invalidation is available BEFORE changing a file.
            Method("Invalidate", new[] { typeof(string), typeof(string) });
            MethodInfo flush = Method("FlushCharacter", new[] { typeof(string), typeof(string), typeof(TimeSpan), typeof(int) });
            bool drained = (bool)flush.Invoke(null, new object[] { account, character, TimeSpan.FromMilliseconds(250), 5 });
            if (!drained) throw new InvalidOperationException("Enforcer still has pending saves. Wait and try again");
        }

        private static void Invalidate(string account, string character)
        {
            MethodInfo invalidate = Method("Invalidate", new[] { typeof(string), typeof(string) });
            invalidate.Invoke(null, new object[] { account, character });
            invalidate.Invoke(null, new object[] { "Steam_" + account, character });
        }
    }
}
