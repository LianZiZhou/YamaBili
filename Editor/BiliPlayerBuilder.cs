using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.Udon;
using Yamadev.YamaStream;
using Yamadev.YamaStream.UI;

namespace YamaBili.EditorTools
{
    // Builds the Bilibili page into every YamaPlayer in the scene and each screen UI that shows it (main screen
    // and sub screens): a "video search" button in the left side bar, the page itself under Pages, the direct
    // link input, the Info credit, the danmaku canvas over the screen, and the Udon behaviours wired together.
    // Nothing inside the YamaPlayer package is changed. Safe to re-run; it removes what it built before.
    // After updating YamaPlayer: Remove From YamaPlayer, update, Build Into YamaPlayer.
    public static class BiliPlayerBuilder
    {
        const string Root = "BiliPlayer";
        const string PageName = "BiliPage";
        const string NavName = "BiliSearch";
        const string DanmakuName = "BiliDanmaku";
        const string DirectName = "BiliDirect";
        const string CreditName = "BiliCredit";
        const string TestedYamaVersion = "1.5."; // YamaPlayer versions whose UI hierarchy this builder was checked against
        const float S = 0.2f; // YamaPlayer draws text 5x larger and scales it down, for crisp glyphs

        // Everything the builder clones or edits inside a YamaPlayer screen UI.
        static readonly string[] RequiredPaths =
        {
            "Canvas/Control/Main/Pages/Return/Text",
            "Canvas/Control/Main/Pages/Settings/Menu/Playback",
            "Canvas/Control/Main/Pages/Settings/Page/Playback View/Viewport/Content/VideoPlayer",
            "Canvas/Control/Main/Pages/Settings/Page/Playback View/Viewport/Content/PlaybackDelay",
            "Canvas/Control/Main/Pages/Settings/Page/Video View",
            "Canvas/Control/Main/Pages/Settings/Page/Permission View",
            "Canvas/Control/Main/Pages/Settings/Page/Other View",
            "Canvas/Control/Main/Pages/Version/Aurhor/Image",
            "Canvas/Control/Main/Pages/Version/Aurhor/VRChat",
            "Canvas/Control/Main/Pages/Version/Aurhor/Github",
            "Canvas/Control/Main/Pages/Version/UpdateLog",
            "Canvas/Control/Main/LeftSide/Container/Info",
            "Canvas/Control/Main/LeftSide/Container/Settings",
            "Canvas/Control/Main/LeftSide/Container/UrlInput",
            "Canvas/Modal/Actions/Execute",
        };

        static string Folder;      // the plugin's runtime folder: BiliLocale.json and Icons/ live next to BiliUrlPool.cs
        static string YamaFolder;  // YamaPlayer's package root, for its images
        static Font _font;

        static BiliSearch _search;
        static UdonBehaviour _searchUdon;
        static readonly List<Text> Labels = new List<Text>();
        static readonly List<string> LabelKeys = new List<string>();

        [MenuItem("Tools/YamaBili/Build Into YamaPlayer")]
        public static void Build()
        {
            var controllers = Object.FindObjectsOfType<Controller>(true);
            if (controllers.Length == 0) { Debug.LogError("[YamaBili] No YamaPlayer in the scene."); return; }
            if (!LocateFolders(controllers[0])) return;
            foreach (var controller in controllers) BuildInto(controller);
        }

        [MenuItem("Tools/YamaBili/Remove From YamaPlayer")]
        public static void RemoveAll()
        {
            foreach (var controller in Object.FindObjectsOfType<Controller>(true))
            {
                Transform player = PlayerRoot(controller);
                RemoveBuilt(player, ScreenUIs(controller, player));
                EditorSceneManager.MarkSceneDirty(controller.gameObject.scene);
            }
            Debug.Log("[YamaBili] Removed from every YamaPlayer.");
        }

        static void BuildInto(Controller controller)
        {
            Transform t = PlayerRoot(controller);
            List<Transform> screenUIs = ScreenUIs(controller, t);
            if (screenUIs.Count == 0) { Debug.LogError("[YamaBili] No screen UI shows " + t.name, t); return; }
            var missing = screenUIs.SelectMany(ui => RequiredPaths.Where(p => ui.Find(p) == null).Select(p => ui.name + "/" + p)).ToList();
            if (missing.Count > 0)
            {
                Debug.LogError($"[YamaBili] {t.name}: YamaPlayer's UI does not have the parts this version of YamaBili builds on " +
                    $"(checked against YamaPlayer {TestedYamaVersion}x). Update YamaBili, or report this with your YamaPlayer version.\nMissing:\n" +
                    string.Join("\n", missing), t);
                return;
            }
            _font = screenUIs[0].Find("Canvas/Control/Main/Pages/Return/Text").GetComponent<Text>().font;

            // Settings survive a rebuild.
            var oldPool = t.Find(Root) != null ? t.Find(Root).GetComponent<BiliUrlPool>() : null;
            string settings = oldPool != null ? JsonUtility.ToJson(new PoolSettings(oldPool)) : null;
            RemoveBuilt(t, screenUIs);

            // Logic object
            var root = new GameObject(Root);
            Undo.RegisterCreatedObjectUndo(root, "Build YamaBili");
            root.transform.SetParent(t, false);
            var pool = UdonSharpUndo.AddComponent<BiliUrlPool>(root);
            if (settings != null) JsonUtility.FromJson<PoolSettings>(settings).ApplyTo(pool);
            var danmaku = UdonSharpUndo.AddComponent<BiliDanmaku>(root);

            var views = new List<BiliDanmakuView>();
            foreach (Transform ui in screenUIs)
            {
                var view = BuildDanmakuCanvas(ui, controller, ui.Find("Canvas/Control/Main/Pages"));
                Set(view, "_saveKey", pool.SaveKeyPrefix);
                views.Add(view);
            }
            // Every panel edits the first view's settings; BiliDanmaku copies them to the others.
            var panels = new List<UdonSharpBehaviour>();
            foreach (Transform ui in screenUIs)
                panels.Add(BuildPanel(ui, root, controller, pool, danmaku, views[0]));

            Set(danmaku, "_controller", controller);
            Set(danmaku, "_pool", pool);
            Set(danmaku, "_views", views.ToArray());
            Set(danmaku, "_panels", panels.ToArray());

            UdonSharpEditorUtility.CopyProxyToUdon(danmaku);
            foreach (var v in views) UdonSharpEditorUtility.CopyProxyToUdon(v);
            BiliUrlPoolGenerator.Generate(pool);

            EditorSceneManager.MarkSceneDirty(t.gameObject.scene);
            Selection.activeGameObject = root;
            Debug.Log($"[YamaBili] Built into {t.name} ({screenUIs.Count} screen UIs). Server: " + pool.ServerRoot, root);
        }

        [System.Serializable]
        class PoolSettings
        {
            public string ServerRoot, Zone, SaveKeyPrefix;
            public int Capacity;
            public bool ShowCredit;

            public PoolSettings(BiliUrlPool p)
            {
                ServerRoot = p.ServerRoot; Zone = p.Zone; SaveKeyPrefix = p.SaveKeyPrefix; Capacity = p.Capacity; ShowCredit = p.ShowCredit;
            }

            public void ApplyTo(BiliUrlPool p)
            {
                p.ServerRoot = ServerRoot; p.Zone = Zone; p.SaveKeyPrefix = SaveKeyPrefix; p.Capacity = Capacity; p.ShowCredit = ShowCredit;
            }
        }

        // The Bilibili page, side bar button, direct link input and Info credit of one screen UI.
        static BiliSearch BuildPanel(Transform ui, GameObject root, Controller controller,
            BiliUrlPool pool, BiliDanmaku danmaku, BiliDanmakuView view)
        {
            Transform control = ui.Find("Canvas/Control");
            Transform main = control.Find("Main");
            Transform pages = main.Find("Pages");
            Transform nav = main.Find("LeftSide/Container");
            var uiController = ui.GetComponent<UdonSharpBehaviour>();
            Labels.Clear();
            LabelKeys.Clear();
            _search = UdonSharpUndo.AddComponent<BiliSearch>(root);
            _searchUdon = UdonSharpEditorUtility.GetBackingUdonBehaviour(_search);

            Transform page = BuildPage(pages, ui.Find("Canvas/Modal/Actions/Execute"));
            BuildNavButton(nav, control, pages, page.gameObject);
            var direct = BuildDirectInput(root, nav.Find("UrlInput"), uiController, pool);
            if (pool.ShowCredit) BuildCredit(pages.Find("Version"));

            Set(_search, "_controller", controller);
            Set(_search, "_uiController", uiController);
            Set(_search, "_pool", pool);
            Set(_search, "_danmaku", danmaku);
            Set(_search, "_view", view);
            Set(_search, "_saveKey", pool.SaveKeyPrefix);
            Set(_search, "_locale", AssetDatabase.LoadAssetAtPath<TextAsset>(Folder + "/BiliLocale.json"));
            Set(_search, "_labels", Labels.ToArray());
            Set(_search, "_labelKeys", LabelKeys.ToArray());
            UdonSharpEditorUtility.CopyProxyToUdon(_search);
            UdonSharpEditorUtility.CopyProxyToUdon(direct);
            return _search;
        }

        // Undoes everything Build added to one player and its screen UIs.
        static void RemoveBuilt(Transform player, List<Transform> screenUIs)
        {
            Remove(player.Find(Root));
            foreach (Transform ui in screenUIs)
            {
                Transform main = ui.Find("Canvas/Control/Main");
                if (main == null) continue;
                Remove(ui.Find(DanmakuName));
                Remove(main.Find("Pages/" + PageName));
                Remove(main.Find("LeftSide/Container/" + NavName));
                Remove(main.Find("LeftSide/Container/UrlInput/" + DirectName));
                Transform version = main.Find("Pages/Version");
                if (version != null && version.Find(CreditName) != null)
                {
                    Remove(version.Find(CreditName));
                    var log = (RectTransform)version.Find("UpdateLog");
                    Undo.RecordObject(log, "Remove YamaBili");
                    log.anchoredPosition = new Vector2(40, -280);
                    log.sizeDelta = new Vector2(500, 500);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(log);
                }
                // Our page's listeners on YamaPlayer's own page buttons now point at nothing.
                foreach (string button in new[] { "Info", "Settings" })
                {
                    Transform b = main.Find("LeftSide/Container/" + button);
                    if (b == null) continue;
                    var ob = b.GetComponent<Button>();
                    Undo.RecordObject(ob, "Remove YamaBili");
                    RemoveListenersTo(ob.onClick, PageName);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(ob);
                }
            }
        }

        static void Remove(Transform tr)
        {
            if (tr != null) Undo.DestroyObjectImmediate(tr.gameObject);
        }

        // YamaPlayer keeps its Controller at <player>/Internal/Controller.
        static Transform PlayerRoot(Controller controller)
        {
            Transform internalNode = controller.transform.parent;
            return internalNode != null && internalNode.parent != null ? internalNode.parent : controller.transform;
        }

        // Every UIController (main screen and sub screens, active or not) that drives this controller;
        // the player's own screen UI first.
        static List<Transform> ScreenUIs(Controller controller, Transform player)
        {
            return Object.FindObjectsOfType<UIController>(true)
                .Where(ui => new SerializedObject(ui).FindProperty("_controller")?.objectReferenceValue == controller)
                .Select(ui => ui.transform)
                .OrderBy(ui => ui.IsChildOf(player) ? 0 : 1)
                .ToList();
        }

        // Finds our runtime folder and YamaPlayer's root wherever they were installed, and checks YamaPlayer's version.
        static bool LocateFolders(Controller controller)
        {
            string poolScript = AssetDatabase.FindAssets("BiliUrlPool t:MonoScript")
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => AssetDatabase.LoadAssetAtPath<MonoScript>(p)?.GetClass() == typeof(BiliUrlPool));
            if (poolScript == null) { Debug.LogError("[YamaBili] Cannot find BiliUrlPool.cs."); return false; }
            Folder = Path.GetDirectoryName(poolScript).Replace('\\', '/');

            // .../Runtime/Internal/Controller.cs -> package root
            string controllerScript = AssetDatabase.GetAssetPath(MonoScript.FromMonoBehaviour(controller));
            YamaFolder = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(controllerScript))).Replace('\\', '/');

            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(controllerScript);
            if (package != null && !package.version.StartsWith(TestedYamaVersion))
                Debug.LogWarning($"[YamaBili] YamaPlayer {package.version} has not been tested with this YamaBili (tested: {TestedYamaVersion}x). " +
                    "If the build fails or the page looks wrong, check for a YamaBili update.");
            return true;
        }

        // --- Danmaku canvas ---------------------------------------------------------------

        // Depth layers in front of the screen, in metres, and the viewing distance the layers are sized for.
        static readonly float[] LayerDepths = { 0.08f, 0.25f, 0.45f, 0.68f, 0.9f };
        const float ViewDistance = 2.8f;
        const int RollPerLayer = 25;

        static BiliDanmakuView BuildDanmakuCanvas(Transform screenUI, Controller controller, Transform pages)
        {
            var uiCanvas = (RectTransform)screenUI.Find("Canvas");
            var go = new GameObject(DanmakuName, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(CanvasGroup));
            go.transform.SetParent(screenUI, false);
            go.layer = uiCanvas.gameObject.layer;
            var rt = (RectTransform)go.transform;
            rt.localPosition = uiCanvas.localPosition;
            rt.localRotation = uiCanvas.localRotation;
            // YamaPlayer's canvas has a z scale of 0, which would flatten the depth layers; keep z like x.
            rt.localScale = new Vector3(uiCanvas.localScale.x, uiCanvas.localScale.y, uiCanvas.localScale.x);
            rt.sizeDelta = uiCanvas.sizeDelta;
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = uiCanvas.GetComponent<Canvas>().sortingOrder + 1;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 4f;
            scaler.referencePixelsPerUnit = 100f;
            var group = go.GetComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;
            go.transform.SetAsFirstSibling();

            // Each layer floats `depth` metres towards the viewer and clips its text with RectMask2D.
            // The view rescales the layers for the player's head at runtime; ViewDistance is only the
            // starting scale, for the editor.
            Vector3 toViewer = -rt.forward;
            var layers = new RectTransform[LayerDepths.Length];
            for (int i = 0; i < LayerDepths.Length; i++)
            {
                float depth = LayerDepths[i];
                var layer = Rect(go.transform, "Layer" + i);
                layer.anchorMin = layer.anchorMax = new Vector2(0.5f, 0.5f);
                layer.sizeDelta = rt.sizeDelta;
                float scale = (ViewDistance - depth) / ViewDistance;
                layer.localScale = new Vector3(scale, scale, scale);
                layer.position = rt.position + toViewer * depth;
                layer.gameObject.AddComponent<RectMask2D>();
                layers[i] = layer;
            }

            var rollTexts = new Text[LayerDepths.Length * RollPerLayer];
            for (int l = 0; l < LayerDepths.Length; l++)
            {
                var roll = Rect(layers[l], "Roll");
                Stretch(roll, 0, 30, 0, 0);
                for (int i = 0; i < RollPerLayer; i++)
                {
                    int k = l * RollPerLayer + i;
                    rollTexts[k] = DanmakuText(roll, "R" + i, new Vector2(0, 1), new Vector2(0, 1), TextAnchor.MiddleLeft);
                }
            }

            // Pinned comments and subtitles stay on the layer nearest the screen.
            var area = layers[0];
            var top = Rect(area, "Top");
            Stretch(top, 0, 30, 0, 0);
            var topTexts = new Text[5];
            for (int i = 0; i < topTexts.Length; i++)
                topTexts[i] = DanmakuText(top, "T" + i, new Vector2(0.5f, 1), new Vector2(0.5f, 1), TextAnchor.MiddleCenter);

            var bottom = Rect(area, "Bottom");
            Stretch(bottom, 0, 0, 0, 110);
            var bottomTexts = new Text[3];
            for (int i = 0; i < bottomTexts.Length; i++)
                bottomTexts[i] = DanmakuText(bottom, "B" + i, new Vector2(0.5f, 0), new Vector2(0.5f, 0), TextAnchor.MiddleCenter);

            var subBox = Rect(area, "Subtitle");
            subBox.anchorMin = subBox.anchorMax = new Vector2(0.5f, 0);
            subBox.pivot = new Vector2(0.5f, 0);
            subBox.anchoredPosition = new Vector2(0, 24);
            subBox.sizeDelta = new Vector2(1500, 76);
            var subText = subBox.gameObject.AddComponent<Text>();
            subText.font = _font;
            subText.fontSize = 40;
            subText.alignment = TextAnchor.MiddleCenter;
            subText.horizontalOverflow = HorizontalWrapMode.Wrap;
            subText.verticalOverflow = VerticalWrapMode.Overflow;
            subText.color = Color.white;
            subText.raycastTarget = false;
            var outline = subBox.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0, 0, 0, 0.85f);
            outline.effectDistance = new Vector2(2, -2);
            subBox.gameObject.SetActive(false);

            var view = UdonSharpUndo.AddComponent<BiliDanmakuView>(go);
            Set(view, "_controller", controller);
            Set(view, "_area", area);
            Set(view, "_group", group);
            Set(view, "_hideWhenActive", new[] { pages.gameObject });
            Set(view, "_layerCount", LayerDepths.Length);
            Set(view, "_canvasRoot", rt);
            Set(view, "_layers", layers);
            Set(view, "_layerDepths", (float[])LayerDepths.Clone());
            Set(view, "_roll", rollTexts);
            Set(view, "_top", topTexts);
            Set(view, "_bottom", bottomTexts);
            Set(view, "_subtitle", subText);
            Set(view, "_subtitleBox", subBox.gameObject);
            return view;
        }

        static Text DanmakuText(RectTransform parent, string name, Vector2 anchor, Vector2 pivot, TextAnchor align)
        {
            var rt = Rect(parent, name);
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.sizeDelta = new Vector2(10, 44);
            var text = rt.gameObject.AddComponent<Text>();
            text.font = _font;
            text.fontSize = 34;
            text.alignment = align;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.color = Color.white;
            text.raycastTarget = false;
            var outline = rt.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0, 0, 0, 0.7f);
            outline.effectDistance = new Vector2(1.5f, -1.5f);
            rt.gameObject.SetActive(false);
            return text;
        }

        // --- Side bar button ------------------------------------------------------------------

        static Button BuildNavButton(Transform nav, Transform control, Transform pages, GameObject page)
        {
            Transform info = nav.Find("Info");
            var go = Object.Instantiate(info.gameObject, nav);
            go.name = NavName;
            Undo.RegisterCreatedObjectUndo(go, "Build YamaBili");
            go.transform.SetSiblingIndex(info.GetSiblingIndex());
            go.GetComponent<Image>().sprite = Icon("bilibili");
            var tip = go.transform.Find("Tooltip/Text");
            if (tip != null) Label(tip.GetComponent<Text>(), "pageTitle");

            var animator = control.GetComponent<Animator>();
            GameObject settings = pages.Find("Settings").gameObject;
            GameObject version = pages.Find("Version").gameObject;
            var button = go.GetComponent<Button>();
            ClearPersistent(button.onClick);
            UnityEventTools.AddStringPersistentListener(button.onClick, animator.SetTrigger, "HidePlaylist");
            UnityEventTools.AddBoolPersistentListener(button.onClick, settings.SetActive, false);
            UnityEventTools.AddBoolPersistentListener(button.onClick, version.SetActive, false);
            UnityEventTools.AddBoolPersistentListener(button.onClick, page.SetActive, true);
            UnityEventTools.AddStringPersistentListener(button.onClick, animator.SetTrigger, "HideFrontControl");
            UnityEventTools.AddStringPersistentListener(button.onClick, animator.SetTrigger, "ShowPage");
            UnityEventTools.AddStringPersistentListener(button.onClick, _searchUdon.SendCustomEvent, nameof(BiliSearch.OpenPage));

            // YamaPlayer's own page buttons must hide ours.
            foreach (var other in new[] { nav.Find("Info"), nav.Find("Settings") })
            {
                var ob = other.GetComponent<Button>();
                RemoveListenersTo(ob.onClick, PageName);
                UnityEventTools.AddBoolPersistentListener(ob.onClick, page.SetActive, false);
                PrefabUtility.RecordPrefabInstancePropertyModifications(ob);
            }
            return button;
        }

        // --- Direct link input ---------------------------------------------------------------------
        // A field of our own laid over the side bar's link icon, filled with the server's ?url= prefix.
        // It is a child of YamaPlayer's field, so hovering it still shows the icon's tooltip.

        static BiliDirectInput BuildDirectInput(GameObject root, Transform original, UdonSharpBehaviour uiController, BiliUrlPool pool)
        {
            var originalField = original.GetComponent<VRCUrlInputField>();
            var rt = Rect(original, DirectName);
            Stretch(rt, 0, 0, 0, 0);
            var image = rt.gameObject.AddComponent<Image>();
            image.color = new Color(1, 1, 1, 0); // invisible, but takes the clicks
            var text = Object.Instantiate(originalField.textComponent.gameObject, rt).GetComponent<Text>();
            text.name = "Text";
            var input = rt.gameObject.AddComponent<VRCUrlInputField>();
            input.textComponent = text;
            input.targetGraphic = image;
            input.lineType = VRCUrlInputField.LineType.SingleLine;
            var colors = input.colors;
            colors.normalColor = colors.highlightedColor = colors.pressedColor = colors.selectedColor = new Color(1, 1, 1, 0);
            input.colors = colors;

            var direct = UdonSharpUndo.AddComponent<BiliDirectInput>(root);
            var udon = UdonSharpEditorUtility.GetBackingUdonBehaviour(direct);
            Set(direct, "_uiController", uiController);
            Set(direct, "_original", originalField);
            Set(direct, "_playEvent", "PlayUrl");
            Set(direct, "_input", input);
            Set(direct, "_pool", pool);
            UnityEventTools.AddStringPersistentListener(input.onEndEdit, udon.SendCustomEvent, nameof(BiliDirectInput.Submit));
            OnPointerEnter(rt.gameObject, udon, nameof(BiliDirectInput.Refill));
            return direct;
        }

        static void OnPointerEnter(GameObject go, UdonBehaviour target, string eventName)
        {
            var trigger = go.AddComponent<UnityEngine.EventSystems.EventTrigger>();
            var entry = new UnityEngine.EventSystems.EventTrigger.Entry { eventID = UnityEngine.EventSystems.EventTriggerType.PointerEnter };
            UnityEventTools.AddStringPersistentListener(entry.callback, target.SendCustomEvent, eventName);
            trigger.triggers.Add(entry);
        }

        // --- Credit on the Info page --------------------------------------------------------------
        // Our module's author above the update log, in the style of YamaPlayer's own author block.

        const float CreditHeight = 190;

        static void BuildCredit(Transform version)
        {
            Transform author = version.Find("Aurhor");
            var log = (RectTransform)version.Find("UpdateLog");
            log.anchoredPosition = new Vector2(40, -280 - CreditHeight);
            log.sizeDelta = new Vector2(500, 500 - CreditHeight);
            PrefabUtility.RecordPrefabInstancePropertyModifications(log); // its scroll view stretches with it

            var credit = Rect(version, CreditName);
            credit.anchorMin = credit.anchorMax = new Vector2(0.5f, 1);
            credit.pivot = new Vector2(0, 1);
            credit.anchoredPosition = new Vector2(40, -280);
            credit.sizeDelta = new Vector2(500, CreditHeight);

            var avatar = Object.Instantiate(author.Find("Image").gameObject, credit).transform as RectTransform;
            avatar.name = "Image";
            Place(avatar, 30, 20, 130, 130);
            avatar.GetComponent<Image>().sprite = AvatarSprite();

            var title = MakeText(credit, 190, 16, 310, 36, 22, TextAnchor.MiddleRight, Strong, "creditTitle");
            title.horizontalOverflow = HorizontalWrapMode.Overflow;
            CreditRow(credit, author.Find("VRChat"), 70, "莲子粥 Lynn");
            CreditRow(credit, author.Find("Github"), 115, "LianZiZhou");
        }

        static void CreditRow(Transform credit, Transform template, float y, string value)
        {
            var row = Object.Instantiate(template.gameObject, credit).transform as RectTransform;
            row.name = template.name;
            Place(row, 180, y, 320, 40);
            row.Find("Text").GetComponent<Text>().text = value;
        }

        // Icons/lynn_avatar.png: the author's avatar, cut to a circle.
        static Sprite AvatarSprite()
        {
            string path = Folder + "/Icons/lynn_avatar.png";
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            if (importer.textureType != TextureImporterType.Sprite)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = true;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        static void ClearPersistent(UnityEventBase e)
        {
            for (int i = e.GetPersistentEventCount() - 1; i >= 0; i--) UnityEventTools.RemovePersistentListener(e, i);
        }

        static void RemoveListenersTo(UnityEventBase e, string targetName)
        {
            for (int i = e.GetPersistentEventCount() - 1; i >= 0; i--)
            {
                var target = e.GetPersistentTarget(i);
                if (target == null || target.name == targetName) UnityEventTools.RemovePersistentListener(e, i);
            }
        }

        // --- Icons ---------------------------------------------------------------------------------

        // Line icons drawn to match YamaPlayer's own set: 512 px, white, ~21 px rounded strokes.
        static Sprite Icon(string name)
        {
            string path = Folder + "/Icons/" + name + ".png";
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Folder + "/Icons");
                const int n = 512;
                const float half = 10.5f;
                var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
                for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    float d = IconDistance(name, p) - half;
                    tex.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(0.5f - d)));
                }
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                AssetDatabase.ImportAsset(path);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.textureType = TextureImporterType.Sprite;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = true;
                importer.SaveAndReimport();
            }
            var imported = (TextureImporter)AssetImporter.GetAtPath(path);
            if (imported.textureType != TextureImporterType.Sprite)
            {
                imported.textureType = TextureImporterType.Sprite;
                imported.alphaIsTransparency = true;
                imported.mipmapEnabled = true;
                imported.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        static float IconDistance(string name, Vector2 p)
        {
            switch (name)
            {
                case "search":
                    return Mathf.Min(Ring(p, new Vector2(222, 290), 160), Seg(p, new Vector2(340, 172), new Vector2(470, 42)));
                case "user":
                    return Mathf.Min(Ring(p, new Vector2(256, 352), 105),
                        p.y >= 40 ? Ring(p, new Vector2(256, 40), 190) : Mathf.Min((p - new Vector2(66, 40)).magnitude, (p - new Vector2(446, 40)).magnitude));
                case "danmaku":
                {
                    float box = Mathf.Abs(RoundBox(p - new Vector2(256, 296), new Vector2(205, 165), 60));
                    float tail = Mathf.Min(Seg(p, new Vector2(120, 131), new Vector2(92, 52)), Seg(p, new Vector2(92, 52), new Vector2(200, 131)));
                    float lines = Mathf.Min(Seg(p, new Vector2(140, 350), new Vector2(372, 350)),
                        Mathf.Min(Seg(p, new Vector2(140, 290), new Vector2(312, 290)), Seg(p, new Vector2(140, 230), new Vector2(252, 230))));
                    return Mathf.Min(box, Mathf.Min(tail, lines));
                }
                case "bilibili":
                {
                    // The Bilibili TV: screen outline, two antennae, two eyes.
                    float box = Mathf.Abs(RoundBox(p - new Vector2(256, 215), new Vector2(226, 165), 70));
                    float antennae = Mathf.Min(Seg(p, new Vector2(196, 380), new Vector2(126, 462)), Seg(p, new Vector2(316, 380), new Vector2(386, 462)));
                    float eyes = Mathf.Min(Seg(p, new Vector2(170, 200), new Vector2(170, 250)), Seg(p, new Vector2(342, 200), new Vector2(342, 250)));
                    return Mathf.Min(box, Mathf.Min(antennae, eyes));
                }
                case "history":
                    return Mathf.Min(Ring(p, new Vector2(256, 256), 215),
                        Mathf.Min(Seg(p, new Vector2(256, 256), new Vector2(256, 380)), Seg(p, new Vector2(256, 256), new Vector2(345, 200))));
            }
            return 1e6f;
        }

        static float Ring(Vector2 p, Vector2 c, float r) => Mathf.Abs((p - c).magnitude - r);

        static float Seg(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float h = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return (p - (a + ab * h)).magnitude;
        }

        static float RoundBox(Vector2 p, Vector2 halfSize, float r)
        {
            Vector2 q = new Vector2(Mathf.Abs(p.x), Mathf.Abs(p.y)) - halfSize + Vector2.one * r;
            return new Vector2(Mathf.Max(q.x, 0), Mathf.Max(q.y, 0)).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0) - r;
        }

        static Sprite YamaSprite(string name)
        {
            return AssetDatabase.LoadAssetAtPath<Sprite>(YamaFolder + "/Assets/Images/" + name + ".png");
        }

        // --- Page ---------------------------------------------------------------------------------
        // Cloned from YamaPlayer's own Settings page, so the side menu, scroll views, section
        // titles, radio buttons and theme colour (UIColor) all behave and look like the player's.

        static Transform BuildPage(Transform pages, Transform modalExecute)
        {
            Transform settings = pages.Find("Settings");
            var pageGo = Object.Instantiate(settings.gameObject, pages);
            pageGo.name = PageName;
            Undo.RegisterCreatedObjectUndo(pageGo, "Build YamaBili");
            Transform page = pageGo.transform;
            page.SetSiblingIndex(settings.GetSiblingIndex() + 1);

            // Templates from the cloned Settings page
            Transform menu = page.Find("Menu");
            Transform body = page.Find("Page");
            Transform scrollTemplate = body.Find("Playback View");
            Transform content = scrollTemplate.Find("Viewport/Content");
            Transform radioSection = content.Find("VideoPlayer");
            Transform stepSection = content.Find("PlaybackDelay");
            radioSection.SetParent(page, false);
            stepSection.SetParent(page, false);
            foreach (var v in new[] { "Video View", "Permission View", "Other View" }) Object.DestroyImmediate(body.Find(v).gameObject);

            // Side menu
            Transform menuTemplate = menu.Find("Playback");
            for (int i = menu.childCount - 1; i >= 0; i--)
                if (menu.GetChild(i) != menuTemplate) Object.DestroyImmediate(menu.GetChild(i).gameObject);
            var menuLayout = menu.GetComponent<VerticalLayoutGroup>();
            menuLayout.spacing = 4;
            string[] tabKeys = { "tabSearch", "tabUploader", "tabFavorites", "tabPages", "tabDanmaku", "tabHistory" };
            string[] tabEvents = { "ShowSearch", "ShowUploader", "ShowFavorites", "ShowParts", "ShowDanmaku", "ShowHistory" };
            Sprite[] tabIcons = { Icon("search"), Icon("user"), YamaSprite("folder"), YamaSprite("video"), Icon("danmaku"), Icon("history") };
            var tabToggles = new Toggle[tabKeys.Length];
            for (int i = 0; i < tabKeys.Length; i++)
            {
                var item = i == 0 ? menuTemplate.gameObject : Object.Instantiate(menuTemplate.gameObject, menu);
                item.name = tabKeys[i];
                ((RectTransform)item.transform).sizeDelta = new Vector2(100, 100);
                item.transform.Find("Background").GetComponent<Image>().sprite = tabIcons[i];
                Label(item.transform.Find("Label").GetComponent<Text>(), tabKeys[i]);
                var toggle = item.GetComponent<Toggle>();
                ClearPersistent(toggle.onValueChanged);
                UnityEventTools.AddStringPersistentListener(toggle.onValueChanged, _searchUdon.SendCustomEvent, tabEvents[i]);
                toggle.isOn = i == 0;
                tabToggles[i] = toggle;
            }

            var listView = BuildListView(body, scrollTemplate, modalExecute);
            var partsView = BuildPartsView(body, scrollTemplate, radioSection);
            var dmView = BuildDanmakuView(body, scrollTemplate, radioSection, stepSection);
            var histView = BuildHistoryView(body, scrollTemplate);
            Object.DestroyImmediate(scrollTemplate.gameObject);
            Object.DestroyImmediate(radioSection.gameObject);
            Object.DestroyImmediate(stepSection.gameObject);

            // Status line under the page body
            var status = MakeText(body, 0, 640, 1100, 30, 15, TextAnchor.MiddleRight, Soft, null);
            status.name = "Status";

            Set(_search, "_tabViews", new[] { listView, listView, listView, partsView, dmView, histView });
            Set(_search, "_tabToggles", tabToggles);
            Set(_search, "_status", status);

            pageGo.SetActive(false);
            return page;
        }

        static GameObject BuildListView(Transform body, Transform scrollTemplate, Transform modalExecute)
        {
            var view = Rect(body, "List View");
            Stretch(view, 0, 0, 0, 0);

            // Input bar: link icon, url field with an underline, pink action button
            var bar = Rect(view, "InputBar");
            Place(bar, 0, 0, 1100, 96);
            var icon = Rect(bar, "Icon");
            Place(icon, 4, 12, 32, 32);
            var iconImage = icon.gameObject.AddComponent<Image>();
            iconImage.sprite = YamaSprite("link");
            iconImage.color = Soft;
            var field = Rect(bar, "UrlInput");
            Place(field, 48, 4, 860, 48);
            var fieldImage = field.gameObject.AddComponent<Image>();
            fieldImage.color = new Color(1, 1, 1, 0);
            var line = Rect(field, "Underline");
            line.anchorMin = new Vector2(0, 0);
            line.anchorMax = new Vector2(1, 0);
            line.pivot = new Vector2(0.5f, 0);
            line.sizeDelta = new Vector2(0, 2);
            line.gameObject.AddComponent<Image>().color = new Color(0.4f, 0.4f, 0.4f, 1f);
            var fieldText = MakeText(field, 4, 6, 852, 40, 20, TextAnchor.MiddleLeft, Strong, null);
            fieldText.supportRichText = false;
            fieldText.horizontalOverflow = HorizontalWrapMode.Overflow;
            var input = field.gameObject.AddComponent<VRCUrlInputField>();
            input.textComponent = fieldText;
            input.targetGraphic = fieldImage;
            input.lineType = VRCUrlInputField.LineType.SingleLine;
            UnityEventTools.AddStringPersistentListener(input.onEndEdit, _searchUdon.SendCustomEvent, nameof(BiliSearch.Submit));
            OnPointerEnter(field.gameObject, _searchUdon, nameof(BiliSearch.RefillInput));

            var submit = Object.Instantiate(modalExecute.gameObject, bar);
            submit.name = "Submit";
            submit.SetActive(true);
            Place((RectTransform)submit.transform, 930, 4, 170, 48);
            var submitButton = submit.GetComponent<Button>();
            ClearPersistent(submitButton.onClick);
            UnityEventTools.AddStringPersistentListener(submitButton.onClick, _searchUdon.SendCustomEvent, nameof(BiliSearch.Submit));
            Label(submit.GetComponentInChildren<Text>(), "submit");

            var hint = MakeText(bar, 48, 58, 1050, 30, 15, TextAnchor.MiddleLeft, Soft, null);

            var listTitle = MakeText(view, 0, 96, 1100, 46, 28, TextAnchor.MiddleLeft, Strong, null);

            // Result rows in a YamaPlayer scroll view
            var scroll = CloneScroll(view, scrollTemplate, "Results", 142, 50);
            const int rows = 20;
            var rowObjs = new GameObject[rows];
            var titles = new Text[rows];
            var owners = new Text[rows];
            var metas = new Text[rows];
            var covers = new RawImage[rows];
            for (int i = 0; i < rows; i++)
            {
                var row = Row(scroll, "Row" + i, 72);
                var cover = Rect(row, "Cover");
                Place(cover, 4, 6, 107, 60);
                covers[i] = cover.gameObject.AddComponent<RawImage>();
                covers[i].color = new Color(0.15f, 0.15f, 0.15f, 1f);
                titles[i] = MakeText(row, 126, 6, 750, 36, 20, TextAnchor.MiddleLeft, Strong, null);
                var owner = IndexButton(row, 126, 42, 520, 26, "_OpenUploader", i);
                owners[i] = MakeText(owner.transform, 0, 0, 520, 26, 13, TextAnchor.MiddleLeft, Soft, null);
                metas[i] = MakeText(row, 650, 42, 226, 26, 13, TextAnchor.MiddleRight, Soft, null);
                ActionButton(row, 900, 6, "add-small", "queue", "_QueueItem", i);
                ActionButton(row, 984, 6, "play-small", "play", "_PlayItem", i);
                rowObjs[i] = row.gameObject;
            }

            // Pager
            var pager = Rect(view, "Pager");
            Place(pager, 0, 596, 1100, 44);
            var prev = StepButton(pager, 0, "left", nameof(BiliSearch.PrevPage));
            var pageText = MakeText(pager, 44, 2, 140, 40, 20, TextAnchor.MiddleCenter, Strong, null);
            var next = StepButton(pager, 188, "right", nameof(BiliSearch.NextPage));
            ActionButton(pager, 250, -8, "add-small", "queueAll", nameof(BiliSearch.QueueAll), -1, 160);

            Set(_search, "_inputBar", bar.gameObject);
            Set(_search, "_input", input);
            Set(_search, "_hint", hint);
            Set(_search, "_listTitle", listTitle);
            Set(_search, "_rows", rowObjs);
            Set(_search, "_rowTitles", titles);
            Set(_search, "_rowMeta", metas);
            Set(_search, "_rowOwners", owners);
            Set(_search, "_rowCovers", covers);
            Set(_search, "_pageText", pageText);
            Set(_search, "_prevButton", prev.gameObject);
            Set(_search, "_nextButton", next.gameObject);
            return view.gameObject;
        }

        static GameObject BuildPartsView(Transform body, Transform scrollTemplate, Transform radioSection)
        {
            var view = Rect(body, "Parts View");
            Stretch(view, 0, 0, 0, 0);

            var header = Object.Instantiate(radioSection.gameObject, view).transform as RectTransform;
            header.name = "Header";
            Place(header, 0, 0, 1100, 130);
            var title = header.Find("Title").GetComponent<Text>();
            title.text = "";
            Label(header.Find("Description").GetComponent<Text>(), "autoNextDesc");
            Toggle on, off;
            RadioPair(header.Find("ToggleGroup"), nameof(BiliSearch._OnAutoNextChanged), out on, out off);

            var scroll = CloneScroll(view, scrollTemplate, "Parts", 140, 50);
            const int rows = 20;
            var rowObjs = new GameObject[rows];
            var titles = new Text[rows];
            var marks = new GameObject[rows];
            for (int i = 0; i < rows; i++)
            {
                var row = Row(scroll, "Part" + i, 56);
                var button = IndexButton(row, 0, 0, 1080, 56, "_PlayPart", i);
                var mark = Rect(button.transform, "PlayingMark");
                Place(mark, 15, 13, 30, 30);
                var markImage = mark.gameObject.AddComponent<Image>();
                markImage.sprite = YamaSprite("music");
                ThemeColor(markImage);
                mark.gameObject.SetActive(false);
                titles[i] = MakeText(button.transform, 60, 8, 1000, 40, 20, TextAnchor.MiddleLeft, Strong, null);
                rowObjs[i] = row.gameObject;
                marks[i] = mark.gameObject;
            }

            var pager = Rect(view, "Pager");
            Place(pager, 0, 596, 1100, 44);
            StepButton(pager, 0, "left", nameof(BiliSearch.PrevParts));
            var pageText = MakeText(pager, 44, 2, 140, 40, 20, TextAnchor.MiddleCenter, Strong, null);
            StepButton(pager, 188, "right", nameof(BiliSearch.NextParts));

            Set(_search, "_partRows", rowObjs);
            Set(_search, "_partTitles", titles);
            Set(_search, "_partMarks", marks);
            Set(_search, "_partPageText", pageText);
            Set(_search, "_partHeader", title);
            Set(_search, "_autoNextOn", on);
            Set(_search, "_autoNextOff", off);
            return view.gameObject;
        }

        static GameObject BuildDanmakuView(Transform body, Transform scrollTemplate, Transform radioSection, Transform stepSection)
        {
            var view = Object.Instantiate(scrollTemplate.gameObject, body);
            view.name = "Danmaku View";
            Transform content = view.transform.Find("Viewport/Content");
            for (int i = content.childCount - 1; i >= 0; i--) Object.DestroyImmediate(content.GetChild(i).gameObject);

            string[] switches = { "dmOn", "dmRoll", "dmTop", "dmBottom", "dmSub" };
            var ons = new Toggle[switches.Length];
            var offs = new Toggle[switches.Length];
            for (int i = 0; i < switches.Length; i++)
            {
                var section = Object.Instantiate(radioSection.gameObject, content).transform;
                section.name = switches[i];
                Label(section.Find("Title").GetComponent<Text>(), switches[i]);
                Label(section.Find("Description").GetComponent<Text>(), switches[i] + "Desc");
                RadioPair(section.Find("ToggleGroup"), nameof(BiliSearch._OnSwitchChanged), out ons[i], out offs[i]);
            }

            string[] steps = { "dmSpeed", "dmFont", "dmOpacity", "dmArea", "dmStay" };
            string[] downs = { "SpeedDown", "FontDown", "OpacityDown", "AreaDown", "StayDown" };
            string[] ups = { "SpeedUp", "FontUp", "OpacityUp", "AreaUp", "StayUp" };
            var values = new Text[steps.Length];
            for (int i = 0; i < steps.Length; i++)
            {
                var section = Object.Instantiate(stepSection.gameObject, content).transform;
                section.name = steps[i];
                Label(section.Find("Title").GetComponent<Text>(), steps[i]);
                Label(section.Find("Description").GetComponent<Text>(), steps[i] + "Desc");
                Transform action = section.Find("Action");
                // Keep the single-step arrows, drop the double ones.
                Object.DestroyImmediate(action.Find("Button").gameObject);
                Object.DestroyImmediate(action.Find("Button (3)").gameObject);
                Rewire(action.Find("Button (1)").GetComponent<Button>(), downs[i]);
                Rewire(action.Find("Button (2)").GetComponent<Button>(), ups[i]);
                values[i] = action.Find("Text").GetComponent<Text>();
            }

            var reset = Object.Instantiate(stepSection.gameObject, content).transform;
            reset.name = "Reset";
            Object.DestroyImmediate(reset.Find("Description").gameObject);
            Object.DestroyImmediate(reset.Find("Action").gameObject);
            var resetTitle = reset.Find("Title").GetComponent<Text>();
            Label(resetTitle, "dmReset");
            resetTitle.raycastTarget = true;
            var resetButton = resetTitle.gameObject.AddComponent<Button>();
            resetButton.targetGraphic = resetTitle;
            UnityEventTools.AddStringPersistentListener(resetButton.onClick, _searchUdon.SendCustomEvent, nameof(BiliSearch.ResetDanmaku));

            Set(_search, "_switchOn", ons);
            Set(_search, "_switchOff", offs);
            Set(_search, "_settingValues", values);
            return view;
        }

        static GameObject BuildHistoryView(Transform body, Transform scrollTemplate)
        {
            var view = Rect(body, "History View");
            Stretch(view, 0, 0, 0, 0);
            MakeText(view, 0, 0, 900, 50, 28, TextAnchor.MiddleLeft, Strong, "tabHistory");
            ActionButton(view, 960, -4, "xmark", "clearHistory", nameof(BiliSearch.ClearHistory), -1, 140);

            var scroll = CloneScroll(view, scrollTemplate, "Entries", 60, 0);
            var rowObjs = new GameObject[10];
            var titles = new Text[10];
            for (int i = 0; i < 10; i++)
            {
                var row = Row(scroll, "Entry" + i, 60);
                var button = IndexButton(row, 0, 0, 1080, 60, "_OpenHistory", i);
                var icon = Rect(button.transform, "FolderMark");
                Place(icon, 15, 15, 30, 30);
                var image = icon.gameObject.AddComponent<Image>();
                image.sprite = YamaSprite("folder");
                image.color = Soft;
                titles[i] = MakeText(button.transform, 60, 10, 1000, 40, 20, TextAnchor.MiddleLeft, Strong, null);
                rowObjs[i] = row.gameObject;
            }
            Set(_search, "_historyRows", rowObjs);
            Set(_search, "_historyTitles", titles);
            return view.gameObject;
        }

        // --- YamaPlayer-style pieces ------------------------------------------------------------------

        static readonly Color Strong = new Color(1f, 1f, 1f, 0.75f);  // FFFFFFBF, YamaPlayer's text colour
        static readonly Color Soft = new Color(0.933f, 0.933f, 0.933f, 0.6f);

        // A copy of the Settings page's scroll view (mask, thin grey scrollbar) placed in a band of the view.
        static RectTransform CloneScroll(Transform parent, Transform template, string name, float top, float bottom)
        {
            var go = Object.Instantiate(template.gameObject, parent);
            go.name = name;
            go.SetActive(true);
            Stretch((RectTransform)go.transform, 0, top, 0, bottom);
            Transform content = go.transform.Find("Viewport/Content");
            for (int i = content.childCount - 1; i >= 0; i--) Object.DestroyImmediate(content.GetChild(i).gameObject);
            var layout = content.GetComponent<VerticalLayoutGroup>();
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.spacing = 2;
            layout.padding = new RectOffset(0, 16, 0, 0);
            go.GetComponent<ScrollRect>().verticalNormalizedPosition = 1f;
            return (RectTransform)content;
        }

        static RectTransform Row(Transform content, string name, float height)
        {
            var row = Rect(content, name);
            var le = row.gameObject.AddComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = height;
            return row;
        }

        // The "icon above caption" buttons of YamaPlayer's track rows (Play, Add to queue, ...).
        static Button ActionButton(Transform parent, float x, float y, string sprite, string captionKey, string eventName, int index, float width = 80)
        {
            var rt = Rect(parent, captionKey);
            Place(rt, x, y, width, 60);
            var bg = rt.gameObject.AddComponent<Image>();
            bg.color = new Color(1, 1, 1, 0);
            var button = rt.gameObject.AddComponent<Button>();
            button.targetGraphic = bg;
            var icon = Rect(rt, "Image");
            icon.anchorMin = icon.anchorMax = new Vector2(0.5f, 0.5f);
            icon.anchoredPosition = new Vector2(0, 10);
            icon.sizeDelta = new Vector2(36, 36);
            var iconImage = icon.gameObject.AddComponent<Image>();
            iconImage.sprite = YamaSprite(sprite);
            iconImage.color = Strong;
            var caption = Rect(rt, "Text");
            caption.anchorMin = caption.anchorMax = new Vector2(0.5f, 0.5f);
            caption.anchoredPosition = new Vector2(0, -17);
            caption.sizeDelta = new Vector2(width * 10, 220);
            caption.localScale = new Vector3(0.1f, 0.1f, 1);
            var text = caption.gameObject.AddComponent<Text>();
            text.font = _font;
            text.fontSize = 125;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.color = Strong;
            text.raycastTarget = false;
            Label(text, captionKey);
            if (index >= 0) AddIndexTrigger(button, eventName, index);
            else UnityEventTools.AddStringPersistentListener(button.onClick, _searchUdon.SendCustomEvent, eventName);
            return button;
        }

        // The small arrow buttons of YamaPlayer's offset control.
        static Button StepButton(Transform parent, float x, string sprite, string eventName)
        {
            var rt = Rect(parent, sprite);
            Place(rt, x, 2, 40, 40);
            var image = rt.gameObject.AddComponent<Image>();
            image.sprite = YamaSprite(sprite);
            image.color = Color.white;
            var button = rt.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            UnityEventTools.AddStringPersistentListener(button.onClick, _searchUdon.SendCustomEvent, eventName);
            return button;
        }

        // A transparent, colour-tinted click area that reports its row index.
        static Button IndexButton(Transform parent, float x, float y, float w, float h, string eventName, int index)
        {
            var rt = Rect(parent, eventName.TrimStart('_'));
            Place(rt, x, y, w, h);
            var bg = rt.gameObject.AddComponent<Image>();
            bg.color = new Color(1, 1, 1, 0);
            var button = rt.gameObject.AddComponent<Button>();
            button.targetGraphic = bg;
            var colors = button.colors;
            colors.normalColor = new Color(1, 1, 1, 0);
            colors.highlightedColor = new Color(1, 1, 1, 0.06f);
            colors.pressedColor = new Color(1, 1, 1, 0.12f);
            colors.selectedColor = new Color(1, 1, 1, 0);
            colors.colorMultiplier = 1;
            button.colors = colors;
            bg.color = Color.white;
            AddIndexTrigger(button, eventName, index);
            return button;
        }

        static void AddIndexTrigger(Button button, string eventName, int index)
        {
            var trigger = UdonSharpUndo.AddComponent<BiliIndexButton>(button.gameObject);
            trigger.Target = _search;
            trigger.Index = index;
            trigger.EventName = eventName;
            UdonSharpEditorUtility.CopyProxyToUdon(trigger);
            var udon = UdonSharpEditorUtility.GetBackingUdonBehaviour(trigger);
            UnityEventTools.AddStringPersistentListener(button.onClick, udon.SendCustomEvent, nameof(BiliIndexButton.OnClick));
        }

        // Turns a cloned YamaPlayer radio group into an Off / On pair reporting to `eventName`.
        static void RadioPair(Transform group, string eventName, out Toggle on, out Toggle off)
        {
            var toggles = group.GetComponentsInChildren<Toggle>(true);
            off = toggles[0];
            on = toggles[1];
            off.name = "Off";
            on.name = "On";
            foreach (var t in toggles)
            {
                ClearPersistent(t.onValueChanged);
                var trigger = t.GetComponent<UnityEngine.EventSystems.EventTrigger>();
                if (trigger != null) Object.DestroyImmediate(trigger);
            }
            Label(off.transform.Find("Label").GetComponent<Text>(), "off");
            Label(on.transform.Find("Label").GetComponent<Text>(), "on");
            UnityEventTools.AddStringPersistentListener(on.onValueChanged, _searchUdon.SendCustomEvent, eventName);
        }

        static void Rewire(Button button, string eventName)
        {
            ClearPersistent(button.onClick);
            UnityEventTools.AddStringPersistentListener(button.onClick, _searchUdon.SendCustomEvent, eventName);
        }

        // Follows the player's theme colour, like YamaPlayer's own accents.
        static void ThemeColor(Image image)
        {
            var uiController = image.GetComponentInParent<Yamadev.YamaStream.UI.UIController>(true);
            var color = UdonSharpUndo.AddComponent<Yamadev.YamaStream.UI.UIColor>(image.gameObject);
            Set(color, "_uiController", uiController);
            UdonSharpEditorUtility.CopyProxyToUdon(color);
            image.color = new Color(0.94f, 0.384f, 0.573f, 1f);
        }

        // --- UI helpers ----------------------------------------------------------------------------

        static RectTransform Rect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        static void Stretch(RectTransform rt, float left, float top, float right, float bottom)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        // Top-left based placement inside a parent, in parent units.
        static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
        }

        static Text MakeText(Transform parent, float x, float y, float w, float h, int size, TextAnchor align, Color color, string key)
        {
            var rt = Rect(parent, key ?? "Text");
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w / S, h / S);
            rt.localScale = new Vector3(S, S, 1);
            var text = rt.gameObject.AddComponent<Text>();
            text.font = _font;
            text.fontSize = Mathf.RoundToInt(size / S);
            text.alignment = align;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            if (key != null) Label(text, key);
            return text;
        }

        static void Label(Text text, string key)
        {
            Labels.Add(text);
            LabelKeys.Add(key);
        }

        static void Set(object target, string field, object value)
        {
            var f = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f == null) throw new System.MissingFieldException(target.GetType().Name, field);
            f.SetValue(target, value);
        }
    }
}
