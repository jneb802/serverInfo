using HarmonyLib;
using System;

namespace ServerInfo
{
    [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
    internal static class DirectoryPeerInfoPatch
    {
        private static void Postfix(ZRpc rpc)
        {
            ObserveRpc(rpc);
        }

        internal static void ObserveRpc(ZRpc rpc)
        {
            try
            {
                ZNetPeer? peer = ZNet.instance != null ? ZNet.instance.GetPeer(rpc) : null;
                if (peer != null) ServerInfoPlugin.DirectoryReporter?.ObserveLifecycle(peer);
            }
            catch (Exception exception)
            {
                ServerInfoPlugin.Log.LogWarning("Player directory peer observation failed: " + exception.GetType().Name);
            }
        }
    }

    [HarmonyPatch(typeof(ZNet), "RPC_CharacterID")]
    internal static class DirectoryCharacterPatch
    {
        private static void Postfix(ZRpc rpc)
        {
            DirectoryPeerInfoPatch.ObserveRpc(rpc);
        }
    }

    [HarmonyPatch(typeof(ZNet), "Disconnect")]
    internal static class DirectoryDisconnectPatch
    {
        private static void Prefix(ZNetPeer peer)
        {
            try
            {
                ServerInfoPlugin.DirectoryReporter?.ObserveLifecycle(peer);
            }
            catch (Exception exception)
            {
                ServerInfoPlugin.Log.LogWarning("Player directory disconnect observation failed: " + exception.GetType().Name);
            }
        }
    }
}
