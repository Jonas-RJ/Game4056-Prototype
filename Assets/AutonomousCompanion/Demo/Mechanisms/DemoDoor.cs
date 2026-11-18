using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace AutonomousCompanion.Demo
{
    /// <summary>Physical door and live route gate. Pressure inputs OR together; a latch
    /// persists until a level reset. This component never selects companion tasks.</summary>
    public sealed class DemoDoor : LocalCondition
    {
        public Collider2D blocker;
        public CompanionWorld world;
        public DemoDoorTarget observable;
        public SpriteRenderer visual;
        public SpriteRenderer gateCap;
        public TMP_Text stateLabel;
        public Color closedColor = new Color(0.85f, 0.35f, 0.2f, 1f);
        public Color openColor = new Color(0.3f, 0.85f, 0.55f, 0.22f);
        private readonly Dictionary<Component, bool> inputs = new Dictionary<Component, bool>();
        private bool manualOpen;
        private string doorLabel;
        public bool IsOpen { get; private set; }
        public bool Latched { get; private set; }
        public override bool IsSatisfied { get { return IsOpen; } }
        public event Action<bool> StateChanged;

        private void Awake()
        {
            if (blocker == null) blocker = GetComponent<Collider2D>();
            if (visual == null) visual = GetComponent<SpriteRenderer>();
            if (stateLabel != null) doorLabel = stateLabel.text;
            Apply(false, false);
        }

        public void SetInput(Component source, bool active, bool latch)
        {
            if (source == null) return;
            inputs[source] = active;
            bool newlyLatched = active && latch && !Latched;
            if (newlyLatched) Latched = true;
            Recalculate();
            if (newlyLatched && world != null) world.NotifyChanged("door_latched:" + Id);
        }

        public void RemoveInput(Component source)
        {
            if (source != null && inputs.Remove(source)) Recalculate();
        }

        /// <summary>Explicit debug/manual input; opening does not latch the door.</summary>
        public void SetOpen(bool open) { manualOpen = open; Recalculate(); }

        public void ResetDoor()
        {
            inputs.Clear(); manualOpen = false; Latched = false;
            Apply(false, true);
        }

        private string Id { get { return observable != null ? observable.targetId : name; } }
        private void Recalculate()
        {
            bool open = manualOpen || Latched;
            foreach (var pair in inputs)
                if (pair.Key != null && pair.Value) { open = true; break; }
            Apply(open, true);
        }
        private void Apply(bool open, bool notify)
        {
            bool changed = IsOpen != open;
            IsOpen = open;
            if (blocker != null) blocker.enabled = !open;
            if (stateLabel != null)
            {
                if (visual != null) visual.enabled = !open;
                if (gateCap != null) gateCap.enabled = !open;
                stateLabel.text = doorLabel + (Latched ? " LATCHED OPEN" : open ? " OPEN" : " CLOSED");
            }
            else if (visual != null) visual.color = open ? openColor : closedColor;
            if (!changed || !notify) return;
            StateChanged?.Invoke(open);
            if (world != null) world.NotifyChanged("door_" + (open ? "opened:" : "closed:") + Id);
        }
    }
}
