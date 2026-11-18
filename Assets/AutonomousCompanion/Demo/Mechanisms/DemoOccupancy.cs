using System.Collections.Generic;
using UnityEngine;

namespace AutonomousCompanion.Demo
{
    internal static class DemoOccupancy
    {
        internal static void Refresh(Collider2D area, List<Collider2D> contacts,
            HashSet<CompanionActorAdapter> actors)
        {
            contacts.Clear();
            actors.Clear();
            if (area == null || !area.enabled || !area.gameObject.activeInHierarchy) return;
            var filter = new ContactFilter2D { useTriggers = true, useLayerMask = false, useDepth = false };
            area.Overlap(filter, contacts);
            foreach (var contact in contacts)
            {
                if (contact == null) continue;
                var actor = contact.GetComponentInParent<CompanionActorAdapter>();
                if (actor != null && actor.isActiveAndEnabled && actor.IsAlive) actors.Add(actor);
            }
        }
    }
}
