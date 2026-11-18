using AutonomousCompanion.Demo.Testing;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AutonomousCompanion.Editor
{
    [InitializeOnLoad]
    public static class RelayLevelTestMenu
    {
        private const string MenuPath = "Tools/Autonomous Companion/Run Level 2 Physics Test";
        private const string ScenePath = "Assets/Scenes/CoopRelayLevel.unity";
        private const string PendingKey = "AutonomousCompanion.RelayTest.Pending";
        private const string BackgroundSavedKey = "AutonomousCompanion.RelayTest.BackgroundSaved";
        private const string BackgroundValueKey = "AutonomousCompanion.RelayTest.BackgroundValue";
        private static RelayLevelPlaytestHarness runningHarness;

        static RelayLevelTestMenu()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        [MenuItem(MenuPath)]
        public static void RunLevel2PhysicsTest()
        {
            var harness = FindHarness();
            if (harness == null) return;
            if (harness.IsRunning)
            {
                Debug.LogWarning("The Level 2 physics test is already running.", harness);
                return;
            }

            if (EditorApplication.isPlaying)
            {
                StartHarness(harness);
                return;
            }

            SessionState.SetBool(PendingKey, true);
            EnableBackgroundSimulation();
            EditorApplication.isPlaying = true;
        }

        [MenuItem(MenuPath, true)]
        private static bool CanRun() => !EditorApplication.isCompiling && !EditorApplication.isUpdating &&
            !EditorApplication.isPaused && (!EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPlaying);

        private static RelayLevelPlaytestHarness FindHarness()
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.path != ScenePath || SceneManager.sceneCount != 1)
            {
                Debug.LogError("Open CoopRelayLevel by itself before running the Level 2 physics test.");
                return null;
            }

            foreach (var root in scene.GetRootGameObjects())
            {
                var harness = root.GetComponentInChildren<RelayLevelPlaytestHarness>();
                if (harness != null) return harness;
            }
            Debug.LogError("CoopRelayLevel is missing its Level 2 playtest harness.");
            return null;
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(PendingKey, false))
            {
                Application.runInBackground = true;
                EditorApplication.delayCall += StartPendingHarness;
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                SessionState.EraseBool(PendingKey);
                RestoreBackgroundSimulation();
            }
        }

        private static void StartPendingHarness()
        {
            if (!EditorApplication.isPlaying || !SessionState.GetBool(PendingKey, false)) return;
            SessionState.EraseBool(PendingKey);
            var harness = FindHarness();
            if (harness != null) StartHarness(harness);
            else RestoreBackgroundSimulation();
        }

        private static void StartHarness(RelayLevelPlaytestHarness harness)
        {
            EnableBackgroundSimulation();
            runningHarness = harness;
            harness.BeginSuite();
            EditorApplication.update -= WatchHarness;
            EditorApplication.update += WatchHarness;
        }

        private static void WatchHarness()
        {
            if (runningHarness == null || !runningHarness.IsRunning) RestoreBackgroundSimulation();
        }

        private static void EnableBackgroundSimulation()
        {
            if (!SessionState.GetBool(BackgroundSavedKey, false))
            {
                SessionState.SetBool(BackgroundValueKey, Application.runInBackground);
                SessionState.SetBool(BackgroundSavedKey, true);
            }
            Application.runInBackground = true;
        }

        private static void RestoreBackgroundSimulation()
        {
            EditorApplication.update -= WatchHarness;
            runningHarness = null;
            if (!SessionState.GetBool(BackgroundSavedKey, false)) return;
            Application.runInBackground = SessionState.GetBool(BackgroundValueKey, false);
            SessionState.EraseBool(BackgroundSavedKey);
            SessionState.EraseBool(BackgroundValueKey);
        }
    }
}
