using System.Collections.Generic;
using UnityEngine;

namespace AutonomousCompanion.Demo
{
    /// <summary>Only the assigned living actor physically overlapping this exit activates it.</summary>
    [DefaultExecutionOrder(-50)]
    public sealed class DemoExit : CompanionTarget
    {
        public Collider2D trigger;
        public CompanionActorAdapter requiredActor;
        public CompanionWorld world;
        private readonly List<Collider2D> contacts = new List<Collider2D>(8);
        private readonly HashSet<CompanionActorAdapter> occupants = new HashSet<CompanionActorAdapter>();
        public bool Occupied { get; private set; }
        public override bool Active { get { return Occupied; } }
        private void Reset() { kind = "exit"; }
        private void Awake()
        {
            if (trigger == null) trigger = GetComponent<Collider2D>();
            if (trigger != null) trigger.isTrigger = true;
        }
        private void FixedUpdate() { RefreshOccupancy(); }
        public void RefreshOccupancy()
        {
            DemoOccupancy.Refresh(trigger, contacts, occupants);
            bool current = requiredActor != null && requiredActor.IsAlive && requiredActor.IsGrounded &&
                arrivalNode != null && requiredActor.CurrentNode == arrivalNode && occupants.Contains(requiredActor);
            if (Occupied == current) return;
            Occupied = current;
            if (world != null) world.NotifyChanged("exit_" + (current ? "occupied:" : "vacated:") + targetId);
        }
        public void ResetExit() { Occupied = false; occupants.Clear(); contacts.Clear(); }
        private void OnDisable() { ResetExit(); }
    }
}
