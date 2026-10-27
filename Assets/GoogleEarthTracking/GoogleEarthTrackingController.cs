using TMPro;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace SwaKaar3DIY.GoogleEarthTracking
{
    // Scene-local image visualization. Never opens a camera or invokes capture/recording.
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ARTrackedImageManager))]
    public sealed class GoogleEarthTrackingController : MonoBehaviour
    {
        const string ReferenceName = "NASAEarth";
        [SerializeField] ARTrackedImageManager imageManager;
        [SerializeField] GameObject indicatorPrefab;
        [SerializeField] TMP_FontAsset diagnosticFont;
        ARTrackedImage selectedImage;
        GameObject indicator;
        GameObject statusCanvas;
        TextMeshProUGUI statusText;
        bool paused;
        float nextStatus;
        string previousStatus;

        void Awake()
        {
            if (imageManager == null) imageManager = GetComponent<ARTrackedImageManager>();
            if (imageManager == null || indicatorPrefab == null || diagnosticFont == null)
            {
                Debug.LogError("[GoogleEarthTracking] Missing manager, indicator or diagnostic font; visualization disabled.", this);
                enabled = false;
                return;
            }
            CreateStatus();
        }

        void LateUpdate()
        {
            ARTrackedImage candidate = null;
            if (imageManager != null && imageManager.isActiveAndEnabled)
            {
                foreach (var image in imageManager.trackables)
                {
                    if (image == null || image.referenceImage.name != ReferenceName) continue;
                    if (candidate == null) candidate = image;
                    // Keep one selected trackable; prefer a fully tracked candidate over a limited one.
                    if (image == selectedImage) candidate = image;
                    if (image.trackingState == TrackingState.Tracking) { candidate = image; break; }
                }
            }
            bool visible = !paused && ARSession.state == ARSessionState.SessionTracking &&
                candidate != null && candidate.trackingState == TrackingState.Tracking;
            UpdateIndicator(candidate, visible);
            if (Time.unscaledTime >= nextStatus)
            {
                nextStatus = Time.unscaledTime + 0.2f;
                RefreshStatus(candidate, visible);
            }
        }

        void UpdateIndicator(ARTrackedImage image, bool visible)
        {
            if (selectedImage != image)
            {
                ReleaseIndicator();
                selectedImage = image;
            }
            if (image == null) { ReleaseIndicator(); return; }
            if (visible && indicator == null)
            {
                indicator = Instantiate(indicatorPrefab, image.transform, false);
                indicator.name = "GoogleEarthOrientationIndicator";
                // ARTrackedImage local X/Z is its image plane, local +Y its normal.
                // Identity local rotation follows the image, not a world-aligned plane.
                indicator.transform.localPosition = Vector3.zero;
                indicator.transform.localRotation = Quaternion.identity;
                indicator.transform.localScale = Vector3.one;
            }
            if (indicator != null) indicator.SetActive(visible);
        }

        void ReleaseIndicator()
        {
            if (indicator != null)
            {
                indicator.SetActive(false); // Never display a stale instance while Destroy is deferred.
                Destroy(indicator);
                indicator = null;
            }
            selectedImage = null;
        }

        void OnApplicationPause(bool value)
        {
            paused = value;
            if (indicator != null) indicator.SetActive(false);
            nextStatus = 0;
        }

        void OnDisable()
        {
            ReleaseIndicator();
            if (statusCanvas != null) statusCanvas.SetActive(false);
        }

        void OnEnable()
        {
            if (statusCanvas != null) statusCanvas.SetActive(true);
            nextStatus = 0;
        }

        void OnDestroy()
        {
            ReleaseIndicator();
            if (statusCanvas != null) Destroy(statusCanvas);
        }

        void RefreshStatus(ARTrackedImage image, bool visible)
        {
            string state = image == null ? "None" : image.trackingState.ToString();
            string detected = image == null ? "not detected" : "detected";
            string message = "Image: " + detected + " | " + ReferenceName + " | " + state +
                (paused ? " | paused" : (image != null && !visible ? " | hidden" : ""));
            if (statusText != null) statusText.text = message;
            if (message == previousStatus) return;
            previousStatus = message;
            Debug.Log("[GoogleEarthTracking] " + message, this);
        }

        void CreateStatus()
        {
            statusCanvas = new GameObject("GoogleEarthTrackingStatus", typeof(RectTransform), typeof(Canvas),
                typeof(UnityEngine.UI.CanvasScaler));
            statusCanvas.transform.SetParent(transform, false);
            statusCanvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = statusCanvas.GetComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0.5f;
            var safe = new GameObject("SafeArea", typeof(RectTransform), typeof(SwaKaar3DIY.UI.SafeAreaLayout));
            safe.transform.SetParent(statusCanvas.transform, false);
            var panel = new GameObject("ImageTrackingPanel", typeof(RectTransform), typeof(UnityEngine.UI.Image));
            var rect = panel.GetComponent<RectTransform>();
            rect.SetParent(safe.transform, false);
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 1);
            rect.anchoredPosition = new Vector2(0, -340);
            rect.sizeDelta = new Vector2(-24, 64);
            var background = panel.GetComponent<UnityEngine.UI.Image>();
            background.color = new Color(0, 0, 0, 0.58f);
            background.raycastTarget = false;
            statusText = new GameObject("ImageTrackingText", typeof(RectTransform), typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>();
            statusText.rectTransform.SetParent(rect, false);
            statusText.rectTransform.anchorMin = Vector2.zero;
            statusText.rectTransform.anchorMax = Vector2.one;
            statusText.rectTransform.offsetMin = new Vector2(16, 6);
            statusText.rectTransform.offsetMax = new Vector2(-16, -6);
            statusText.font = diagnosticFont;
            statusText.fontSize = 26;
            statusText.enableAutoSizing = true;
            statusText.fontSizeMin = 18;
            statusText.fontSizeMax = 26;
            statusText.textWrappingMode = TextWrappingModes.NoWrap;
            statusText.color = Color.white;
            statusText.raycastTarget = false;
            statusText.richText = false;
            statusText.text = "Image: not detected | NASAEarth | None";
        }
    }
}