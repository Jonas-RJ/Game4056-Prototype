using UnityEngine;

namespace AutonomousCompanion.Demo
{
    /// <summary>Switch A can be left only after B has latched the door and the living human
    /// is past its safe boundary. A model response cannot override these physical facts.</summary>
    public sealed class DemoSafeReleaseCondition : LocalCondition
    {
        public DemoDoor latchedDoor;
        public CompanionActorAdapter player;
        public CompanionWorld world;
        public float safePlayerX = -1.4f;
        private bool previous;
        public override bool IsSatisfied
        { get { return latchedDoor != null && latchedDoor.Latched && player != null &&
                       player.IsAlive && player.Position.x >= safePlayerX; } }
        private void FixedUpdate()
        {
            bool current = IsSatisfied;
            if (current == previous) return;
            previous = current;
            if (world != null) world.NotifyChanged(current ? "switch_a_safe_to_release" : "switch_a_release_protected");
        }
        public void ResetCondition() { previous = IsSatisfied; }
    }
}
