using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppSharp;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SocialSpaceModMenu
{
    /// <summary>
    /// The mod's way to tell other mod users things: extra keys in this player's client data, which the game
    /// relays to everyone in the instance (players without the mod ignore keys they don't know). Every send
    /// carries the player's current data plus the mod's keys, so a server that replaces rather than merges
    /// still keeps everything the game set. Sends are coalesced to at most one per MinInterval.
    /// </summary>
    internal static class ClientDataChannel
    {
        private const float MinInterval = 0.15f;

        private static readonly Dictionary<string, string> Pending = new();
        private static float _lastSend = -10f;

        /// <summary>Queue key=value for the next send (the latest value per key wins).</summary>
        public static void Set(string key, string value) => Pending[key] = value ?? "";

        /// <summary>Called every frame; sends whatever is queued once the interval allows.</summary>
        public static void Flush()
        {
            if (Pending.Count == 0 || Time.unscaledTime - _lastSend < MinInterval || !SharpNetwork.InInstance)
                return;
            var data = new Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Object>();
            if (SharpNetwork.ClientData != null && SharpNetwork.ClientData.TryGetValue(SharpNetwork.SessionId, out var current) && current != null)
                foreach (var kv in current)
                    if (!Pending.ContainsKey(kv.Key))
                        data[kv.Key] = kv.Value;
            foreach (var kv in Pending)
                data[kv.Key] = (Il2CppSystem.String)kv.Value;
            SharpNetwork.SetClientData(data);
            Pending.Clear();
            _lastSend = Time.unscaledTime;
        }

        /// <summary>This player's own AccountClientData, or null before it spawns.</summary>
        public static AccountClientData Mine()
        {
            foreach (var obj in Object.FindObjectsByType(Il2CppType.Of<AccountClientData>(), FindObjectsSortMode.None))
            {
                var acc = obj.TryCast<AccountClientData>();
                if (acc != null && acc.sharpView != null && acc.sharpView.isMine)
                    return acc;
            }
            return null;
        }

        /// <summary>Everyone else currently in the instance.</summary>
        public static List<AccountClientData> Others()
        {
            var list = new List<AccountClientData>();
            foreach (var obj in Object.FindObjectsByType(Il2CppType.Of<AccountClientData>(), FindObjectsSortMode.None))
            {
                var acc = obj.TryCast<AccountClientData>();
                if (acc != null && !(acc.sharpView != null && acc.sharpView.isMine))
                    list.Add(acc);
            }
            return list;
        }
    }
}
