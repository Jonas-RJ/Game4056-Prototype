using UnityEngine;
using UnityEngine.InputSystem;

namespace AutonomousCompanion.Demo
{
    /// <summary>Physical motor for keyboard input, graph traversals and an exclusive visual controller.</summary>
    [DefaultExecutionOrder(-100)]
    [RequireComponent(typeof(Rigidbody2D), typeof(BoxCollider2D))]
    public sealed class DemoActor : CompanionActorAdapter
    {
        public bool allowKeyboard;
        public NavigationNode[] nodes;
        public float moveSpeed = 4f;
        public float jumpVelocity = 8.5f;
        public float deathY = -3.5f;
        public float arrivalTolerance = 0.16f;
        private Rigidbody2D body;
        private BoxCollider2D box;
        private bool alive = true;
        private float horizontal;
        private float testHorizontal;
        private bool testJumpHeld;
        private bool jumpQueued;
        private bool testOverride;
        private MonoBehaviour autonomousOwner;
        private float autonomousHorizontal;
        private NavigationLink traversal;
        private readonly RaycastHit2D[] groundHits = new RaycastHit2D[4];
        public bool HasAutonomousControl => autonomousOwner != null;
        public Rigidbody2D Body { get { if (body == null) body = GetComponent<Rigidbody2D>(); return body; } }
        public override Vector2 Position { get { return Body.position; } }
        public override bool IsAlive { get { return alive && Position.y > deathY; } }
        public override bool IsGrounded
        {
            get
            {
                var filter = new ContactFilter2D();
                filter.SetLayerMask(~(1 << 8)); filter.useTriggers = false;
                int count = Physics2D.BoxCast(Position + Vector2.down * 0.58f,
                    new Vector2(0.5f, 0.04f), 0f, Vector2.down, filter, groundHits, 0.09f);
                for (int i = 0; i < count; i++)
                    if (groundHits[i].collider != null && groundHits[i].normal.y > 0.5f) return true;
                return false;
            }
        }
        public override NavigationNode CurrentNode
        {
            get
            {
                if (!IsGrounded || !IsAlive || nodes == null) return null;
                NavigationNode result = null; float best = float.PositiveInfinity;
                foreach (var node in nodes)
                {
                    if (node == null || !node.isActiveAndEnabled) continue;
                    Vector2 delta = (Vector2)node.transform.position - Position;
                    if (Mathf.Abs(delta.y) > .2f || Mathf.Abs(delta.x) > arrivalTolerance) continue;
                    if (delta.sqrMagnitude < best) { result = node; best = delta.sqrMagnitude; }
                }
                return result;
            }
        }
        public override bool CanStartTraversal { get { return IsAlive && IsGrounded && traversal == null && autonomousOwner == null; } }
        private void Awake()
        {
            body = GetComponent<Rigidbody2D>(); box = GetComponent<BoxCollider2D>();
            body.gravityScale = 2.2f; body.constraints = RigidbodyConstraints2D.FreezeRotation;
            body.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            body.interpolation = RigidbodyInterpolation2D.Interpolate;
            body.sleepMode = RigidbodySleepMode2D.NeverSleep;
            gameObject.layer = 8;
        }
        private void Start()
        {
            foreach (var other in FindObjectsByType<DemoActor>(FindObjectsSortMode.None))
                if (other != this) Physics2D.IgnoreCollision(box, other.GetComponent<Collider2D>(), true);
        }
        private void Update()
        {
            if (allowKeyboard && autonomousOwner == null && !testOverride && Keyboard.current != null)
            {
                var keys = Keyboard.current;
                horizontal = (keys.dKey.isPressed || keys.rightArrowKey.isPressed ? 1f : 0f)
                    - (keys.aKey.isPressed || keys.leftArrowKey.isPressed ? 1f : 0f);
                if (keys.spaceKey.wasPressedThisFrame || keys.wKey.wasPressedThisFrame || keys.upArrowKey.wasPressedThisFrame)
                    jumpQueued = true;
            }
        }
        private void FixedUpdate()
        {
            if (!IsAlive) { alive = false; body.linearVelocity = new Vector2(0, body.linearVelocity.y); return; }
            if (autonomousOwner != null) horizontal = autonomousHorizontal;
            else if (testOverride && traversal == null) horizontal = testHorizontal;
            var velocity = body.linearVelocity;
            velocity.x = horizontal * moveSpeed;
            if (jumpQueued && IsGrounded) velocity.y = jumpVelocity;
            jumpQueued = false;
            body.linearVelocity = velocity;
        }
        public override bool CanTraverse(NavigationLink link)
        {
            // Validity must remain true in flight: grounded/start-location checks belong in BeginTraversal.
            return IsAlive && link != null && link.IsOpen &&
                (link.traversal == "walk" || link.traversal == "jump");
        }
        public override bool BeginTraversal(NavigationLink link)
        {
            if (!CanStartTraversal || CurrentNode == null || !CanTraverse(link)) return false;
            traversal = link;
            horizontal = Mathf.Sign(link.destination.transform.position.x - Position.x);
            if (link.traversal == "jump") jumpQueued = true;
            return true;
        }
        public override TraversalResult TickTraversal(float fixedDeltaTime)
        {
            if (traversal == null || !CanTraverse(traversal)) return TraversalResult.Failed;
            float delta = traversal.destination.transform.position.x - Position.x;
            horizontal = Mathf.Abs(delta) < .075f ? 0f : Mathf.Sign(delta);
            if (CurrentNode == traversal.destination)
            {
                horizontal = 0f; traversal = null;
                body.linearVelocity = new Vector2(0f, body.linearVelocity.y);
                return TraversalResult.Succeeded;
            }
            return TraversalResult.Running;
        }
        public override void StopLocomotion()
        {
            horizontal = 0f; autonomousHorizontal = 0; traversal = null; jumpQueued = false;
            if (Body != null) body.linearVelocity = new Vector2(0f, body.linearVelocity.y);
        }
        public void SetTestInput(float horizontal, bool jump)
        {
            if (autonomousOwner != null) return;
            testOverride = true; testHorizontal = Mathf.Clamp(horizontal, -1f, 1f);
            if (jump && !testJumpHeld) jumpQueued = true;
            testJumpHeld = jump;
        }
        public void ClearTestInput()
        { testOverride = false; testHorizontal = 0; testJumpHeld = false; horizontal = 0; jumpQueued = false; }
        public bool TryAcquireAutonomousControl(MonoBehaviour owner)
        {
            if (owner == null || allowKeyboard || !IsAlive || traversal != null ||
                (autonomousOwner != null && autonomousOwner != owner)) return false;
            if (autonomousOwner == owner) return true;
            ClearTestInput();
            StopLocomotion();
            autonomousOwner = owner;
            return true;
        }
        public bool ApplyAutonomousControl(MonoBehaviour owner, float movement, bool jump)
        {
            if (owner == null || owner != autonomousOwner || !IsAlive || float.IsNaN(movement) || float.IsInfinity(movement) || movement < -1 || movement > 1) return false;
            autonomousHorizontal = movement;
            if (jump && IsGrounded) jumpQueued = true;
            return true;
        }
        public void StopAutonomousControl(MonoBehaviour owner)
        {
            if (owner != null && owner == autonomousOwner) StopLocomotion();
        }
        public void ReleaseAutonomousControl(MonoBehaviour owner)
        {
            if (owner == null || owner != autonomousOwner) return;
            StopLocomotion();
            autonomousOwner = null;
        }
        public void ResetActor(Vector2 position)
        {
            alive = true; traversal = null; autonomousHorizontal = 0; ClearTestInput();
            Body.position = position; Body.linearVelocity = Vector2.zero; Body.angularVelocity = 0;
            Body.WakeUp(); Physics2D.SyncTransforms();
        }
        public void Kill() { alive = false; StopLocomotion(); }
    }
}
