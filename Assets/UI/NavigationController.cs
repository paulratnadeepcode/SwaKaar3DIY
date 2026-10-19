using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARFoundation;

namespace SwaKaar3DIY.UI
{
    public sealed class NavigationController : MonoBehaviour
    {
        [SerializeField] bool arScreen;
        [SerializeField] UnityEngine.UI.Button navigationButton;
        [SerializeField] ConfirmationDialog dialog;
        [SerializeField] LandingScreenController landing;
        [SerializeField] ARSession session;
        [SerializeField] ARCameraManager cameraManager;
        InputAction back;
        bool transitioning;
        int lastBackFrame = -1;
        public bool IsBlockingNavigation => transitioning || dialog.IsOpen;
        public bool IsTransitioning => transitioning;
        bool Busy => transitioning || (landing != null && landing.IsLoading);

        void Awake()
        {
            back = new InputAction("Back", InputActionType.Button, "<Keyboard>/escape");
            back.AddBinding("<Gamepad>/buttonEast");
            back.performed += _ => HandleBack();
        }
        void OnEnable() { navigationButton.onClick.AddListener(RequestNavigation); back.Enable(); }
        void OnDisable() { navigationButton.onClick.RemoveListener(RequestNavigation); back.Disable(); }
        void OnDestroy() { back.Dispose(); }

        public void HandleBack()
        {
            if (Busy || lastBackFrame == Time.frameCount) return;
            lastBackFrame = Time.frameCount;
            if (dialog.IsOpen) dialog.Cancel();
            else RequestNavigation();
        }

        public void RequestNavigation()
        {
            if (Busy || dialog.IsOpen) return;
            dialog.Show(arScreen ? "End AR session?" : "Exit SwaKaar3DIY?",
                arScreen ? "Return to Home" : "Exit", ConfirmNavigation);
        }

        void ConfirmNavigation()
        {
            if (Busy) return;
            if (arScreen && !Application.CanStreamedLevelBeLoaded("Assets/Scenes/LandingScene.unity"))
            {
                Debug.LogError("LandingScene is not available in the build scene list.", this);
                return;
            }
            transitioning = true;
            navigationButton.interactable = false;
            if (arScreen) StartCoroutine(ReturnHome());
            else
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                Application.Quit();
#else
                // Quit is intentionally suppressed in Editor tests.
                Debug.Log("Exit confirmed. Application.Quit runs in the Android player.", this);
#endif
            }
        }

        IEnumerator ReturnHome()
        {
            if (cameraManager != null) cameraManager.enabled = false;
            if (session != null) session.enabled = false;
            yield return null;
            yield return SceneManager.LoadSceneAsync("Assets/Scenes/LandingScene.unity", LoadSceneMode.Single);
        }
    }
}
