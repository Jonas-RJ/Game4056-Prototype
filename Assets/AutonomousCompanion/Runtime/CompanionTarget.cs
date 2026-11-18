using System;
using UnityEngine;
namespace AutonomousCompanion
{
    /// <summary>Place on each observable door, switch, exit or waypoint. Subclass for actual state
    /// and interaction. Switch adapters must maintain occupancy and report a protected commitment.</summary>
    public class CompanionTarget : MonoBehaviour
    {
        public string targetId;
        public string kind = "waypoint";
        public NavigationNode arrivalNode;
        public string[] controls = Array.Empty<string>();
        public LocalCondition safeReleaseCondition;
        public virtual bool Active { get { return false; } }
        public virtual bool SupportsHold { get { return false; } }
        public virtual bool IsHolding(CompanionActorAdapter actor) { return false; }
        /// <summary>Implement actual occupancy validation, including physical proximity.</summary>
        public virtual bool CanHold(CompanionActorAdapter actor)
        { return SupportsHold && actor != null && actor.CurrentNode == arrivalNode; }
        public virtual bool BeginHold(CompanionActorAdapter actor) { return false; }
        /// <summary>Must only adjust local stance; do not move off the switch.</summary>
        public virtual void MaintainHold(CompanionActorAdapter actor) { }
        public bool CanRelease { get { return safeReleaseCondition != null && safeReleaseCondition.IsSatisfied; } }
        public virtual void ReleaseHold(CompanionActorAdapter actor) { }
    }
}
