using AutonomousCompanion.Vision;
using UnityEngine;

namespace AutonomousCompanion.Demo
{
    /// <summary>Draws two public status markers above a gate for the camera-based hold action.</summary>
    public sealed class DemoVisualHoldSignals : MonoBehaviour
    {
        public VisionAgent agent;
        public DemoLevel level;
        public DemoDoor gate;
        public bool playerPassesToRight = true;

        private static readonly Color Idle = new Color32(73, 81, 106, 255);
        private static readonly Color Active = new Color32(250, 47, 215, 255);
        private SpriteRenderer latchMarker;
        private SpriteRenderer passageMarker;
        private float gateHalfWidth;

        private void Start()
        {
            if (agent == null) agent = GetComponent<VisionAgent>();
            if (level == null) level = GetComponent<DemoLevel>();
            if (gate == null && level != null) gate = level.doorA;
            if (agent == null || level == null || level.player == null || gate == null ||
                gate.visual == null || gate.visual.sprite == null) return;

            Bounds visual = gate.visual.bounds;
            gateHalfWidth = visual.extents.x;
            // Leave space for the existing gate label below both indicators.
            Vector3 center = new Vector3(visual.center.x, visual.max.y + .98f, visual.center.z);
            latchMarker = CreateMarker("Gate latched indicator", center + Vector3.left * .21f);
            passageMarker = CreateMarker("Player beyond gate indicator", center + Vector3.right * .21f);
            agent.gateLatchedMarker = latchMarker;
            agent.playerPassedGateMarker = passageMarker;
            Update();
        }

        private SpriteRenderer CreateMarker(string label, Vector3 position)
        {
            var marker = new GameObject(label).AddComponent<SpriteRenderer>();
            marker.transform.SetParent(transform, false);
            marker.transform.position = position;
            marker.sprite = gate.visual.sprite;
            marker.sharedMaterial = gate.visual.sharedMaterial;
            marker.sortingLayerID = gate.visual.sortingLayerID;
            marker.sortingOrder = gate.visual.sortingOrder + 10;
            marker.drawMode = SpriteDrawMode.Sliced;
            marker.size = Vector2.one * .31f;
            return marker;
        }

        private void Update()
        {
            if (latchMarker == null || passageMarker == null) return;
            latchMarker.color = gate.Latched ? Active : Idle;
            float direction = playerPassesToRight ? 1f : -1f;
            bool passed = level.player.IsAlive &&
                direction * (level.player.Position.x - gate.transform.position.x) > gateHalfWidth + .38f;
            passageMarker.color = passed ? Active : Idle;
        }

        private void OnDisable()
        {
            if (latchMarker != null) latchMarker.enabled = false;
            if (passageMarker != null) passageMarker.enabled = false;
        }

        private void OnEnable()
        {
            if (latchMarker != null) latchMarker.enabled = true;
            if (passageMarker != null) passageMarker.enabled = true;
        }

        private void OnDestroy()
        {
            if (latchMarker != null) Destroy(latchMarker.gameObject);
            if (passageMarker != null) Destroy(passageMarker.gameObject);
        }
    }
}
