#if UNITY_EDITOR
using AutonomousCompanion.Demo;
using AutonomousCompanion.Demo.Testing;
using AutonomousCompanion.Vision;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AutonomousCompanion.Editor
{
    public static class VisionDemoMenu
    {
        private const string ConfigurePath = "Tools/Autonomous Companion/Configure Visual Companion";
        private const string TestPath = "Tools/Autonomous Companion/Run Offline Vision Test (Play Mode)";

        [MenuItem(ConfigurePath)]
        public static void Configure()
        {
            var scene = SceneManager.GetActiveScene();
            var level = Find<DemoLevel>(scene);
            var camera = Find<Camera>(scene);
            if (EditorApplication.isPlayingOrWillChangePlaymode || level == null || camera == null ||
                level.player == null || level.companion == null || level.world == null)
            {
                Debug.LogError("Open the saved visual demo copy in Edit Mode before configuring its companion.");
                return;
            }

            var go = level.gameObject;
            var adapter = go.GetComponent<DemoVisionActorAdapter>();
            if (adapter == null) adapter = Undo.AddComponent<DemoVisionActorAdapter>(go);
            var agent = go.GetComponent<VisionAgent>();
            bool addedAgent = agent == null;
            if (addedAgent) agent = Undo.AddComponent<VisionAgent>(go);
            var harness = go.GetComponent<VisionPlaytestHarness>();
            if (harness == null) harness = Undo.AddComponent<VisionPlaytestHarness>(go);
            Undo.RecordObjects(new Object[] { adapter, agent, harness }, "Configure Visual Companion");
            adapter.level = level;
            agent.actor = adapter;
            agent.gameCamera = camera;
            if (addedAgent) agent.enabled = false;
            harness.level = level;
            harness.agent = agent;
            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeGameObject = go;
            Debug.Log("Visual companion wired. Enable Vision Agent to use the configured loopback backend.");
        }

        [MenuItem(TestPath)]
        public static void RunOfflineTest()
        {
            var harness = Find<VisionPlaytestHarness>(SceneManager.GetActiveScene());
            if (!EditorApplication.isPlaying || harness == null)
            { Debug.LogError("Open the configured visual demo and press Play first."); return; }
            Application.runInBackground = true;
            harness.BeginOfflineTest();
        }

        [MenuItem(TestPath, true)]
        private static bool CanRunOfflineTest() => EditorApplication.isPlaying && !EditorApplication.isCompiling;

        private static T Find<T>(Scene scene) where T : Component
        {
            if (!scene.IsValid() || !scene.isLoaded) return null;
            foreach (var root in scene.GetRootGameObjects())
            {
                var component = root.GetComponentInChildren<T>(true);
                if (component != null) return component;
            }
            return null;
        }
    }
}
#endif
