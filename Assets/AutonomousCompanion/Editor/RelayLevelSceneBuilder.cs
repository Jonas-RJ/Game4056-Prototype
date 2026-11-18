#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using AutonomousCompanion.Demo;
using AutonomousCompanion.Demo.Testing;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AutonomousCompanion.Editor
{
    public sealed class RelayLevelSceneBuilder
    {
        public const string ScenePath = "Assets/Scenes/CoopRelayLevel.unity";
        private const string GeneratedPath = "Assets/AutonomousCompanion/Demo/Generated/";
        private const float RidgeHeight = .7f;
        private static readonly Color PlayerColor = Hex("FFB28B");
        private static readonly Color CompanionColor = Hex("6AD8F0");
        private static readonly Color SwitchAColor = Hex("F5B65F");
        private static readonly Color SwitchBColor = Hex("6DE3BC");
        private static readonly Color TextColor = Hex("EAF4FC");
        private static readonly Color SecondaryTextColor = Hex("ADC2D5");

        private readonly Sprite square;
        private readonly Material spriteMaterial;
        private readonly TMP_FontAsset font;

        private RelayLevelSceneBuilder()
        {
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(GeneratedPath + "Square.asset"))
            {
                if (asset is Sprite sprite) { square = sprite; break; }
            }
            spriteMaterial = Load<Material>(GeneratedPath + "UnlitSprite.mat");
            font = Load<TMP_FontAsset>("Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");
            if (square == null) throw new InvalidOperationException("The shared demo square sprite is missing.");
        }

        [MenuItem("Tools/Autonomous Companion/Build Ridge Relay Level")]
        public static void Build()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Stop Play Mode before building Ridge Relay.");
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i).isDirty)
                    throw new InvalidOperationException("Save current scene edits before building Ridge Relay.");
            }

            var builder = new RelayLevelSceneBuilder();
            var playerMaterial = Load<PhysicsMaterial2D>(GeneratedPath + "PlayerPhysics.physicsMaterial2D");
            var companionMaterial = Load<PhysicsMaterial2D>(GeneratedPath + "CompanionPhysics.physicsMaterial2D");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            builder.BuildScene(playerMaterial, companionMaterial);
            CoopCampaignSetup.ConfigureScene(scene, false);
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new InvalidOperationException("Could not save Ridge Relay to " + ScenePath);
            Debug.Log("RIDGE_RELAY_BUILT " + ScenePath);
        }

        private void BuildScene(PhysicsMaterial2D playerMaterial, PhysicsMaterial2D companionMaterial)
        {
            MakeCamera();
            MakeTerrain();

            var root = new GameObject("RelayLevel");
            var world = root.AddComponent<CompanionWorld>();
            var level = root.AddComponent<DemoLevel>();
            level.world = world;
            level.playerSpawn = new Vector2(-6f, .65f);
            level.companionSpawn = new Vector2(-8f, .65f);

            var actors = Child(root.transform, "Actors").transform;
            level.player = MakeActor(actors, "Player", level.playerSpawn, PlayerColor, playerMaterial);
            level.companion = MakeActor(actors, "Companion", level.companionSpawn, CompanionColor, companionMaterial);
            world.player = level.player;
            world.companion = level.companion;

            var mechanisms = Child(root.transform, "Mechanisms").transform;
            level.doorA = MakeDoor(mechanisms, "door_a", "A", -5f, 0f, SwitchAColor, world);
            level.doorB = MakeDoor(mechanisms, "door_b", "B", 2.8f, RidgeHeight, SwitchBColor, world);

            var nodes = MakeNavigation(root.transform, level);
            level.player.nodes = nodes;
            level.companion.nodes = nodes;
            level.switchA = MakeSwitch(mechanisms, "switch_a", "HOLD A", -9f, 0f, SwitchAColor,
                nodes[0], world, false, level.doorA);
            level.switchB = MakeSwitch(mechanisms, "switch_b", "LATCH B", 1f, RidgeHeight, SwitchBColor,
                nodes[7], world, true, level.doorA, level.doorB);
            var release = level.switchA.gameObject.AddComponent<DemoSafeReleaseCondition>();
            release.latchedDoor = level.doorA;
            release.player = level.player;
            release.world = world;
            release.safePlayerX = -1f;
            level.switchA.safeReleaseCondition = release;

            level.companionExit = MakeExit(mechanisms, "exit_companion", "PARTNER", nodes[11],
                CompanionColor, level.companion, world);
            level.playerExit = MakeExit(mechanisms, "exit_player", "PLAYER", nodes[12],
                PlayerColor, level.player, world);

            root.AddComponent<RelayManualControls>().level = level;
            root.AddComponent<RelayLevelPlaytestHarness>().level = level;
            MakeHud(level);
        }

        private static void MakeCamera()
        {
            var go = new GameObject("Main Camera");
            go.tag = "MainCamera";
            go.transform.position = new Vector3(.5f, 1.55f, -10f);
            var camera = go.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = 6.75f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Hex("0A1322");
            go.AddComponent<AudioListener>();
            var fit = go.AddComponent<DemoCameraFit>();
            fit.centerX = .5f;
            fit.minimumHalfWidth = 12f;
            fit.minimumVerticalHalf = 5.8f;
        }

        private void MakeTerrain()
        {
            var terrain = new GameObject("RidgeTerrain").transform;
            var guides = Child(terrain, "Background").transform;
            for (int i = 0; i < 7; i++)
            {
                Rectangle(guides, "Guide_" + i, new Vector2(.5f, i * .7f),
                    new Vector2(21.5f, .012f), Hex("17283C"), order: -10);
            }

            MakePlatform(terrain, "StartPlatform", -10f, -3.5f, 0f);
            MakePlatform(terrain, "RaisedPlatform", -1.8f, 3.8f, RidgeHeight);
            MakePlatform(terrain, "ExitPlatform", 5.5f, 11f, 0f);
            Rectangle(terrain, "LeftBoundary", new Vector2(-10.24f, 1.15f),
                new Vector2(.48f, 3.7f), Hex("192A41"), solid: true);
            Rectangle(terrain, "RightBoundary", new Vector2(11.24f, 1.15f),
                new Vector2(.48f, 3.7f), Hex("192A41"), solid: true);
            MakePitMarkings(terrain, "FirstGap", -3.5f, -1.8f);
            MakePitMarkings(terrain, "SecondGap", 3.8f, 5.5f);
        }

        private void MakePlatform(Transform parent, string name, float left, float right, float top)
        {
            const float bottom = -2.5f;
            float x = (left + right) * .5f;
            float width = right - left;
            Rectangle(parent, name, new Vector2(x, (top + bottom) * .5f),
                new Vector2(width, top - bottom), Hex("182A41"), solid: true);
            Rectangle(parent, name + "Band", new Vector2(x, top - .12f),
                new Vector2(width, .24f), Hex("314761"), order: 1);
            Rectangle(parent, name + "Lip", new Vector2(x, top - .024f),
                new Vector2(width, .048f), Hex("7795AC"), order: 2);
            Rectangle(parent, name + "Base", new Vector2(x, bottom + .03f),
                new Vector2(width, .06f), Hex("233C55"), order: 1);
        }

        private void MakePitMarkings(Transform parent, string name, float left, float right)
        {
            Color warning = Hex("ED8C7A");
            float center = (left + right) * .5f;
            var markings = Child(parent, name).transform;
            for (int i = 0; i < 5; i++)
            {
                float x = center + (i - 2) * .22f;
                var stripe = Rectangle(markings, "Stripe_" + i, new Vector2(x, -1.4f),
                    new Vector2(.07f, .21f), warning);
                stripe.transform.localRotation = Quaternion.Euler(0f, 0f, -32f);
            }
            Label(markings, "Jump", "JUMP", new Vector2(center, -1.82f), right - left, .29f, warning);
        }

        private DemoActor MakeActor(Transform parent, string name, Vector2 spawn, Color color, PhysicsMaterial2D material)
        {
            var size = new Vector2(.65f, 1.2f);
            var go = Rectangle(parent, name, spawn, size, color, order: 5);
            go.transform.localScale = Vector3.one;
            var visual = go.GetComponent<SpriteRenderer>();
            visual.drawMode = SpriteDrawMode.Sliced;
            visual.size = size;
            var box = go.AddComponent<BoxCollider2D>();
            box.size = size;
            box.sharedMaterial = material;
            var body = go.AddComponent<Rigidbody2D>();
            body.gravityScale = 2.2f;
            body.constraints = RigidbodyConstraints2D.FreezeRotation;
            go.layer = 8;
            var actor = go.AddComponent<DemoActor>();
            actor.allowKeyboard = false;
            actor.arrivalTolerance = .22f;
            Color eyeColor = Hex("15243B");
            var eyeSize = new Vector2(.075f, .11f);
            Rectangle(go.transform, "EyeLeft", new Vector2(-.13f, .2f), eyeSize, eyeColor, order: 6);
            Rectangle(go.transform, "EyeRight", new Vector2(.13f, .2f), eyeSize, eyeColor, order: 6);
            return actor;
        }

        private static NavigationNode[] MakeNavigation(Transform parent, DemoLevel level)
        {
            const float low = .65f;
            const float high = low + RidgeHeight;
            Vector2[] points =
            {
                new(-9f, low), new(-8f, low), new(-6f, low), new(-5.8f, low), new(-4.3f, low), new(-3.9f, low),
                new(-1.4f, high), new(1f, high), new(2f, high), new(3.4f, high),
                new(5.9f, low), new(8f, low), new(9.5f, low)
            };
            var route = Child(parent, "AuthoredNavigation").transform;
            var nodes = new NavigationNode[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                var go = Child(route, "Node_" + i);
                go.transform.localPosition = points[i];
                nodes[i] = go.AddComponent<NavigationNode>();
            }
            for (int i = 0; i < nodes.Length; i++)
            {
                var links = new List<NavigationLink>(2);
                if (i > 0) links.Add(Link(nodes[i - 1], i - 1, level));
                if (i < nodes.Length - 1) links.Add(Link(nodes[i + 1], i, level));
                nodes[i].links = links.ToArray();
            }
            return nodes;
        }

        private static NavigationLink Link(NavigationNode destination, int segment, DemoLevel level)
        {
            var link = new NavigationLink { destination = destination };
            if (segment == 5 || segment == 9) link.traversal = "jump";
            if (segment == 3) link.gate = level.doorA;
            if (segment == 8) link.gate = level.doorB;
            return link;
        }

        private DemoDoor MakeDoor(Transform parent, string id, string label, float x, float groundY, Color color,
            CompanionWorld world)
        {
            const float height = 2.7f;
            var go = Rectangle(parent, id, new Vector2(x, groundY + height * .5f),
                new Vector2(.3f, height), color, solid: true, order: 3);
            var door = go.AddComponent<DemoDoor>();
            door.blocker = go.GetComponent<BoxCollider2D>();
            door.visual = go.GetComponent<SpriteRenderer>();
            door.world = world;
            door.closedColor = color;
            door.openColor = WithAlpha(color, .2f);
            var observable = go.AddComponent<DemoDoorTarget>();
            observable.targetId = id;
            observable.kind = "door";
            observable.door = door;
            door.observable = observable;
            Rectangle(parent, id + "Cap", new Vector2(x, groundY + height + .08f),
                new Vector2(.72f, .12f), color, order: 4);
            Label(parent, id + "Label", label, new Vector2(x, groundY + height + .41f), 1f, .35f, color);
            return door;
        }

        private DemoPressureSwitch MakeSwitch(Transform parent, string id, string label, float x, float groundY,
            Color color, NavigationNode node, CompanionWorld world, bool latch, params DemoDoor[] doors)
        {
            var go = Rectangle(parent, id, new Vector2(x, groundY + .1f),
                new Vector2(.95f, .2f), color, order: 3);
            var trigger = go.AddComponent<BoxCollider2D>();
            trigger.isTrigger = true;
            trigger.size = new Vector2(1f, 1.5f);
            var plate = go.AddComponent<DemoPressureSwitch>();
            plate.trigger = trigger;
            plate.visual = go.GetComponent<SpriteRenderer>();
            plate.targetId = id;
            plate.kind = latch ? "latching_switch" : "switch";
            plate.arrivalNode = node;
            plate.world = world;
            plate.latchOnPress = latch;
            plate.controlledDoors = doors;
            plate.controls = Array.ConvertAll(doors, door => door.observable.targetId);
            Label(parent, id + "Label", label, new Vector2(x, groundY - .54f), 1.8f, .28f, color);
            return plate;
        }

        private DemoExit MakeExit(Transform parent, string id, string label, NavigationNode node,
            Color color, DemoActor actor, CompanionWorld world)
        {
            Vector2 center = node.transform.position;
            var go = Rectangle(parent, id, center, new Vector2(.9f, 1.3f), WithAlpha(color, .12f));
            var trigger = go.AddComponent<BoxCollider2D>();
            trigger.isTrigger = true;
            var exit = go.AddComponent<DemoExit>();
            exit.targetId = id;
            exit.kind = "exit";
            exit.arrivalNode = node;
            exit.requiredActor = actor;
            exit.trigger = trigger;
            exit.world = world;

            Rectangle(parent, id + "Top", center + new Vector2(0f, .65f), new Vector2(.9f, .06f), color, order: 2);
            Rectangle(parent, id + "Left", center + new Vector2(-.42f, 0f), new Vector2(.06f, 1.3f), color, order: 2);
            Rectangle(parent, id + "Right", center + new Vector2(.42f, 0f), new Vector2(.06f, 1.3f), color, order: 2);
            Rectangle(parent, id + "Center", new Vector2(center.x, -.11f), new Vector2(.16f, .13f), color, order: 2);
            Label(parent, id + "Label", label, new Vector2(center.x, -.54f), 1.45f, .24f, color);
            return exit;
        }

        private void MakeHud(DemoLevel level)
        {
            var go = new GameObject("RelayHUD", typeof(RectTransform));
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = go.AddComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = UnityEngine.UI.CanvasScaler.ScreenMatchMode.Expand;

            HudText(go.transform, "Title", "TOGETHER / 02 - RIDGE RELAY", true, 48f, 70f, 44f, TextColor);
            HudText(go.transform, "Mode", "LOCAL PLAY", true, 55f, 38f, 23f, CompanionColor,
                TextAlignmentOptions.Right);
            HudText(go.transform, "Objective", "Hold A so your partner can reach B. Then cross together to your matching exits.",
                true, 117f, 42f, 24f, SecondaryTextColor);
            HudText(go.transform, "Controls", "<color=#FFB28B>PLAYER</color>  A / D to move   SPACE to jump"
                + "     <color=#6AD8F0>PARTNER</color>  J / L to move   I to jump     R to reset",
                false, 86f, 40f, 24f, TextColor);
            var status = HudText(go.transform, "Status", "Stand on A to open the first door.",
                false, 44f, 38f, 22f, SecondaryTextColor);
            var hud = go.AddComponent<RelayLevelHud>();
            hud.level = level;
            hud.label = status;
        }

        private TextMeshProUGUI HudText(Transform parent, string name, string value, bool top, float offset,
            float height, float fontSize, Color color, TextAlignmentOptions alignment = TextAlignmentOptions.Left)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            float y = top ? 1f : 0f;
            rect.anchorMin = new Vector2(0f, y);
            rect.anchorMax = new Vector2(1f, y);
            rect.pivot = new Vector2(.5f, y);
            rect.sizeDelta = new Vector2(-144f, height);
            rect.anchoredPosition = new Vector2(0f, top ? -offset : offset);
            var text = go.AddComponent<TextMeshProUGUI>();
            text.font = font;
            text.text = value;
            text.fontSize = fontSize;
            text.color = color;
            text.alignment = alignment;
            text.raycastTarget = false;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            return text;
        }

        private void Label(Transform parent, string name, string value, Vector2 position, float width, float size, Color color)
        {
            var go = Child(parent, name);
            go.transform.localPosition = new Vector3(position.x, position.y, -1f);
            var text = go.AddComponent<TextMeshPro>();
            text.font = font;
            text.text = value;
            text.fontSize = size * 10f;
            text.color = color;
            text.alignment = TextAlignmentOptions.Center;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.rectTransform.sizeDelta = new Vector2(width, .6f);
            text.GetComponent<MeshRenderer>().sortingOrder = 20;
        }

        private GameObject Rectangle(Transform parent, string name, Vector2 position, Vector2 size, Color color,
            bool solid = false, int order = 0)
        {
            var go = Child(parent, name);
            go.transform.localPosition = position;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = square;
            renderer.sharedMaterial = spriteMaterial;
            renderer.color = color;
            renderer.sortingOrder = order;
            if (solid) go.AddComponent<BoxCollider2D>();
            return go;
        }

        private static GameObject Child(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go;
        }

        private static T Load<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null) throw new InvalidOperationException("Required Ridge Relay asset is missing: " + path);
            return asset;
        }

        private static Color Hex(string value)
        {
            ColorUtility.TryParseHtmlString("#" + value, out var color);
            return color;
        }

        private static Color WithAlpha(Color color, float alpha)
        {
            color.a = alpha;
            return color;
        }
    }
}
#endif
