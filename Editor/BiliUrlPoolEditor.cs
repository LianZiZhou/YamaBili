using UdonSharpEditor;
using UnityEditor;
using UnityEngine;

namespace YamaBili.EditorTools
{
    // The URL baker: pool settings, a preview of the URLs, what r and z mean, and the Bake button.
    [CustomEditor(typeof(BiliUrlPool))]
    public class BiliUrlPoolEditor : Editor
    {
        static bool _showHelp = true;

        const string HelpZh =
            "【为什么要烘焙】VRChat 不允许 Udon 在运行时拼接 URL，世界里能请求的只有上传前写进场景的 VRCUrl。" +
            "所以插件预先烘焙两组带编号的链接：?v=0…N-1（视频）和 ?q=0…N-1（列表页、封面图）。" +
            "搜索时，服务器把每条结果放进一个空槽，返回槽号；世界拿槽号取出对应的 VRCUrl 播放。" +
            "同一实例里所有人用的是同一个槽号，所以 YamaPlayer 的同步天然一致。\n\n" +
            "【环形池】服务器按顺序分配槽位，转满一圈后覆盖最早的条目；相同的视频/查询在被覆盖前复用同一个槽。" +
            "一条链接（播放列表里的条目、历史记录）在它的槽被覆盖前一直有效。\n\n" +
            "【r = 容量】1–100，单位 1 万：r=10 表示 V 和 Q 各 10 万条。容量越大，链接被覆盖前存活越久，" +
            "但世界体积和烘焙时间按比例增加（r=100 是 200 万条 URL，场景会非常大）。\n\n" +
            "【z = 分区】1–5 位小写字母。不填时使用所有世界共享的默认池，别的世界的流量也会推动你的槽位；" +
            "填写后（例如 lynn）你的世界拥有独立的池。池由 (r, z) 共同确定：同一个 z 配不同的 r 是两个互不相干的池，" +
            "所以改了 r 或 z 就必须重新烘焙并重新上传世界。\n\n" +
            "【m = 合写】为了缩短 URL，r 和 z 合成一个参数写进每条链接：r=10、z=ab → m=10ab；r=1 时省略数字（m=ab）；" +
            "默认池（r=1 且无 z）不写 m，链接与旧版完全相同。";

        const string HelpEn =
            "Why bake: Udon cannot build URLs at runtime; only VRCUrls saved into the scene can be requested. " +
            "So the plugin bakes numbered links, ?v=N (videos) and ?q=N (list pages, cover images). The server puts each " +
            "search result into a slot and returns its number; the world plays the VRCUrl with that number. Everyone in the " +
            "instance resolves the same number, so YamaPlayer sync stays consistent.\n\n" +
            "Ring pool: slots are handed out in order and the oldest entry is overwritten once the ring wraps; a link " +
            "(playlist entry, history) stays valid until its slot is reused.\n\n" +
            "r = capacity, 1-100 in units of 10000 per ring. Bigger keeps links alive longer but grows world size and bake time.\n\n" +
            "z = zone, 1-5 lowercase letters. Empty uses the default pool shared by every world; a zone gives your world its own. " +
            "A pool is (r, z): the same z with another r is a different pool, so changing either means re-baking and re-uploading.\n\n" +
            "m packs both into one short parameter: r=10 z=ab -> m=10ab, r=1 z=ab -> m=ab; the default pool omits m.";

        public override void OnInspectorGUI()
        {
            if (UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target)) return;
            var pool = (BiliUrlPool)target;
            serializedObject.Update();

            EditorGUILayout.LabelField("YamaBili URL Baker", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BiliUrlPool.ServerRoot)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BiliUrlPool.Capacity)), new GUIContent("r  Capacity (x10000)"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BiliUrlPool.Zone)), new GUIContent("z  Zone"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BiliUrlPool.SaveKeyPrefix)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BiliUrlPool.ShowCredit)));
            serializedObject.ApplyModifiedProperties();

            int capacity = Mathf.Clamp(pool.Capacity, 1, BiliUrlPool.MaxCapacity);
            string zone = (pool.Zone ?? "").Trim();
            bool zoneOk = BiliUrlPoolGenerator.ZoneValid(zone);
            int count = capacity * BiliUrlPool.SlotsPerCapacity;
            string q = BiliUrlPoolGenerator.NormalizedRoot(pool.ServerRoot) + BiliUrlPoolGenerator.QueryStart(capacity, zone);

            if (!zoneOk) EditorGUILayout.HelpBox("z must be 1-5 lowercase letters (a-z), or empty. / z 只能是 1–5 位小写字母或留空。", MessageType.Error);
            else
            {
                string id = BiliUrlPoolGenerator.PoolId(capacity, zone);
                // Each URL is stored twice (proxy + Udon) as a YAML line of roughly its length plus some overhead.
                long bytes = 2L * count * 2 * (q.Length + 6 + count.ToString().Length);
                EditorGUILayout.HelpBox(
                    $"m = {(id.Length == 0 ? "(none, default pool)" : id)}\n" +
                    $"{q}v=0 … {q}v={count - 1}\n" +
                    $"{q}search=<keyword>\n" +
                    $"{count:N0} + {count:N0} URLs, ~{bytes / (1024f * 1024f):F0} MB of scene data",
                    capacity > 10 ? MessageType.Warning : MessageType.Info);
            }

            int baked = pool.V != null ? pool.V.Length : 0;
            string bakedFirst = baked > 0 && pool.V[0] != null ? pool.V[0].Get() : "";
            bool upToDate = zoneOk && baked == count && bakedFirst == q + "v=0";
            EditorGUILayout.LabelField("Baked", baked == 0 ? "nothing yet" : $"{baked:N0} x2 ({bakedFirst})");
            if (!upToDate && baked > 0) EditorGUILayout.HelpBox("Settings changed since the last bake. / 设置已改动，需要重新烘焙。", MessageType.Warning);

            using (new EditorGUI.DisabledScope(!zoneOk))
            {
                if (GUILayout.Button(upToDate ? "Re-bake URL Pools" : "Bake URL Pools", GUILayout.Height(28)))
                {
                    bool go = capacity <= 10 || EditorUtility.DisplayDialog("YamaBili",
                        $"r={capacity} bakes {count * 2:N0} URLs into the scene. The scene file and the uploaded world grow a lot and baking takes a while.\n\n" +
                        $"r={capacity} 会向场景写入 {count * 2:N0} 条 URL，场景和世界体积会大幅增加，烘焙需要一些时间。", "Bake", "Cancel");
                    if (go) BiliUrlPoolGenerator.Generate(pool);
                }
            }

            _showHelp = EditorGUILayout.Foldout(_showHelp, "How r / z work · r 和 z 的原理", true);
            if (_showHelp)
            {
                EditorGUILayout.HelpBox(HelpZh, MessageType.None);
                EditorGUILayout.HelpBox(HelpEn, MessageType.None);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Input prefixes", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BiliUrlPool.SearchPrefix)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BiliUrlPool.UploaderPrefix)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BiliUrlPool.FavoritesPrefix)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BiliUrlPool.DirectPrefix)));
            }
        }
    }
}
