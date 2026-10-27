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
        [SerializeField] UnityEngine.UI.Button viewReconstructionButton;
        [SerializeField] TMPro.TMP_Text viewReconstructionLabel;
        const string ARScene = "Assets/Scenes/ARTestScene.unity";
        const string VisualizationScene = "Assets/Scenes/MeshPointCloudScene.unity";
        string destination = ARScene;
        TMPro.TMP_Text loadingLabel;
        bool loading;
        Vector2 previousSize;
        public bool IsLoading => loading;

        void OnEnable() { startARButton.onClick.AddListener(StartAR); if (viewReconstructionButton != null) viewReconstructionButton.onClick.AddListener(ViewReconstruction); }
        void OnDisable() { startARButton.onClick.RemoveListener(StartAR); if (viewReconstructionButton != null) viewReconstructionButton.onClick.RemoveListener(ViewReconstruction); }

        void LateUpdate()
        {
            var size = safeArea.rect.size;
            if (size == previousSize || size.x <= 0 || size.y <= 0) return;
            previousSize = size;
            // Center the combined logo/button group while preserving image proportions.
            const float buttonHeight = 88, gap = 32, margin = 32, exitHeight = 72, buttonGap = 16;
            float extraHeight = viewReconstructionButton != null ? buttonHeight + buttonGap : 0;
            float controlsHeight = buttonHeight + buttonGap + exitHeight + extraHeight;
            float width = Mathf.Min(800, Mathf.Max(1, size.x - margin * 2));
            float height = Mathf.Min(width * 1122f / 1402f, Mathf.Max(1, size.y - margin * 2 - controlsHeight - gap));
            width = height * 1402f / 1122f;
            logo.sizeDelta = new Vector2(width, height);
            logo.anchoredPosition = new Vector2(0, (controlsHeight + gap) / 2);
            var buttonRect = (RectTransform)startARButton.transform;
            buttonRect.sizeDelta = new Vector2(Mathf.Min(360, size.x - margin * 2), buttonHeight);
            float startY = (controlsHeight - height - gap - buttonHeight) / 2;
            buttonRect.anchoredPosition = new Vector2(0, startY);
            if (viewReconstructionButton != null)
            {
                var viewRect = (RectTransform)viewReconstructionButton.transform;
                viewRect.sizeDelta = new Vector2(Mathf.Min(520, size.x - margin * 2), buttonHeight);
                viewRect.anchoredPosition = new Vector2(0, startY - buttonHeight - buttonGap);
            }
            var exitRect = (RectTransform)exitButton.transform;
            exitRect.sizeDelta = new Vector2(buttonRect.sizeDelta.x, exitHeight);
            exitRect.anchoredPosition = new Vector2(0, startY - buttonHeight / 2 - buttonGap - exitHeight / 2 - extraHeight);
        }

        public void StartAR() { BeginLoad(ARScene, buttonLabel); }
        public void ViewReconstruction() { BeginLoad(VisualizationScene, viewReconstructionLabel); }

        void BeginLoad(string scene, TMPro.TMP_Text label)
        {
            if (loading || (navigation != null && navigation.IsBlockingNavigation)) return;
            if (!Application.CanStreamedLevelBeLoaded(scene))
            {
                Debug.LogError(scene + " is not available in the build scene list.", this);
                return;
            }
            destination = scene;
            loadingLabel = label;
            loading = true;
            startARButton.interactable = false;
            exitButton.interactable = false;
            if (viewReconstructionButton != null) viewReconstructionButton.interactable = false;
            if (loadingLabel != null) loadingLabel.text = "Loading...";
            StartCoroutine(LoadAR());
        }

        IEnumerator LoadAR()
        {
            // Give the disabled/loading state one frame to render before scene loading.
            yield return null;
            var operation = SceneManager.LoadSceneAsync(destination, LoadSceneMode.Single);
            if (operation == null)
            {
                loading = false;
                startARButton.interactable = true;
                exitButton.interactable = true;
                if (viewReconstructionButton != null) viewReconstructionButton.interactable = true;
                if (loadingLabel != null) loadingLabel.text = destination == ARScene ? "Start AR" : "View Mesh/Point Clouds";
                Debug.LogError("Unable to begin loading " + destination, this);
                yield break;
            }
            yield return operation;
        }
    }
}
