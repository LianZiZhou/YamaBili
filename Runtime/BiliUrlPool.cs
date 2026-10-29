using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace YamaBili
{
    // Pre-baked numbered URLs for the YamaBili API server.
    // Udon cannot build a VRCUrl at runtime, so the server hands out slot numbers and the world looks them up here.
    // Bake with the Bake button in this component's inspector (or Tools/YamaBili/Bake URL Pools).
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class BiliUrlPool : UdonSharpBehaviour
    {
        public const int SlotsPerCapacity = 10000;
        public const int MaxCapacity = 100;

        [Tooltip("API server root with trailing slash.")]
        public string ServerRoot = "https://bili.v.lzz.moe/";
        [Tooltip("r: pool size in units of 10000 URLs per ring (1-100).")]
        [Range(1, MaxCapacity)] public int Capacity = 1;
        [Tooltip("z: 1-5 lowercase letters for a pool of this world's own. Empty = the shared default pool.")]
        public string Zone = "";
        [Tooltip("Prefix of the PlayerData keys that keep search history and danmaku settings.")]
        public string SaveKeyPrefix = "YamaBili.";
        [Tooltip("Show the plugin credit on YamaPlayer's Info page.")]
        public bool ShowCredit = true;

        [Header("Input prefixes")]
        public VRCUrl SearchPrefix;
        public VRCUrl UploaderPrefix;
        public VRCUrl FavoritesPrefix;
        public VRCUrl DirectPrefix;

        [Header("Pools (generated)")]
        [HideInInspector] public VRCUrl[] V; // ?v=N : playable video slots
        [HideInInspector] public VRCUrl[] Q; // ?q=N : list pages and cover atlases

        public VRCUrl GetV(int i) => i >= 0 && V != null && i < V.Length ? V[i] : null;
        public VRCUrl GetQ(int i) => i >= 0 && Q != null && i < Q.Length ? Q[i] : null;

        public bool IsOurs(string url) => !string.IsNullOrEmpty(url) && url.StartsWith(ServerRoot);
    }
}
