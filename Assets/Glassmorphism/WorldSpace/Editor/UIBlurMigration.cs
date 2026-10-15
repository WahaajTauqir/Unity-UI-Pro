using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Glassmorphism.Editor
{
    /// Explicit, repeatable migration. Keeps backend file IDs and all existing references.
    public static class UIBlurMigration
    {
        const string CaptureGuid = "c339df1cb9dae49469bc6a75a8f9f69d";
        const string BackdropGuid = "7094aa5831cf54954bd3585bafbebe61";

        [MenuItem("Tools/Glassmorphism/Migrate Panels to UI Blur")]
        public static void MigrateProject()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("Exit Play mode before migrating blur panels.");
                return;
            }
            int count = 0;
            // Prefabs first; scene instances then inherit their facade where applicable.
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!ContainsLegacyModule(path)) continue;
                GameObject root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    int changed = MigrateHierarchy(root, false);
                    if (changed > 0) PrefabUtility.SaveAsPrefabAsset(root, path);
                    count += changed;
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            Scene active = SceneManager.GetActiveScene();
            try
            {
                foreach (string guid in AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!ContainsLegacyModule(path)) continue;
                    Scene scene = SceneManager.GetSceneByPath(path);
                    bool alreadyLoaded = scene.IsValid() && scene.isLoaded;
                    if (!alreadyLoaded) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                    try
                    {
                        int changed = 0;
                        foreach (GameObject root in scene.GetRootGameObjects())
                            changed += MigrateHierarchy(root, true);
                        if (changed > 0)
                        {
                            EditorSceneManager.MarkSceneDirty(scene);
                            EditorSceneManager.SaveScene(scene);
                        }
                        count += changed;
                    }
                    finally
                    {
                        if (!alreadyLoaded) EditorSceneManager.CloseScene(scene, true);
                    }
                }
            }
            finally
            {
                if (active.IsValid() && active.isLoaded) SceneManager.SetActiveScene(active);
            }
            Debug.Log("UIBlur migration complete: " + count + " panels migrated. Backend references retained.");
        }

        static bool ContainsLegacyModule(string path)
        {
            string text = File.ReadAllText(path);
            return text.Contains(CaptureGuid) || text.Contains(BackdropGuid);
        }

        static int MigrateHierarchy(GameObject root, bool recordUndo)
        {
            var panels = new HashSet<GameObject>();
            foreach (var module in root.GetComponentsInChildren<UIBlurScreenCapture>(true)) panels.Add(module.gameObject);
            foreach (var module in root.GetComponentsInChildren<UIBlurSharedBackdrop>(true)) panels.Add(module.gameObject);
            int count = 0;
            foreach (GameObject panel in panels)
            {
                if (panel.GetComponent<UIBlur>() != null) continue;
                var capture = panel.GetComponent<UIBlurScreenCapture>();
                var backdrop = panel.GetComponent<UIBlurSharedBackdrop>();
                bool enabled = capture != null ? capture.enabled : backdrop.enabled;
                var facade = recordUndo ? Undo.AddComponent<UIBlur>(panel) : panel.AddComponent<UIBlur>();
                facade.enabled = enabled;
                facade.Synchronize();
                // Inactive objects don't receive OnEnable; retain the hidden module setup.
                if (capture != null) capture.hideFlags |= HideFlags.HideInInspector;
                if (backdrop != null) backdrop.hideFlags |= HideFlags.HideInInspector;
                EditorUtility.SetDirty(facade);
                if (capture != null) EditorUtility.SetDirty(capture);
                if (backdrop != null) EditorUtility.SetDirty(backdrop);
                count++;
            }
            return count;
        }
    }
}
