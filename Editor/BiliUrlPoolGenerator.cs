using System.Text.RegularExpressions;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDKBase;

namespace YamaBili.EditorTools
{
    // Bakes a BiliUrlPool: the input prefixes plus Capacity x 10000 `?v=N` and `?q=N` URLs.
    // Every URL names its pool with the packed `m` parameter (r and z), omitted for the default pool,
    // so the server keeps one pair of rings per (r, z). See BiliUrlPoolEditor for the explanation shown to users.
    public static class BiliUrlPoolGenerator
    {
        static readonly Regex ZoneRe = new Regex("^[a-z]{1,5}$");

        [MenuItem("Tools/YamaBili/Bake URL Pools")]
        public static void GenerateAll()
        {
            foreach (var pool in Object.FindObjectsOfType<BiliUrlPool>(true)) Generate(pool);
        }

        public static bool ZoneValid(string zone) => string.IsNullOrEmpty(zone) || ZoneRe.IsMatch(zone);

        // The packed pool id: r (left out when 1) followed by z, e.g. "10ab", "a", "10" or "" for the default pool.
        public static string PoolId(int capacity, string zone) => (capacity == 1 ? "" : capacity.ToString()) + (zone ?? "");

        // Query string start for every baked URL, e.g. "?m=10ab&" or "?".
        public static string QueryStart(int capacity, string zone)
        {
            string id = PoolId(capacity, zone);
            return id.Length == 0 ? "?" : "?m=" + id + "&";
        }

        public static string NormalizedRoot(string root)
        {
            root = (root ?? "").Trim();
            return root.EndsWith("/") ? root : root + "/";
        }

        public static bool Generate(BiliUrlPool pool)
        {
            int capacity = Mathf.Clamp(pool.Capacity, 1, BiliUrlPool.MaxCapacity);
            string zone = (pool.Zone ?? "").Trim();
            if (!ZoneValid(zone))
            {
                Debug.LogError($"[YamaBili] Zone \"{zone}\" must be 1-5 lowercase letters (or empty).", pool);
                return false;
            }
            string root = NormalizedRoot(pool.ServerRoot);
            string q = root + QueryStart(capacity, zone);
            int count = capacity * BiliUrlPool.SlotsPerCapacity;

            Undo.RecordObject(pool, "Bake YamaBili URL Pools");
            pool.ServerRoot = root;
            pool.Capacity = capacity;
            pool.Zone = zone;
            pool.SearchPrefix = new VRCUrl(q + "search=");
            pool.UploaderPrefix = new VRCUrl(q + "mid=");
            pool.FavoritesPrefix = new VRCUrl(q + "fav=");
            pool.DirectPrefix = new VRCUrl(q + "url=");
            try
            {
                pool.V = Build(q + "v=", count, "V", 0f);
                pool.Q = Build(q + "q=", count, "Q", 0.5f);
                EditorUtility.DisplayProgressBar("YamaBili", "Copying to Udon...", 1f);
                UdonSharpEditorUtility.CopyProxyToUdon(pool);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            EditorUtility.SetDirty(pool);
            EditorSceneManager.MarkSceneDirty(pool.gameObject.scene);
            Debug.Log($"[YamaBili] Baked {count} + {count} URLs: {q}v=N", pool);
            return true;
        }

        static VRCUrl[] Build(string prefix, int count, string label, float progressStart)
        {
            var urls = new VRCUrl[count];
            for (int i = 0; i < count; i++)
            {
                if ((i & 0xFFFF) == 0 && count > 100000)
                    EditorUtility.DisplayProgressBar("YamaBili", $"Baking {label} {i}/{count}", progressStart + 0.5f * i / count);
                urls[i] = new VRCUrl(prefix + i);
            }
            return urls;
        }
    }
}
