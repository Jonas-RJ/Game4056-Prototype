using UnityEngine;
namespace AutonomousCompanion.Demo
{
    /// <summary>Keeps both switches and exits visible when the Game view or capture aspect changes.</summary>
    [ExecuteAlways]
    [RequireComponent(typeof(Camera))]
    public sealed class DemoCameraFit : MonoBehaviour
    {
        public float centerX = .75f;
        public float minimumHalfWidth = 11f;
        public float minimumVerticalHalf = 6f;
        private Camera sceneCamera;
        private void OnEnable() { Fit(); }
        private void LateUpdate() { Fit(); }
        private void OnPreCull() { Fit(); }
        private void OnValidate() { Fit(); }
        private void Fit()
        {
            if (sceneCamera == null) sceneCamera = GetComponent<Camera>();
            if (sceneCamera == null) return;
            float aspect = sceneCamera.targetTexture != null
                ? (float)sceneCamera.targetTexture.width / Mathf.Max(1, sceneCamera.targetTexture.height)
                : sceneCamera.aspect;
            sceneCamera.orthographic = true;
            sceneCamera.orthographicSize = Mathf.Max(minimumVerticalHalf, minimumHalfWidth / Mathf.Max(.1f, aspect));
            var position = transform.position; position.x = centerX; transform.position = position;
        }
    }
}
