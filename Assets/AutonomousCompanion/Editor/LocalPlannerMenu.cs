#if UNITY_EDITOR
using AutonomousCompanion.Demo.Testing;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AutonomousCompanion.Editor
{
    public static class LocalPlannerMenu
    {
        private const string Path = "Tools/Autonomous Companion/Run Offline AI Test (Play Mode)";
        [MenuItem(Path)]
        public static void Run()
        {
            var scene = SceneManager.GetActiveScene();
            if (!EditorApplication.isPlaying || scene.name != "AutonomousCoopDemo")
            { Debug.LogError("Open AutonomousCoopDemo and press Play before running the offline AI test."); return; }
            foreach (var root in scene.GetRootGameObjects())
            {
                var harness = root.GetComponentInChildren<CooperationPlaytestHarness>();
                if (harness == null) continue;
                if (harness.IsRunning) { Debug.LogWarning("A cooperation test is already running."); return; }
                Application.runInBackground = true;
                harness.BeginLocalRun();
                return;
            }
            Debug.LogError("The active scene has no cooperation test harness.");
        }
        [MenuItem(Path, true)]
        private static bool Available() => EditorApplication.isPlaying && !EditorApplication.isCompiling;
    }
}
#endif
