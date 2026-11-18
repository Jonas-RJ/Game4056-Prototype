using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
namespace AutonomousCompanion.Demo
{
    public sealed class DemoStatusHud : MonoBehaviour
    {
        public DemoLevel level;
        public CompanionExecutor executor;
        public DecisionHttpClient client;
        public TextMeshProUGUI label;
        private void Update()
        {
            if (level == null || label == null || executor == null) return;
            if (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame) level.ResetLevel();
            if (level.Completed) { label.text = "TOGETHER. Both exits reached.   Press R to play again."; return; }
            if (!level.player.IsAlive || !level.companion.IsAlive) { label.text = "A partner fell. Press R to restart."; return; }
            bool local = level.brain != null && level.brain.decisionMode == AutonomousCompanionBrain.DecisionMode.LocalPlanner;
            string mode = level.brain != null && level.brain.enabled ? (local ? "LOCAL AI" : "AUTONOMOUS") : "AI PAUSED";
            string task = executor.CurrentTask.action + (string.IsNullOrEmpty(executor.CurrentTask.target_id) ? "" : " / " + executor.CurrentTask.target_id);
            string network = !local && client != null && !string.IsNullOrEmpty(client.LastError) ? "  |  " + client.LastError : "";
            label.text = mode + "  |  " + task + "  |  " + executor.CurrentTask.status + network;
        }
    }
}
