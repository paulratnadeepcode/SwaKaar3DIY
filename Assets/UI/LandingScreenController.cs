using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SwaKaar3DIY.UI
{
    public sealed class LandingScreenController : MonoBehaviour
    {
        [SerializeField] UnityEngine.UI.Button startARButton;
        [SerializeField] RectTransform safeArea;
        [SerializeField] RectTransform logo;
        [SerializeField] TMPro.TMP_Text buttonLabel;
        [SerializeField] UnityEngine.UI.Button exitButton;
        [SerializeField] NavigationController navigation;
        const string ARScene = "Assets/Scenes/ARTestScene.unity";
        bool loading;
        Vector2 previousSize;
        public bool IsLoading => loading;

        void OnEnable() { startARButton.onClick.AddListener(StartAR); }
        void OnDisable() { startARButton.onClick.RemoveListener(StartAR); }

        void LateUpdate()
        {
            var size = safeArea.rect.size;
            if (size == previousSize || size.x <= 0 || size.y <= 0) return;
            previousSize = size;
            // Center the combined logo/button group while preserving image proportions.
            const float buttonHeight = 88, gap = 32, margin = 32, exitHeight = 72, buttonGap = 16;
            float controlsHeight = buttonHeight + buttonGap + exitHeight;
            float width = Mathf.Min(800, Mathf.Max(1, size.x - margin * 2));
            float height = Mathf.Min(width * 1122f / 1402f, Mathf.Max(1, size.y - margin * 2 - controlsHeight - gap));
            width = height * 1402f / 1122f;
            logo.sizeDelta = new Vector2(width, height);
            logo.anchoredPosition = new Vector2(0, (controlsHeight + gap) / 2);
            var buttonRect = (RectTransform)startARButton.transform;
            buttonRect.sizeDelta = new Vector2(Mathf.Min(360, size.x - margin * 2), buttonHeight);
            float startY = (controlsHeight - height - gap - buttonHeight) / 2;
            buttonRect.anchoredPosition = new Vector2(0, startY);
            var exitRect = (RectTransform)exitButton.transform;
            exitRect.sizeDelta = new Vector2(buttonRect.sizeDelta.x, exitHeight);
            exitRect.anchoredPosition = new Vector2(0, startY - buttonHeight / 2 - buttonGap - exitHeight / 2);
        }

        public void StartAR()
        {
            if (loading || (navigation != null && navigation.IsBlockingNavigation)) return;
            if (!Application.CanStreamedLevelBeLoaded(ARScene))
            {
                Debug.LogError("ARTestScene is not available in the build scene list.", this);
                return;
            }
            loading = true;
            startARButton.interactable = false;
            exitButton.interactable = false;
            buttonLabel.text = "Loading...";
            StartCoroutine(LoadAR());
        }

        IEnumerator LoadAR()
        {
            // Give the disabled/loading state one frame to render before scene loading.
            yield return null;
            var operation = SceneManager.LoadSceneAsync(ARScene, LoadSceneMode.Single);
            if (operation == null)
            {
                loading = false;
                startARButton.interactable = true;
                exitButton.interactable = true;
                buttonLabel.text = "Start AR";
                Debug.LogError("Unable to begin loading ARTestScene.", this);
                yield break;
            }
            yield return operation;
        }
    }
}
