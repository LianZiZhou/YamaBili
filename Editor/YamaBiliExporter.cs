using System.IO;
using UnityEditor;
using UnityEngine;

namespace YamaBili.EditorTools
{
    // Packs the plugin folder into Exports/YamaBili-<version>.unitypackage at the project root.
    // Batch mode: Unity -batchmode -quit -projectPath <p> -executeMethod YamaBili.EditorTools.YamaBiliExporter.Export
    public static class YamaBiliExporter
    {
        [MenuItem("Tools/YamaBili/Export .unitypackage")]
        public static void ExportMenu()
        {
            string path = Export();
            EditorUtility.RevealInFinder(path);
        }

        public static string Export()
        {
            string folder = PluginFolder();
            string version = File.Exists(folder + "/version.txt") ? File.ReadAllText(folder + "/version.txt").Trim() : "0.0.0";
            Directory.CreateDirectory("Exports");
            string path = Path.GetFullPath($"Exports/YamaBili-{version}.unitypackage");
            AssetDatabase.ExportPackage(folder, path, ExportPackageOptions.Recurse);
            Debug.Log("[YamaBili] Exported " + path);
            return path;
        }

        // Assets/YamaBili, or wherever the plugin was moved to: the parent of this script's Editor folder.
        static string PluginFolder()
        {
            foreach (string guid in AssetDatabase.FindAssets("YamaBiliExporter t:MonoScript"))
            {
                string script = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileName(script) == "YamaBiliExporter.cs")
                    return Path.GetDirectoryName(Path.GetDirectoryName(script)).Replace('\\', '/');
            }
            return "Assets/YamaBili";
        }
    }
}
