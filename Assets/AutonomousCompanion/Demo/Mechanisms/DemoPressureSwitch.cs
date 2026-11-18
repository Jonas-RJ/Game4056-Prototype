using System.Collections.Generic;
using UnityEngine;

namespace AutonomousCompanion.Demo
{
    /// <summary>A real overlap activates the plate for either actor. BeginHold only records
    /// a commitment once the companion actually stands on the plate; it cannot press remotely.</summary>
    [DefaultExecutionOrder(-50)]
    public sealed class DemoPressureSwitch : CompanionTarget
    {
        public Collider2D trigger;
        public CompanionWorld world;
        public DemoDoor[] controlledDoors = new DemoDoor[0];
        public bool latchOnPress;
        public SpriteRenderer visual;
        private readonly List<Collider2D> contacts = new List<Collider2D>(8);
        private readonly HashSet<CompanionActorAdapter> occupants = new HashSet<CompanionActorAdapter>();
        private CompanionActorAdapter committedActor;
        private bool occupied;
        public bool Occupied { get { return occupied; } }
        public bool Latched { get; private set; }
        public override bool Active { get { return occupied || Latched; } }
        public override bool SupportsHold { get { return !latchOnPress; } }
        private void Reset() { kind = "switch"; }
        private void Awake()
        {
            if (trigger == null) trigger = GetComponent<Collider2D>();
            if (visual == null) visual = GetComponent<SpriteRenderer>();
            if (trigger != null) trigger.isTrigger = true;
        }
        private void FixedUpdate() { RefreshOccupancy(); }

        public void RefreshOccupancy()
        {
            bool wasActive = Active;
            bool wasOccupied = occupied;
            bool wasLatched = Latched;
            DemoOccupancy.Refresh(trigger, contacts, occupants);
            occupied = occupants.Count != 0;
            if (occupied && latchOnPress) Latched = true;
            if (committedActor != null && !occupants.Contains(committedActor)) committedActor = null;
            if (wasActive != Active || wasLatched != Latched || wasOccupied != occupied)
            {
                ApplyDoors();
                if (world != null)
                    world.NotifyChanged("switch_" + (Latched ? "latched:" : occupied ? "pressed:" : "released:") + targetId);
            }
            if (visual != null) visual.color = Active ? new Color(0.25f, 0.9f, 0.45f) : new Color(0.9f, 0.7f, 0.2f);
        }

        private void ApplyDoors()
        {
            foreach (var door in controlledDoors)
                if (door != null) door.SetInput(this, Active, latchOnPress && Latched);
        }

        public override bool CanHold(CompanionActorAdapter actor)
        {
            RefreshOccupancy();
            return SupportsHold && actor != null && actor.IsAlive && actor.IsGrounded &&
                actor.CurrentNode == arrivalNode && occupants.Contains(actor);
        }
        public override bool BeginHold(CompanionActorAdapter actor)
        {
            if (!CanHold(actor) || (committedActor != null && committedActor != actor)) return false;
            actor.StopLocomotion(); committedActor = actor;
            return true;
        }
        public override bool IsHolding(CompanionActorAdapter actor)
        {
            return actor != null && committedActor == actor && actor.IsAlive && occupants.Contains(actor);
        }
        public override void MaintainHold(CompanionActorAdapter actor)
        {
            if (committedActor == actor && actor != null) actor.StopLocomotion();
        }
        public override void ReleaseHold(CompanionActorAdapter actor)
        {
            if (committedActor == actor && CanRelease) committedActor = null;
            // The physical plate remains pressed until the actor actually walks off it.
        }
        public void ResetSwitch()
        {
            occupants.Clear(); contacts.Clear(); committedActor = null;
            occupied = false; Latched = false;
            foreach (var door in controlledDoors) if (door != null) door.RemoveInput(this);
        }
        private void OnDisable()
        {
            occupants.Clear(); committedActor = null; occupied = false;
            foreach (var door in controlledDoors) if (door != null) door.RemoveInput(this);
        }
    }
}
