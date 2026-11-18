using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
namespace AutonomousCompanion
{
    /// <summary>Calls your own backend, never DeepSeek directly. Do not put upstream API keys in assets.</summary>
    public sealed class DecisionHttpClient : MonoBehaviour
    {
        public string endpoint = "http://127.0.0.1:8765/decision";
        [Min(1)] public int timeoutSeconds = 15;
        [Min(0.1f)] public float initialBackoffSeconds = 1f;
        [Min(1f)] public float maxBackoffSeconds = 30f;
        public bool IsBusy { get; private set; }
        public string LastError { get; private set; } = "";
        public float NextAttemptTime { get; private set; }
        private int failures;
        private UnityWebRequest activeRequest;
        public bool TrySend(DecisionRequest payload, Action<DecisionResponse, string> callback)
        {
            if (!isActiveAndEnabled || IsBusy || Time.unscaledTime < NextAttemptTime) return false;
            if (payload == null || callback == null) return false;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            { LastError = "invalid_backend_url"; return false; }
            IsBusy = true; StartCoroutine(Send(payload, callback)); return true;
        }
        private IEnumerator Send(DecisionRequest payload, Action<DecisionResponse, string> callback)
        {
            DecisionResponse response = null; string error = null;
            UnityWebRequest request = null;
            try
            {
                request = new UnityWebRequest(endpoint, "POST"); activeRequest = request;
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload)));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json"); request.timeout = Math.Max(1, timeoutSeconds);
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success) error = "backend_request_failed:" + request.responseCode;
                else
                {
                    try { response = JsonUtility.FromJson<DecisionResponse>(request.downloadHandler.text); }
                    catch (Exception) { error = "invalid_backend_json"; }
                    if (response == null || response.request_id != payload.request_id || response.state_version != payload.state_version)
                    { response = null; error = "response_correlation_mismatch"; }
                    else if (response.source != "model" && response.source != "fallback")
                    { response = null; error = "invalid_response_source"; }
                    else if (!Contains(payload.allowed_actions, response.action, response.target_id))
                    { response = null; error = "response_not_in_requested_actions"; }
                }
            }
            finally
            {
                if (request != null) request.Dispose();
                activeRequest = null; IsBusy = false;
            }
            // A fallback means model service did not produce a usable decision: apply backoff too.
            if (error != null || (response != null && response.source == "fallback"))
            {
                failures = Math.Min(failures + 1, 16);
                NextAttemptTime = Time.unscaledTime + Mathf.Min(maxBackoffSeconds, initialBackoffSeconds * Mathf.Pow(2f, failures - 1));
                LastError = error ?? "backend_fallback";
            }
            else { failures = 0; NextAttemptTime = 0; LastError = ""; }
            callback(response, error);
        }
        private static bool Contains(ActionOption[] options, string action, string targetId)
        {
            if (options == null) return false;
            foreach (var item in options) if (item != null && item.action == action && item.target_id == targetId) return true;
            return false;
        }
        /// <summary>Cancel without delivering an obsolete callback. Use when resetting a session.</summary>
        public void CancelPending()
        {
            if (activeRequest != null) activeRequest.Abort();
            StopAllCoroutines();
            if (activeRequest != null) { activeRequest.Dispose(); activeRequest = null; }
            IsBusy = false;
        }
        public void ResetAfterWorldReset()
        { CancelPending(); failures = 0; NextAttemptTime = 0; LastError = ""; }
        private void OnDisable() { CancelPending(); }
    }
}
