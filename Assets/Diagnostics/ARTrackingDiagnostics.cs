using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace SwaKaar3DIY.Diagnostics
{
    // Scene-local development diagnostics; deliberately independent of production UI.
    public sealed class ARTrackingDiagnostics : MonoBehaviour
    {
        [SerializeField] ARSession session;
        [SerializeField] ARCameraManager cameraManager;
        [SerializeField] Camera arCamera;
        [SerializeField] TMP_FontAsset font;
        TextMeshProUGUI label;
        RectTransform safeArea;
        bool wasTracking;
        bool paused;
        float nextRefresh;
        long lastTimestamp;
        bool hasTimestamp;
        float lastFreshFrame;

        void Awake()
        {
            var canvasObject = new GameObject("DiagnosticCanvas", typeof(RectTransform), typeof(Canvas), typeof(UnityEngine.UI.CanvasScaler));
            canvasObject.transform.SetParent(transform, false);
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasObject.GetComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0.5f;
            safeArea = new GameObject("SafeArea", typeof(RectTransform)).GetComponent<RectTransform>();
            safeArea.SetParent(canvasObject.transform, false);
            var panel = new GameObject("TrackingPanel", typeof(RectTransform), typeof(UnityEngine.UI.Image));
            var rect = panel.GetComponent<RectTransform>();
            rect.SetParent(safeArea, false);
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 1);
            rect.anchoredPosition = new Vector2(0, -128);
            rect.sizeDelta = new Vector2(-24, 200);
            var image = panel.GetComponent<UnityEngine.UI.Image>();
            image.color = new Color(0, 0, 0, 0.58f);
            image.raycastTarget = false;
            label = new GameObject("TrackingText", typeof(RectTransform), typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>();
            label.rectTransform.SetParent(rect, false);
            label.rectTransform.anchorMin = Vector2.zero;
            label.rectTransform.anchorMax = Vector2.one;
            label.rectTransform.offsetMin = new Vector2(20, 12);
            label.rectTransform.offsetMax = new Vector2(-20, -12);
            label.font = font;
            label.fontSize = 30;
            label.enableAutoSizing = true;
            label.fontSizeMin = 22;
            label.fontSizeMax = 30;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.color = Color.white;
            label.raycastTarget = false;
            label.richText = false;
            label.text = "AR: Initializing";
            ResetFrame();
        }

        void OnApplicationPause(bool value) { paused = value; ResetFrame(); }
        void OnEnable() { ResetFrame(); }
        void ResetFrame() { hasTimestamp = false; lastFreshFrame = float.NegativeInfinity; }

        void LateUpdate()
        {
            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + 0.1f;
            var area = Screen.safeArea;
            safeArea.anchorMin = new Vector2(area.xMin / Screen.width, area.yMin / Screen.height);
            safeArea.anchorMax = new Vector2(area.xMax / Screen.width, area.yMax / Screen.height);
            safeArea.offsetMin = safeArea.offsetMax = Vector2.zero;

            var state = ARSession.state;
            var cameraState = TrackingState.None;
            bool frameAvailable = false;
            bool cameraRunning = cameraManager != null && cameraManager.isActiveAndEnabled &&
                cameraManager.subsystem != null && cameraManager.subsystem.running;
            if (!paused && cameraRunning && arCamera != null)
            {
                var parameters = new XRCameraParams
                {
                    zNear = arCamera.nearClipPlane, zFar = arCamera.farClipPlane,
                    screenWidth = Screen.width, screenHeight = Screen.height,
                    screenOrientation = Screen.orientation
                };
                if (cameraManager.subsystem.TryGetLatestFrame(parameters, out var frame))
                {
                    cameraState = frame.trackingState;
                    if (frame.TryGetTimestamp(out long timestamp))
                    {
                        if (!hasTimestamp || timestamp != lastTimestamp)
                        {
                            lastFreshFrame = Time.unscaledTime;
                            lastTimestamp = timestamp;
                            hasTimestamp = true;
                        }
                        frameAvailable = Time.unscaledTime - lastFreshFrame < 1f;
                    }
                    // Without a frame timestamp, do not claim confirmed live tracking.
                }
            }

            string status;
            if (paused || session == null || !session.isActiveAndEnabled || arCamera == null ||
                !arCamera.isActiveAndEnabled || cameraManager == null || !cameraManager.isActiveAndEnabled ||
                state == ARSessionState.Unsupported ||
                ARSession.notTrackingReason == NotTrackingReason.CameraUnavailable ||
                (state == ARSessionState.SessionTracking && !cameraManager.permissionGranted))
                status = "Unavailable";
            else if (state == ARSessionState.SessionTracking && cameraRunning && frameAvailable &&
                     cameraState == TrackingState.Tracking && ARSession.notTrackingReason == NotTrackingReason.None)
            {
                status = "Tracking";
                wasTracking = true;
            }
            else if (!wasTracking && state == ARSessionState.SessionInitializing &&
                     (ARSession.notTrackingReason == NotTrackingReason.Initializing || ARSession.notTrackingReason == NotTrackingReason.None))
                status = "Initializing";
            else if (wasTracking || cameraState == TrackingState.Limited ||
                     (ARSession.notTrackingReason != NotTrackingReason.None && ARSession.notTrackingReason != NotTrackingReason.Initializing) ||
                     state == ARSessionState.SessionTracking)
                status = "Limited";
            else
                status = "Initializing";

            string details = $"Session: {state} | Camera: {(frameAvailable ? cameraState.ToString() : "No fresh frame")}";
            if (ARSession.notTrackingReason != NotTrackingReason.None)
                details = $"Reason: {ARSession.notTrackingReason} | Camera: {(frameAvailable ? cameraState.ToString() : "No fresh frame")}";
            string pose = "Position (m): unavailable\nRotation (deg): unavailable";
            if (arCamera != null)
            {
                var p = arCamera.transform.position;
                var r = arCamera.transform.eulerAngles;
                pose = string.Format(CultureInfo.InvariantCulture,
                    "Position (m): X {0:F2}  Y {1:F2}  Z {2:F2}\nRotation (deg): X {3:F1}  Y {4:F1}  Z {5:F1}",
                    p.x, p.y, p.z, r.x, r.y, r.z);
            }
            label.text = $"AR: {status}{(status == "Tracking" ? "" : " (pose unconfirmed)")}\n{details}\n{pose}";
        }
    }
}
