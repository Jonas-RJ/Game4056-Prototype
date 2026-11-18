#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using AutonomousCompanion.Demo;
using AutonomousCompanion.Demo.Testing;

namespace AutonomousCompanion.Editor
{
    /// <summary>Builds only the separate demo scene. Invoked by Tools/Autonomous Companion/Build Demo Scene.</summary>
    public static class DemoSceneBuilder
    {
        private const string ScenePath = "Assets/Scenes/AutonomousCoopDemo.unity";
        private static Sprite square;
        private static Material spriteMaterial;
        private static TMP_FontAsset font;
        [MenuItem("Tools/Autonomous Companion/Build Demo Scene")]
        public static void Build()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Stop Play Mode before building.");
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("Save current scene edits before building the demo.");
            EnsureFolder("Assets/AutonomousCompanion/Demo/Generated"); EnsureFolder("Assets/Scenes");
            PrepareVisualAssets();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "AutonomousCoopDemo";
            var cameraGo = new GameObject("Main Camera"); cameraGo.tag = "MainCamera";
            var camera = cameraGo.AddComponent<Camera>(); camera.orthographic = true; camera.orthographicSize = 6f;
            camera.backgroundColor = new Color(.035f, .055f, .095f); camera.clearFlags = CameraClearFlags.SolidColor;
            camera.transform.position = new Vector3(.75f, 1.6f, -10f);
            cameraGo.AddComponent<AudioListener>(); cameraGo.AddComponent<DemoCameraFit>();
            // Geometry is intentionally clear: authored links exactly match these surfaces and doors.
            Rect("MainPlatform", new Vector2(-2.25f, -.4f), new Vector2(13.5f, .8f), Hex("253950"), true);
            Rect("ExitPlatform", new Vector2(8.4f, -.4f), new Vector2(4.2f, .8f), Hex("253950"), true);
            Rect("MainPlatformLip", new Vector2(-2.25f, -.025f), new Vector2(13.5f, .05f), Hex("7898AE"));
            Rect("ExitPlatformLip", new Vector2(8.4f, -.025f), new Vector2(4.2f, .05f), Hex("7898AE"));
            Rect("LeftBoundary", new Vector2(-9.25f, 1), new Vector2(.5f, 4), Hex("18283C"), true);
            Rect("RightBoundary", new Vector2(10.75f, 1), new Vector2(.5f, 4), Hex("18283C"), true);
            for (int i = 0; i < 7; i++)
                Rect("PitWarning" + i, new Vector2(4.65f + i * .25f, -1.2f), new Vector2(.12f, .18f), Hex("ED776B"));
            Label("GAP", new Vector2(5.4f, -1.8f), 3.5f, Hex("ED776B"), .27f);
            for (int i = 0; i < 5; i++)
                Rect("BackgroundLine" + i, new Vector2(.75f, 1.7f + i * .7f), new Vector2(19.2f, .015f), Hex("142238"), false, -10);

            var root = new GameObject("DemoLevel");
            var world = root.AddComponent<CompanionWorld>();
            var executor = root.AddComponent<CompanionExecutor>(); executor.world = world;
            var client = root.AddComponent<DecisionHttpClient>(); client.endpoint = "http://127.0.0.1:8765/decision";
            client.enabled = false;
            var brain = root.AddComponent<AutonomousCompanionBrain>();
            brain.world = world; brain.executor = executor; brain.client = client;
            // Local planning needs no network service or model download.
            var planner = root.AddComponent<DemoLocalPlanner>(); planner.world = world;
            brain.localPlanner = planner; brain.decisionMode = AutonomousCompanionBrain.DecisionMode.LocalPlanner;
            brain.enabled = false;
            brain.eventDebounceSeconds = .2f; brain.periodicDecisionSeconds = .5f;
            brain.goal = "Both characters reach their own exits alive: player to exit_player, companion to exit_companion. " +
                "The human controls player and gives no task commands. switch_a is a pressure switch: it opens door_a only while occupied. " +
                "switch_b permanently latches door_a and door_b open when stepped on. Doors block both actors when closed. " +
                "The gap before the exits can be crossed using the authored jump route. Infer useful cooperation from these rules and observed state.";
            var level = root.AddComponent<DemoLevel>();
            var player = Actor("Player", new Vector2(-5, .65f), Hex("FFB28B"), true);
            var companion = Actor("Companion", new Vector2(-7, .65f), Hex("6AD8F0"), false);
            world.player = player; world.companion = companion;
            level.player = player; level.companion = companion; level.world = world; level.brain = brain;
            level.playerSpawn = new Vector2(-5, .65f); level.companionSpawn = new Vector2(-7, .65f);

            float[] xs = { -8, -7, -5, -2.8f, -1.2f, 0, 2.2f, 3.8f, 4.1f, 6.7f, 8, 9 };
            var nodes = new NavigationNode[xs.Length]; var navigationRoot = new GameObject("AuthoredNavigation");
            for (int i = 0; i < xs.Length; i++)
            {
                var go = new GameObject("Node_" + i); go.transform.SetParent(navigationRoot.transform);
                go.transform.position = new Vector3(xs[i], .65f); nodes[i] = go.AddComponent<NavigationNode>();
            }
            player.nodes = nodes; companion.nodes = nodes;
            var doorA = Door("door_a", -2f, Hex("F5B65F"), world);
            var doorB = Door("door_b", 3f, Hex("6DE3BC"), world);
            for (int i = 0; i < nodes.Length; i++)
            {
                var links = new List<NavigationLink>();
                if (i > 0) links.Add(Link(nodes[i - 1], i - 1, doorA, doorB));
                if (i < nodes.Length - 1) links.Add(Link(nodes[i + 1], i, doorA, doorB));
                nodes[i].links = links.ToArray();
            }
            var switchA = Switch("switch_a", -8f, Hex("F5B65F"), nodes[0], world, false, new[] { doorA });
            var switchB = Switch("switch_b", 0f, Hex("6DE3BC"), nodes[5], world, true, new[] { doorA, doorB });
            var release = switchA.gameObject.AddComponent<DemoSafeReleaseCondition>();
            release.latchedDoor = doorA; release.player = player; release.world = world; release.safePlayerX = -1.4f;
            switchA.safeReleaseCondition = release;
            var companionExit = Exit("exit_companion", 8f, Hex("6AD8F0"), nodes[10], companion, world);
            var playerExit = Exit("exit_player", 9f, Hex("FFB28B"), nodes[11], player, world);
            level.switchA = switchA; level.switchB = switchB; level.doorA = doorA; level.doorB = doorB;
            level.companionExit = companionExit; level.playerExit = playerExit;
            var harness = root.AddComponent<CooperationPlaytestHarness>(); harness.level = level; harness.executor = executor;

            Label("A", new Vector2(-8, -.7f), 1f, Hex("F5B65F"), .5f);
            Label("HOLD TO OPEN", new Vector2(-8, -1.15f), 3f, Hex("B7C6D5"), .22f);
            Label("B", new Vector2(0, -.7f), 1f, Hex("6DE3BC"), .5f);
            Label("LATCH BOTH DOORS", new Vector2(0, -1.15f), 3.5f, Hex("B7C6D5"), .22f);
            Label("A", new Vector2(-2, 3.1f), 1f, Hex("F5B65F"), .38f);
            Label("B", new Vector2(3, 3.1f), 1f, Hex("6DE3BC"), .38f);
            Label("AI", new Vector2(8, -.7f), 1.5f, Hex("6AD8F0"), .26f);
            Label("YOU", new Vector2(9, -.7f), 1.5f, Hex("FFB28B"), .26f);
            MakeHud(level, executor, client);
            CoopCampaignSetup.ConfigureScene(scene, true);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log("AUTONOMOUS_DEMO_BUILT " + ScenePath);
        }
        private static NavigationLink Link(NavigationNode destination, int segment, DemoDoor a, DemoDoor b)
        { return new NavigationLink { destination = destination, traversal = segment == 8 ? "jump" : "walk", gate = segment == 3 ? a : segment == 6 ? b : null }; }
        private static DemoActor Actor(string name, Vector2 position, Color color, bool keyboard)
        {
            var go = Rect(name, position, new Vector2(.65f, 1.2f), color, false, 5);
            // Keep transform scale=1 so the adapter's physical dimensions remain world-space constants.
            var renderer = go.GetComponent<SpriteRenderer>(); renderer.drawMode = SpriteDrawMode.Sliced; renderer.size = new Vector2(.65f, 1.2f); go.transform.localScale = Vector3.one;
            var box = go.AddComponent<BoxCollider2D>(); box.size = new Vector2(.65f, 1.2f);
            var rb = go.AddComponent<Rigidbody2D>(); rb.gravityScale = 2.2f; rb.constraints = RigidbodyConstraints2D.FreezeRotation;
            var material = new PhysicsMaterial2D(name + "_Frictionless") { friction = 0, bounciness = 0 };
            string materialPath = "Assets/AutonomousCompanion/Demo/Generated/" + name + "Physics.physicsMaterial2D";
            var existing = AssetDatabase.LoadAssetAtPath<PhysicsMaterial2D>(materialPath);
            if (existing == null) { AssetDatabase.CreateAsset(material, materialPath); existing = material; }
            else UnityEngine.Object.DestroyImmediate(material);
            box.sharedMaterial = existing;
            var actor = go.AddComponent<DemoActor>(); actor.allowKeyboard = keyboard; go.layer = 8;
            var left = Rect("EyeL", position + new Vector2(-.13f, .2f), new Vector2(.07f, .11f), Hex("15243B"), false, 6);
            var right = Rect("EyeR", position + new Vector2(.13f, .2f), new Vector2(.07f, .11f), Hex("15243B"), false, 6);
            left.transform.SetParent(go.transform, true); right.transform.SetParent(go.transform, true);
            return actor;
        }
        private static DemoDoor Door(string id, float x, Color color, CompanionWorld world)
        {
            var go = Rect(id, new Vector2(x, 1.35f), new Vector2(.3f, 2.7f), color, true, 3);
            var door = go.AddComponent<DemoDoor>(); door.blocker = go.GetComponent<BoxCollider2D>(); door.world = world;
            var observable = go.AddComponent<DemoDoorTarget>(); observable.targetId = id; observable.kind = "door"; observable.door = door;
            door.observable = observable; door.closedColor = color; door.openColor = new Color(color.r, color.g, color.b, .18f);
            Rect(id + "Cap", new Vector2(x, 2.8f), new Vector2(.65f, .13f), color, false, 4);
            return door;
        }
        private static DemoPressureSwitch Switch(string id, float x, Color color, NavigationNode node, CompanionWorld world, bool latch, DemoDoor[] doors)
        {
            var go = Rect(id, new Vector2(x, .1f), new Vector2(.95f, .2f), color, false, 2);
            var trigger = go.AddComponent<BoxCollider2D>(); trigger.isTrigger = true; trigger.size = new Vector2(1, 1.5f);
            var result = go.AddComponent<DemoPressureSwitch>(); result.targetId = id; result.kind = latch ? "latching_switch" : "switch";
            result.arrivalNode = node; result.trigger = trigger; result.world = world; result.latchOnPress = latch; result.controlledDoors = doors;
            var controls = new List<string>(); foreach (var door in doors) controls.Add(door.observable.targetId); result.controls = controls.ToArray();
            return result;
        }
        private static DemoExit Exit(string id, float x, Color color, NavigationNode node, DemoActor actor, CompanionWorld world)
        {
            var go = Rect(id, new Vector2(x, .75f), new Vector2(.85f, 1.5f), new Color(color.r, color.g, color.b, .15f), false, 0);
            Rect(id + "Top", new Vector2(x, 1.55f), new Vector2(.85f, .06f), color, false, 1);
            var collider = go.AddComponent<BoxCollider2D>(); collider.isTrigger = true;
            var exit = go.AddComponent<DemoExit>(); exit.targetId = id; exit.kind = "exit"; exit.arrivalNode = node;
            exit.requiredActor = actor; exit.trigger = collider; exit.world = world;
            return exit;
        }
        private static GameObject Rect(string name, Vector2 position, Vector2 size, Color color, bool solid = false, int order = 0)
        {
            var go = new GameObject(name); go.transform.position = position; go.transform.localScale = new Vector3(size.x, size.y, 1);
            var sr = go.AddComponent<SpriteRenderer>(); sr.sprite = square; sr.sharedMaterial = spriteMaterial; sr.color = color; sr.sortingOrder = order;
            if (solid) go.AddComponent<BoxCollider2D>(); return go;
        }
        private static void Label(string value, Vector2 position, float width, Color color, float size)
        {
            var go = new GameObject("Label_" + value); go.transform.position = new Vector3(position.x, position.y, -1);
            var text = go.AddComponent<TextMeshPro>(); text.font = font; text.text = value; text.fontSize = size * 10f;
            text.color = color; text.alignment = TextAlignmentOptions.Center; text.textWrappingMode = TextWrappingModes.NoWrap;
            text.rectTransform.sizeDelta = new Vector2(width, .6f); text.GetComponent<MeshRenderer>().sortingOrder = 20;
        }
        private static void MakeHud(DemoLevel level, CompanionExecutor executor, DecisionHttpClient client)
        {
            var canvasGo = new GameObject("DemoHUD", typeof(RectTransform)); var canvas = canvasGo.AddComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.AddComponent<UnityEngine.UI.CanvasScaler>(); scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
            HudText(canvasGo.transform, "TOGETHER / 01", new Vector2(80, -60), new Vector2(1200, 80), 52, Hex("EAF4FC"));
            HudText(canvasGo.transform, "AUTONOMOUS COMPANION  /  COOPERATIVE PLATFORMING", new Vector2(84, -144), new Vector2(1600, 45), 22, Hex("86A4BC"));
            HudText(canvasGo.transform, "MOVE  A / D or arrows     JUMP  Space / W     RESET  R", new Vector2(84, -940), new Vector2(1700, 45), 24, Hex("C7D8E7"));
            var status = HudText(canvasGo.transform, "Observing...", new Vector2(84, -995), new Vector2(1720, 40), 21, Hex("6AD8F0"));
            var hud = canvasGo.AddComponent<DemoStatusHud>(); hud.level = level; hud.executor = executor; hud.client = client; hud.label = status;
        }
        private static TextMeshProUGUI HudText(Transform parent, string value, Vector2 position, Vector2 size, float fontSize, Color color)
        {
            var go = new GameObject("Text", typeof(RectTransform)); go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>(); rect.anchorMin = rect.anchorMax = new Vector2(0, 1); rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = position; rect.sizeDelta = size;
            var text = go.AddComponent<TextMeshProUGUI>(); text.font = font; text.text = value; text.fontSize = fontSize; text.color = color;
            text.raycastTarget = false; text.textWrappingMode = TextWrappingModes.NoWrap; return text;
        }
        private static Color Hex(string value) { ColorUtility.TryParseHtmlString("#" + value, out var color); return color; }
        private static void EnsureFolder(string path)
        {
            var parts = path.Split('/'); string current = parts[0];
            for (int i = 1; i < parts.Length; i++) { string next = current + "/" + parts[i]; if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]); current = next; }
        }
        private static void PrepareVisualAssets()
        {
            string texturePath = "Assets/AutonomousCompanion/Demo/Generated/Square.asset";
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            if (texture == null)
            {
                texture = new Texture2D(2, 2); texture.SetPixels(new[] { Color.white, Color.white, Color.white, Color.white }); texture.Apply(); texture.filterMode = FilterMode.Point;
                AssetDatabase.CreateAsset(texture, texturePath);
                square = Sprite.Create(texture, new Rect(0, 0, 2, 2), new Vector2(.5f, .5f), 2, 0, SpriteMeshType.FullRect);
                square.name = "Square"; AssetDatabase.AddObjectToAsset(square, texture);
            }
            else foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(texturePath)) if (asset is Sprite sprite) square = sprite;
            if (square == null) throw new InvalidOperationException("Generated square sprite is missing.");
            string materialPath = "Assets/AutonomousCompanion/Demo/Generated/UnlitSprite.mat";
            spriteMaterial = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (spriteMaterial == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
                if (shader == null) throw new InvalidOperationException("URP 2D unlit sprite shader is missing.");
                spriteMaterial = new Material(shader); AssetDatabase.CreateAsset(spriteMaterial, materialPath);
            }
            var fonts = AssetDatabase.FindAssets("t:TMP_FontAsset");
            if (fonts.Length == 0) { TMP_PackageResourceImporter.ImportResources(true, false, false); AssetDatabase.Refresh(); fonts = AssetDatabase.FindAssets("t:TMP_FontAsset"); }
            foreach (var guid in fonts)
            {
                var asset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetDatabase.GUIDToAssetPath(guid));
                if (asset != null && asset.name.Contains("LiberationSans")) { font = asset; break; }
            }
            if (font == null && fonts.Length > 0) font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetDatabase.GUIDToAssetPath(fonts[0]));
            if (font == null) throw new InvalidOperationException("TMP essential fonts still importing; run Build again after import finishes.");
        }
    }
}
#endif
