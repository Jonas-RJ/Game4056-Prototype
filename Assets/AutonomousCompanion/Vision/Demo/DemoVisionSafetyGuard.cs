using System;
using System.Collections.Generic;
using AutonomousCompanion.Vision;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AutonomousCompanion.Demo
{
    /// <summary>Predicts local support and jump clearance from the motor and current 2D colliders.</summary>
    internal sealed class DemoVisionSafetyGuard
    {
        private const float MinFloorNormal = .5f;
        // Keep a margin inside the box footprint while allowing the actor to overhang a ledge.
        private const float SupportWidthRatio = .7f;
        private const float WalkDropHeightRatio = .07f;
        private const int MaxWalkProbes = 96;
        private const int MaxTakeoffProbes = 8;
        private const float WalkLookaheadSeconds = 1.5f;
        private const float ApproachSupportWidthRatio = .2f;
        private const string EdgeReason = "Walking would lose ground support";
        private const string BlockedJumpReason = "Solid geometry blocks the jump path";
        private const string LandingReason = "Jump has no supported landing within reach";
        private const string ClearanceReason = "Jump landing lacks body clearance";
        private readonly BoxCollider2D box;
        private readonly Rigidbody2D body;
        private readonly ContactFilter2D solidFilter = new ContactFilter2D { useTriggers = false };
        private readonly List<RaycastHit2D> hits = new List<RaycastHit2D>(8);
        private readonly List<Collider2D> overlaps = new List<Collider2D>(8);

        public DemoActor Actor { get; }

        public DemoVisionSafetyGuard(DemoActor actor)
        {
            Actor = actor;
            body = actor.Body;
            box = actor.GetComponent<BoxCollider2D>();
        }

        private bool Ready => Actor.isActiveAndEnabled && box != null && box.isActiveAndEnabled && !box.isTrigger && body.simulated;
        private static float ContactMargin => Mathf.Max(2f * Physics2D.defaultContactOffset, .001f);

        public bool Check(int horizontal, bool jump, out string reason)
        {
            reason = "";
            if (!jump && horizontal == 0) return true;
            if (!Ready)
            {
                reason = "Companion collision shape unavailable";
                return false;
            }

            bool grounded = Actor.IsGrounded;
            if (jump)
            {
                reason = grounded ? JumpRejection(horizontal) : "Jump requires ground contact";
                return reason.Length == 0;
            }

            // Preserve directional input through the jump, including the first rising step when
            // the ground sensor can still see the takeoff surface.
            if (!grounded || body.linearVelocity.y > ContactMargin / Time.fixedDeltaTime) return true;
            if (HasWalkingSupport(horizontal)) return true;
            reason = EdgeReason;
            return false;
        }

        public string Observe()
        {
            if (!Ready || !Actor.IsGrounded) return "";
            return $"Walk left {WalkObservation(-1)}, right {WalkObservation(1)}. " +
                $"Jump left {JumpObservation(-1)}, in place {JumpObservation(0)}, right {JumpObservation(1)}.";
        }

        public VisionJumpOption[] ObserveJumpOptions(Camera camera, float leftX, float rightX)
        {
            float span = rightX - leftX;
            // The demo motor's horizontal plane must agree with the frame's action projection.
            if (!Ready || !Actor.IsGrounded || !camera.orthographic ||
                Vector3.Dot(camera.transform.right, Vector3.right) < .9999f || span <= .01f)
                return Array.Empty<VisionJumpOption>();

            var options = new List<VisionJumpOption>(4);
            Vector2 start = body.position;
            Bounds bounds = box.bounds;
            foreach (int horizontal in new[] { -1, 1 })
            {
                if (PredictJump(start, horizontal, true, out Vector2 landing, out var support).Length == 0 &&
                    TryMakeOption(start, landing, support, horizontal, true, camera, leftX, span, out var current))
                    options.Add(current);

                if (!TryFindApproach(start, bounds, horizontal, leftX, rightX, out float walkLimit, out float stride))
                    continue;
                // Back off from the limit and check both ends of the move_to arrival tolerance.
                // The live guard still checks the real pose after walking.
                float tolerance = VisionAgent.MoveArrivalTolerance;
                float inset = Mathf.Max(2f * tolerance, bounds.size.x * .3f);
                for (int i = 0; i < MaxTakeoffProbes; i++)
                {
                    float distance = horizontal * (walkLimit - start.x) - inset - i * 2f * stride;
                    if (distance <= 2f * tolerance) break;
                    Vector2 takeoff = start + Vector2.right * (horizontal * distance);
                    if (!PredictBeyondLimit(takeoff, horizontal, walkLimit, out landing, out support) ||
                        !PredictBeyondLimit(takeoff - Vector2.right * tolerance, horizontal, walkLimit, out _, out var leftSupport) ||
                        !PredictBeyondLimit(takeoff + Vector2.right * tolerance, horizontal, walkLimit, out _, out var rightSupport) ||
                        leftSupport != support || rightSupport != support)
                        continue;
                    if (TryMakeOption(takeoff, landing, support, horizontal, false, camera, leftX, span, out var approach))
                    {
                        options.Add(approach);
                        break;
                    }
                }
            }
            return options.ToArray();
        }

        private bool TryFindApproach(Vector2 start, Bounds bounds, int horizontal, float leftX, float rightX,
            out float walkLimit, out float stride)
        {
            // Probe a straight, level approach without moving the actor or reading any puzzle state.
            walkLimit = start.x;
            float speed = Mathf.Abs(StepVelocity(horizontal, 0f).x);
            stride = Mathf.Min(speed * Time.fixedDeltaTime, bounds.size.x * ApproachSupportWidthRatio);
            if (stride <= .001f) return false;
            Vector2 position = start;
            Vector2 step = Vector2.right * (horizontal * stride);
            int count = Mathf.Min(MaxWalkProbes, Mathf.FloorToInt(speed * WalkLookaheadSeconds / stride));
            for (int i = 0; i < count; i++)
            {
                Vector2 next = position + step;
                if (next.x < leftX || next.x > rightX) return false;
                if (FirstBlockingHit(position, step, i == 0, out _) ||
                    !HasFloorSupport(next, bounds, ContactMargin, ApproachSupportWidthRatio))
                    return true;
                position = next;
                walkLimit = next.x;
            }
            return false;
        }

        private bool PredictBeyondLimit(Vector2 takeoff, int horizontal, float walkLimit, out Vector2 landing, out Collider2D support) =>
            PredictJump(takeoff, horizontal, false, out landing, out support).Length == 0 &&
            horizontal * (landing.x - walkLimit) > VisionAgent.MoveArrivalTolerance;

        private static bool TryMakeOption(Vector2 takeoff, Vector2 landing, Collider2D support, int horizontal, bool fromCurrent,
            Camera camera, float leftX, float span, out VisionJumpOption option)
        {
            option = null;
            Vector3 viewport = camera.WorldToViewportPoint(landing);
            float takeoffX = (takeoff.x - leftX) / span;
            float landingX = (landing.x - leftX) / span;
            if (viewport.z <= 0f || viewport.x < 0f || viewport.x > 1f || viewport.y < 0f || viewport.y > 1f ||
                landingX < 0f || landingX > 1f || takeoffX < 0f || takeoffX > 1f)
                return false;
            option = new VisionJumpOption
            {
                horizontal = horizontal,
                from_current = fromCurrent,
                takeoff_screen_x = takeoffX,
                landing_screen_x = landingX,
                landing_screen_y = 1f - viewport.y,
                LandingWorld = landing,
                LandingSurface = support
            };
            return true;
        }

        private string WalkObservation(int horizontal) => HasWalkingSupport(horizontal) ? "supported" : "edge ahead";

        private string JumpObservation(int horizontal)
        {
            switch (JumpRejection(horizontal))
            {
                case "": return "clear";
                case BlockedJumpReason: return "blocked by solid";
                case ClearanceReason: return "no body clearance";
                default: return "no landing";
            }
        }

        private bool HasWalkingSupport(int horizontal)
        {
            Vector2 position = body.position;
            Bounds bounds = box.bounds;
            Vector2 displacement = Vector2.right * StepVelocity(horizontal, body.linearVelocity.y).x * Time.fixedDeltaTime;
            if (FirstBlockingHit(position, displacement, true, out var wall))
                displacement = displacement.normalized * wall.distance;

            // Allow contact tolerance and small steps down. A walk command may continue only
            // while the next position still has a floor close to the feet.
            float stepDown = Mathf.Max(bounds.size.y * WalkDropHeightRatio, ContactMargin);
            return HasFloorSupport(position + displacement, bounds, stepDown);
        }

        public bool RevalidateJumpOption(VisionJumpOption option, out string reason)
        {
            if (!Ready || !Actor.IsGrounded)
            {
                reason = "Jump requires a grounded actor with a collision shape";
                return false;
            }
            reason = PredictJump(body.position, option.horizontal, true, out var landing, out var support);
            if (reason.Length != 0) return false;
            if (option.LandingSurface != null && support == option.LandingSurface &&
                Mathf.Abs(landing.x - option.LandingWorld.x) <= .22f &&
                Mathf.Abs(landing.y - option.LandingWorld.y) <= .12f)
                return true;
            reason = "Jump would land outside the selected landing or on different support";
            return false;
        }

        public bool ValidateJumpOptionLanding(VisionJumpOption option, out string reason)
        {
            reason = "Selected landing support is no longer beneath the actor";
            if (!Ready || !Actor.IsGrounded || option.LandingSurface == null ||
                !option.LandingSurface.isActiveAndEnabled) return false;
            // The motor's grounded sensor can report landing slightly before body contact.
            // Probe only the foot region, and require the selected collider among the
            // nearest supporting hits so a lower old surface cannot mask a replacement.
            Bounds bounds = box.bounds;
            float margin = ContactMargin;
            Vector2 origin = new Vector2(bounds.center.x, bounds.min.y + 2f * margin);
            var scene = Actor.gameObject.scene.GetPhysicsScene2D();
            scene.BoxCast(origin, new Vector2(bounds.size.x * SupportWidthRatio, margin), 0f,
                Vector2.down, .12f + 2f * margin, solidFilter, hits);
            float nearest = float.PositiveInfinity, selected = float.PositiveInfinity;
            foreach (var hit in hits)
            {
                if (hit.distance <= 0f || hit.normal.y <= MinFloorNormal || !IsSolid(hit.collider)) continue;
                nearest = Mathf.Min(nearest, hit.distance);
                if (hit.collider == option.LandingSurface) selected = Mathf.Min(selected, hit.distance);
            }
            if (float.IsPositiveInfinity(selected) || selected > nearest + margin) return false;
            reason = "";
            return true;
        }

        private string JumpRejection(int horizontal) => PredictJump(body.position, horizontal, true, out _, out _);

        private string PredictJump(Vector2 position, int horizontal, bool atCurrentPose, out Vector2 landing, out Collider2D support)
        {
            landing = default;
            support = null;
            float step = Time.fixedDeltaTime;
            float verticalSpeed = Actor.jumpVelocity;
            if (verticalSpeed <= 0f) return LandingReason;

            Bounds bounds = box.bounds;
            for (float elapsed = 0f; elapsed < VisionAgent.MaxJumpSeconds; elapsed += step)
            {
                Vector2 velocity = StepVelocity(horizontal, verticalSpeed);
                Vector2 displacement = velocity * step;
                if (FirstBlockingHit(position, displacement, atCurrentPose && elapsed == 0f, out var hit))
                {
                    if (velocity.y >= 0f || hit.normal.y <= MinFloorNormal) return BlockedJumpReason;

                    landing = position + displacement.normalized * hit.distance;
                    if (landing.y <= Actor.deathY || !HasFloorSupport(landing, bounds, ContactMargin))
                        return LandingReason;
                    support = hit.collider;
                    return HasLandingClearance(landing) ? "" : ClearanceReason;
                }

                position += displacement;
                if (position.y <= Actor.deathY) break;
                verticalSpeed = velocity.y;
            }
            return LandingReason;
        }

        private Vector2 StepVelocity(int horizontal, float verticalSpeed)
        {
            // The motor overwrites x every fixed step and writes jumpVelocity only at launch.
            float step = Time.fixedDeltaTime;
            Vector2 velocity = new Vector2(horizontal * Actor.moveSpeed, verticalSpeed);
            velocity += Physics2D.gravity * (body.gravityScale * step);
            velocity /= 1f + body.linearDamping * step;
            return Vector2.ClampMagnitude(velocity, Physics2D.maxTranslationSpeed);
        }

        private bool FirstBlockingHit(Vector2 position, Vector2 displacement, bool atCurrentPose, out RaycastHit2D hit)
        {
            hit = default;
            float distance = displacement.magnitude;
            if (distance <= Mathf.Epsilon) return false;
            Vector2 direction = displacement / distance;
            box.Cast(position, body.rotation, direction, solidFilter, hits, distance);
            foreach (var candidate in hits)
            {
                if (!IsSolid(candidate.collider)) continue;
                if (atCurrentPose && candidate.distance <= Mathf.Epsilon)
                {
                    // A cast starting in contact reports the opposite of the cast direction as
                    // its normal. Use the actual contact geometry to let the actor move away.
                    var separation = box.Distance(candidate.collider);
                    if (separation.isValid && separation.distance >= -ContactMargin &&
                        Vector2.Dot(direction, separation.normal) >= -.001f) continue;
                }
                hit = candidate;
                return true;
            }
            return false;
        }

        private bool HasFloorSupport(Vector2 position, Bounds bounds, float maxDrop, float widthRatio = SupportWidthRatio)
        {
            float margin = ContactMargin;
            Vector2 offset = position - body.position;
            Vector2 origin = new Vector2(bounds.center.x + offset.x, bounds.min.y + offset.y + 2f * margin);
            Vector2 footprint = new Vector2(bounds.size.x * widthRatio, margin);
            PhysicsScene2D scene = Actor.gameObject.scene.GetPhysicsScene2D();
            scene.BoxCast(origin, footprint, 0f, Vector2.down, maxDrop + 2f * margin, solidFilter, hits);
            foreach (var hit in hits)
            {
                if (hit.distance > 0f && hit.normal.y > MinFloorNormal && IsSolid(hit.collider)) return true;
            }
            return false;
        }

        private bool HasLandingClearance(Vector2 position)
        {
            // Lift within contact tolerance so touching adjacent floor tiles does not count
            // as occupying the body. Check all colliders, including composite surfaces.
            box.Overlap(position + Vector2.up * ContactMargin, body.rotation, solidFilter, overlaps);
            foreach (var other in overlaps)
            {
                if (IsSolid(other)) return false;
            }
            return true;
        }

        private bool IsSolid(Collider2D other)
        {
            if (other == null || other == box || other.isTrigger) return false;
            var otherBody = other.attachedRigidbody;
            if (otherBody == body || (otherBody != null && otherBody.TryGetComponent<DemoActor>(out _))) return false;
            if (Physics2D.GetIgnoreCollision(box, other)) return false;

            int ownLayer = box.gameObject.layer;
            int otherLayer = other.gameObject.layer;
            bool layerContact = !Physics2D.GetIgnoreLayerCollision(ownLayer, otherLayer);
            bool? ownOverride = ContactOverride(box, body, otherLayer);
            bool? otherOverride = ContactOverride(other, otherBody, ownLayer);
            if (!ownOverride.HasValue) return otherOverride ?? layerContact;
            if (!otherOverride.HasValue || ownOverride == otherOverride) return ownOverride.Value;
            if (box.layerOverridePriority == other.layerOverridePriority) return false;
            return box.layerOverridePriority > other.layerOverridePriority ? ownOverride.Value : otherOverride.Value;
        }

        private static bool? ContactOverride(Collider2D collider, Rigidbody2D rigidbody, int otherLayer)
        {
            int mask = 1 << otherLayer;
            int excluded = collider.excludeLayers;
            int included = collider.includeLayers;
            if (rigidbody != null)
            {
                excluded |= rigidbody.excludeLayers;
                included |= rigidbody.includeLayers;
            }
            if ((excluded & mask) != 0) return false;
            if ((included & mask) != 0) return true;
            return null;
        }
    }
}
