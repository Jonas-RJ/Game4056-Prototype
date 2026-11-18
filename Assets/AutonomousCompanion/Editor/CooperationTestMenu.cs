#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using AutonomousCompanion.Demo.Testing;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AutonomousCompanion.Editor
{
    /// <summary>Local-only physics runner. Disables both brains and HTTP clients before entering
    /// PlayMode. SessionState survives assembly/domain reload. No scene is created or saved.</summary>
    [InitializeOnLoad]
    public static class CooperationTestMenu
    {
        private const string PendingKey = "AutonomousCompanion.LocalTest.Pending";
        private const string RestoreKey = "AutonomousCompanion.LocalTest.Restore";
        private const string BackgroundSavedKey = "AutonomousCompanion.LocalTest.BackgroundSaved";
        private const string BackgroundValueKey = "AutonomousCompanion.LocalTest.BackgroundValue";
        private const string MenuPath = "Tools/Autonomous Companion/Run Local Physics Test";

        [Serializable] private sealed class ComponentState
        {
            public string global_id;
            public bool enabled;
        }
        [Serializable] private sealed class SavedStates
        {
            public List<ComponentState> components = new List<ComponentState>();
        }

        static CooperationTestMenu()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        [MenuItem(MenuPath)]
        public static void RunLocalPhysicsTest()
        {
            if (!ValidateScene(out string error)) { Debug.LogError("Local physics test refused: " + error); return; }
            var scene = SceneManager.GetActiveScene();
            var harness = FindHarness(scene);
            if (harness == null) { Debug.LogError("Local physics test refused: no CooperationPlaytestHarness in the active demo scene."); return; }
            if (harness.IsRunning) { Debug.LogWarning("A cooperation test is already running."); return; }
            if (EditorApplication.isPlaying)
            {
                EnableBackgroundSimulation();
                DisableNetwork(scene);
                harness.BeginFixtureSuite();
                return;
            }
            // Do not save, replace, open, or clean a dirty user scene.
            if (scene.isDirty)
            { Debug.LogError("Local physics test refused: save the AutonomousCoopDemo scene first. No scene changes were made."); return; }
            var saved = new SavedStates();
            foreach (var component in NetworkComponents(scene))
                saved.components.Add(new ComponentState {
                    global_id = GlobalObjectId.GetGlobalObjectIdSlow(component).ToString(), enabled = component.enabled
                });
            SessionState.SetString(RestoreKey, JsonUtility.ToJson(saved));
            SessionState.SetBool(PendingKey, true);
            EnableBackgroundSimulation();
            DisableNetwork(scene); // Happens BEFORE play, including when domain reload is enabled.
            EditorApplication.isPlaying = true;
        }

        [MenuItem(MenuPath, true)]
        private static bool CanRun()
        { return !EditorApplication.isCompiling && !EditorApplication.isUpdating && (!EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPlaying); }

        private static bool ValidateScene(out string error)
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded || scene.name != "AutonomousCoopDemo")
            { error = "Open the existing AutonomousCoopDemo scene first."; return false; }
            if (SceneManager.sceneCount != 1)
            { error = "Run with only AutonomousCoopDemo loaded; other scenes will not be modified."; return false; }
            if (string.IsNullOrEmpty(scene.path))
            { error = "The demo scene must already be saved."; return false; }
            error = null; return true;
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(PendingKey, false))
            {
                if (!ValidateScene(out string error))
                {
                    Debug.LogError("Local physics test cancelled: " + error);
                    EditorApplication.isPlaying = false;
                    return;
                }
                // PlayMode/domain reload may recreate application settings from the project.
                // Keep this runtime override so physical simulation advances without Game focus.
                Application.runInBackground = true;
                DisableNetwork(SceneManager.GetActiveScene());
                // Wait until scene Start methods have registered world targets and reset physics.
                EditorApplication.delayCall += StartPendingFixture;
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                SessionState.SetBool(PendingKey, false);
                RestoreEditModeComponents();
                RestoreBackgroundSimulation();
            }
        }

        private static void StartPendingFixture()
        {
            if (!EditorApplication.isPlaying || !SessionState.GetBool(PendingKey, false)) return;
            SessionState.SetBool(PendingKey, false);
            if (!ValidateScene(out string error)) { Debug.LogError("Local physics test cancelled: " + error); return; }
            var scene = SceneManager.GetActiveScene();
            Application.runInBackground = true;
            DisableNetwork(scene);
            var harness = FindHarness(scene);
            if (harness == null) { Debug.LogError("Local physics test cancelled: harness missing after reload."); return; }
            harness.BeginFixtureSuite();
            Debug.Log("Local physics fixture started. All companion HTTP clients are disabled; no external model is used. Read the harness ResultJson or COOP_PLAYTEST console result.");
        }

        private static CooperationPlaytestHarness FindHarness(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                var harness = root.GetComponentInChildren<CooperationPlaytestHarness>(true);
                if (harness != null) return harness;
            }
            return null;
        }

        private static List<Behaviour> NetworkComponents(Scene scene)
        {
            var components = new List<Behaviour>();
            foreach (var root in scene.GetRootGameObjects())
            {
                components.AddRange(root.GetComponentsInChildren<AutonomousCompanionBrain>(true));
                components.AddRange(root.GetComponentsInChildren<DecisionHttpClient>(true));
            }
            return components;
        }

        private static void DisableNetwork(Scene scene)
        {
            foreach (var component in NetworkComponents(scene))
            {
                if (EditorApplication.isPlaying && component is DecisionHttpClient client) client.CancelPending();
                component.enabled = false;
            }
        }

        private static void RestoreEditModeComponents()
        {
            string json = SessionState.GetString(RestoreKey, "");
            SessionState.EraseString(RestoreKey);
            if (string.IsNullOrEmpty(json)) return;
            var saved = JsonUtility.FromJson<SavedStates>(json);
            if (saved == null) return;
            foreach (var state in saved.components)
            {
                if (!GlobalObjectId.TryParse(state.global_id, out var id)) continue;
                var component = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) as Behaviour;
                if (component != null && component.gameObject.scene.name == "AutonomousCoopDemo") component.enabled = state.enabled;
            }
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
            if (!SessionState.GetBool(BackgroundSavedKey, false)) return;
            Application.runInBackground = SessionState.GetBool(BackgroundValueKey, false);
            SessionState.EraseBool(BackgroundSavedKey);
            SessionState.EraseBool(BackgroundValueKey);
        }
    }
}
#endif
