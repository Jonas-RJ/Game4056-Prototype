using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace AutonomousCompanion.Demo
{
    public sealed class CoopLevelFlow : MonoBehaviour
    {
        public DemoLevel level;
        public GameObject completionPanel;
        public UnityEngine.UI.Button continueButton;
        public TMPro.TextMeshProUGUI message;
        public string destinationScene;
        public string completionText;
        public bool IsLoading { get; private set; }

        private void OnEnable()
        {
            if (level != null) level.CompletedChanged += Refresh;
            if (continueButton != null) continueButton.onClick.AddListener(Continue);
            Refresh();
        }
        private void OnDisable()
        {
            if (level != null) level.CompletedChanged -= Refresh;
            if (continueButton != null) continueButton.onClick.RemoveListener(Continue);
        }
        private void Update()
        {
            if (Keyboard.current != null && (Keyboard.current.enterKey.wasPressedThisFrame ||
                Keyboard.current.numpadEnterKey.wasPressedThisFrame)) Continue();
        }
        private void Refresh()
        {
            if (completionPanel != null) completionPanel.SetActive(level != null && level.Completed);
            if (message != null) message.text = completionText;
            if (continueButton != null) continueButton.interactable = !IsLoading;
        }
        public void Continue()
        {
            if (IsLoading || level == null || !level.Completed) return;
            if (!Application.CanStreamedLevelBeLoaded(destinationScene))
            {
                if (message != null) message.text = "Could not open the next level. Press R to retry this level.";
                return;
            }
            IsLoading = true;
            if (continueButton != null) continueButton.interactable = false;
            SceneManager.LoadSceneAsync(destinationScene, LoadSceneMode.Single);
        }
    }
}
