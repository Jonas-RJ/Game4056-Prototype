using TMPro;
using UnityEngine;

namespace AutonomousCompanion.Demo
{
    public sealed class RelayLevelHud : MonoBehaviour
    {
        public DemoLevel level;
        public bool firstGapBeforeLatch = true;
        public TextMeshProUGUI label;

        private void Update()
        {
            if (level == null || label == null) return;
            string status = ReadStatus();
            if (label.text != status) label.text = status;
        }

        private string ReadStatus()
        {
            if (level.Completed) return "Both exits reached. ENTER to continue / R to replay.";
            if (!level.player.IsAlive || !level.companion.IsAlive) return "A partner fell. Press R to restart.";
            if (level.companionExit.Occupied) return "Companion is at the exit. Bring the player to the orange exit.";
            if (level.playerExit.Occupied) return "Player is at the exit. Bring the companion to the blue exit.";
            if (level.switchA.CanRelease) return firstGapBeforeLatch ? "Both gates are latched. Bring the companion across both gaps." : "Both gates are latched. Cross the gap to your matching exits.";
            if (level.switchB.Latched) return "Both gates are latched. Move the player onto the middle platform.";
            if (level.switchA.Occupied) return firstGapBeforeLatch ? "Keep the companion on A. Jump the player across the first gap to B." : "Keep the companion on A. Move the player through the gate to B.";
            return "Move the companion onto A to open the first gate.";
        }
    }
}
