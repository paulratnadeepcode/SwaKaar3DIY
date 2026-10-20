using System;
using System.IO;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace SwaKaar3DIY.Capture
{
    // Development-only, one successful image per scene visit. Owns no AR resources.
    [DisallowMultipleComponent]
    public sealed class ARCameraCleanImageCapture : MonoBehaviour
    {
        [SerializeField] ARSession session;
        [SerializeField] ARCameraManager cameraManager;
        [SerializeField] Camera arCamera;
        [SerializeField] Shader copyShader;
        [SerializeField] TMP_FontAsset font;
        [SerializeField] SwaKaar3DIY.UI.NavigationController navigation;
        const int Width = 1920, Height = 1080;
        const float TimeoutSeconds = 10f;
        const GraphicsFormat PixelFormat = GraphicsFormat.R8G8B8A8_UNorm;
        static readonly int MainTextureId = Shader.PropertyToID("_MainTex");
        UnityEngine.UI.Button button;
        TMP_Text label;
        GameObject canvasObject;
        bool subscribed, waiting, paused, saved;
        float deadline;
        long latestTimestamp, requestAfterTimestamp;
        int candidateFrame = -1;
        Texture candidate;
        Metadata candidateMetadata;
        Work work;

        [Serializable]
        public sealed class Metadata
        {
            public int schemaVersion = 2;
            public string utc, unityVersion, graphicsApi, projectColorSpace, sourceFormat;
            public int sourceWidth, sourceHeight, outputWidth = Width, outputHeight = Height;
            public string outputFormat = "PNG RGBA8; lossless encoding of the copied provider RGB values";
            public long cameraTimestampNs;
            public double frameCallbackRealtimeSeconds, copySubmissionRealtimeSeconds;
            public int unityFrame;
            public string sessionState, cameraTrackingState, notTrackingReason, screenOrientation;
            // Column-vector convention: sourceUV = M * (outputU, outputV, 0, 1).
            public Matrix4x4 appliedUvTransform = new Matrix4x4
            {
                m00 = 1, m11 = -1, m13 = 1, m22 = 1, m33 = 1
            };
            public Matrix4x4 reportedDisplayMatrix;
            public bool displayMatrixAvailable;
            public bool displayMatrixApplied = false;
            public string uvMappingDirection = "Output quad UV to source OES texture UV, column-vector [u,v,0,1]";
            public string uvPolicy = "sourceU=outputU; sourceV=1-outputV; one shader V reflection over full [0,1]x[0,1]; no screen aspect-fill crop, scaling, horizontal reflection or device rotation";
            public string orientationPolicy = "Texture-aligned output with one source V reflection, not display-oriented passthrough; display rotation/crop is not applied. Corrected orientation requires physical device verification";
            public string pngCoordinates = "Top-left origin; X increases rightward, Y increases downward";
            public bool cpuRowReversal = false;
            public string rowOrderPolicy = "GLES bottom-first readback is expected, not separately measured on this device. Bytes pass unchanged to Unity EncodeArrayToPNG; its bottom-first-to-PNG-top-first conversion was verified with an asymmetric 2x2 fixture in Editor. No CPU row reversal; the only explicit image reflection is sourceV=1-outputV in the shader";
            public bool orientationPhysicallyVerified = false;
            public string colorPolicy = "Sample external-OES provider RGB unchanged into R8G8B8A8_UNorm (sRGB=false); no shader gamma/linear conversion or post-processing. PNG has no embedded gamma/ICC profile; intended display-referred values are not colorimetrically calibrated";
            public string timingLimitation = "Camera timestamp associated with this frame callback and same-Unity-frame copy submission; exact GPU image/exposure association requires device validation";
            public bool genuine1080pDetailVerified = false;
            public bool gpuReadbackCompleted;
        }

        // This object, rather than a scene coroutine, keeps GPU resources alive through readback.
        sealed class Work
        {
            public RenderTexture target;
            public Material material;
            public volatile bool cancelled, completed;
            public bool saved;
            public string error, stem, json;
            public Metadata metadata;
            public void ReleaseGpu()
            {
                if (target != null) { target.Release(); Destroy(target); target = null; }
                if (material != null) { Destroy(material); material = null; }
            }
        }

        void OnEnable()
        {
            if (!Application.isPlaying || (!Application.isEditor && !Debug.isDebugBuild)) return;
            if (session == null || cameraManager == null || arCamera == null || copyShader == null || font == null || navigation == null)
            {
                Debug.LogWarning("[CleanImage] Required scene references are missing; capture disabled.", this);
                return;
            }
            if (canvasObject == null) CreateButton();
            canvasObject.SetActive(true);
            cameraManager.frameReceived += OnFrame;
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            subscribed = true;
            RefreshButton();
        }

        void CreateButton()
        {
            canvasObject = new GameObject("CleanImageDevelopmentCanvas", typeof(RectTransform), typeof(Canvas),
                typeof(UnityEngine.UI.CanvasScaler), typeof(UnityEngine.UI.GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = -1; // Existing navigation/modal canvases remain above this button.
            var scaler = canvasObject.GetComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0.5f;
            var area = new GameObject("SafeArea", typeof(RectTransform), typeof(SwaKaar3DIY.UI.SafeAreaLayout));
            area.transform.SetParent(canvasObject.transform, false);
            var control = new GameObject("CaptureFrameButton", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
            var rect = control.GetComponent<RectTransform>();
            rect.SetParent(area.transform, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0);
            rect.anchoredPosition = new Vector2(0, 32);
            rect.sizeDelta = new Vector2(360, 88);
            var image = control.GetComponent<UnityEngine.UI.Image>();
            image.color = new Color(0.12f, 0.16f, 0.18f, 0.92f);
            button = control.GetComponent<UnityEngine.UI.Button>();
            button.targetGraphic = image;
            button.navigation = new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.None };
            button.onClick.AddListener(RequestCapture);
            label = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>();
            label.rectTransform.SetParent(rect, false);
            label.rectTransform.anchorMin = Vector2.zero;
            label.rectTransform.anchorMax = Vector2.one;
            label.rectTransform.offsetMin = new Vector2(12, 8);
            label.rectTransform.offsetMax = new Vector2(-12, -8);
            label.font = font;
            label.fontSize = 30;
            label.enableAutoSizing = true;
            label.fontSizeMin = 22;
            label.fontSizeMax = 30;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white;
            label.raycastTarget = false;
        }

        public void RequestCapture()
        {
            if (!subscribed || paused || waiting || work != null || saved || navigation.IsBlockingNavigation) return;
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.OpenGLES3 || SystemInfo.graphicsUVStartsAtTop ||
                !SystemInfo.supportsAsyncGPUReadback || !SystemInfo.IsFormatSupported(PixelFormat, GraphicsFormatUsage.Render) ||
                !SystemInfo.IsFormatSupported(PixelFormat, GraphicsFormatUsage.ReadPixels) || !copyShader.isSupported)
            {
                Fail("Requires GLES3 external-OES shader support, bottom-first readback and async RGBA8 render/readback support. No blocking fallback used.");
                return;
            }
            waiting = true;
            requestAfterTimestamp = latestTimestamp;
            candidate = null;
            candidateFrame = -1;
            deadline = Time.realtimeSinceStartup + TimeoutSeconds;
            RefreshButton();
        }

        bool TryTrackingFrame(out XRCameraFrame frame)
        {
            frame = default;
            if (paused || session == null || !session.isActiveAndEnabled || arCamera == null || !arCamera.isActiveAndEnabled ||
                cameraManager == null || !cameraManager.isActiveAndEnabled || !cameraManager.permissionGranted ||
                cameraManager.subsystem == null || !cameraManager.subsystem.running || ARSession.state != ARSessionState.SessionTracking ||
                ARSession.notTrackingReason != NotTrackingReason.None) return false;
            var parameters = new XRCameraParams
            {
                zNear = arCamera.nearClipPlane, zFar = arCamera.farClipPlane,
                screenWidth = Screen.width, screenHeight = Screen.height, screenOrientation = Screen.orientation
            };
            return cameraManager.subsystem.TryGetLatestFrame(parameters, out frame) && frame.trackingState == TrackingState.Tracking;
        }

        void OnFrame(ARCameraFrameEventArgs args)
        {
            if (args.timestampNs.HasValue) latestTimestamp = args.timestampNs.Value;
            if (!waiting) return;
            candidate = null;
            try
            {
                if (navigation.IsBlockingNavigation || !args.timestampNs.HasValue || args.timestampNs.Value <= requestAfterTimestamp ||
                    !TryTrackingFrame(out var frame) || !frame.TryGetTimestamp(out long timestamp) || timestamp != args.timestampNs.Value) return;
                if (args.textures == null || args.propertyNameIds == null) return;
                for (int i = 0; i < Math.Min(args.textures.Count, args.propertyNameIds.Count); i++)
                    if (args.propertyNameIds[i] == MainTextureId) { candidate = args.textures[i]; break; }
                if (candidate == null) return;
                if (candidate.width != Width || candidate.height != Height)
                {
                    Fail($"Expected genuine source dimensions {Width}x{Height}; exposed source is {candidate.width}x{candidate.height}. No upscaling performed.");
                    return;
                }
                candidateFrame = Time.frameCount;
                candidateMetadata = new Metadata
                {
                    utc = DateTime.UtcNow.ToString("O"), unityVersion = Application.unityVersion,
                    graphicsApi = SystemInfo.graphicsDeviceType.ToString(), projectColorSpace = QualitySettings.activeColorSpace.ToString(),
                    sourceWidth = candidate.width, sourceHeight = candidate.height, sourceFormat = candidate.graphicsFormat.ToString(),
                    cameraTimestampNs = timestamp, frameCallbackRealtimeSeconds = Time.realtimeSinceStartupAsDouble, unityFrame = Time.frameCount,
                    screenOrientation = Screen.orientation.ToString(), sessionState = ARSession.state.ToString(),
                    cameraTrackingState = frame.trackingState.ToString(), notTrackingReason = ARSession.notTrackingReason.ToString(),
                    displayMatrixAvailable = args.displayMatrix.HasValue, reportedDisplayMatrix = args.displayMatrix ?? Matrix4x4.identity
                };
            }
            catch (Exception ex) { Fail("Frame selection failed safely: " + ex.Message); }
        }

        void OnBeginCamera(ScriptableRenderContext context, Camera camera)
        {
            if (!waiting || camera != arCamera || candidate == null || candidateFrame != Time.frameCount) return;
            Work job = null;
            bool submitted = false;
            try
            {
                if (navigation.IsBlockingNavigation || !TryTrackingFrame(out var frame) ||
                    !frame.TryGetTimestamp(out long timestamp) || timestamp != candidateMetadata.cameraTimestampNs) return;
                job = new Work { metadata = candidateMetadata };
                job.material = new Material(copyShader) { hideFlags = HideFlags.HideAndDontSave };
                var descriptor = new RenderTextureDescriptor(Width, Height)
                {
                    graphicsFormat = PixelFormat, depthBufferBits = 0, msaaSamples = 1,
                    useMipMap = false, autoGenerateMips = false
                };
                job.target = new RenderTexture(descriptor) { name = "CleanCameraOwnedFrame", hideFlags = HideFlags.HideAndDontSave };
                if (!job.target.Create() || job.target.graphicsFormat != PixelFormat || job.target.sRGB)
                    throw new InvalidOperationException("Cannot allocate exact non-sRGB RGBA8 target.");
                job.metadata.copySubmissionRealtimeSeconds = Time.realtimeSinceStartupAsDouble;
                job.stem = Path.Combine(Application.persistentDataPath, "CleanImageCheckpoint", "clean-camera-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N"));
                work = job;
                waiting = false;
                var callbackJob = job;
                using (var commands = new CommandBuffer { name = "Clean ARCore camera copy and readback" })
                {
                    commands.Blit(candidate, job.target, job.material, 0);
                    commands.RequestAsyncReadback(job.target, 0, PixelFormat, request => OnReadback(callbackJob, request));
                    context.ExecuteCommandBuffer(commands);
                    submitted = true;
                }
                candidate = null;
                RefreshButton();
            }
            catch (Exception ex)
            {
                // Before successful submission no GPU consumer owns these allocations.
                if (!submitted) { job?.ReleaseGpu(); work = null; }
                Fail("GPU copy submission failed safely: " + ex.Message);
            }
        }

        void OnReadback(Work job, AsyncGPUReadbackRequest request)
        {
            try
            {
                if (job.cancelled) { job.error = "Capture cancelled before readback completion."; job.completed = true; return; }
                if (request.hasError) throw new InvalidOperationException("Asynchronous GPU readback failed; no synchronous fallback attempted.");
                var pixels = request.GetData<byte>().ToArray(); // Own bytes before the callback data expires.
                if (pixels.Length != Width * Height * 4) throw new InvalidOperationException("Unexpected readback byte count.");
                job.metadata.gpuReadbackCompleted = true;
                job.json = JsonUtility.ToJson(job.metadata, true);
                // EncodeArrayToPNG is documented thread-safe. No Unity scene objects are used on the worker.
                Task.Run(() => WriteImage(job, pixels));
            }
            catch (Exception ex) { job.error = ex.Message; job.completed = true; }
            finally { job.ReleaseGpu(); }
        }

        static void WriteImage(Work job, byte[] pixels)
        {
            string png = job.stem + ".png", json = job.stem + ".json";
            try
            {
                if (job.cancelled) return;
                // Bottom-first input -> PNG top-first conversion was verified in Editor only.
                // The single image-orientation correction is in shader UVs; do not reverse CPU rows.
                var encoded = ImageConversion.EncodeArrayToPNG(pixels, PixelFormat, Width, Height, Width * 4);
                if (encoded == null || encoded.Length == 0) throw new IOException("PNG encoder returned no data.");
                if (job.cancelled) return;
                Directory.CreateDirectory(Path.GetDirectoryName(job.stem));
                File.WriteAllBytes(png + ".partial", encoded);
                File.WriteAllText(json + ".partial", job.json);
                if (job.cancelled) return;
                File.Move(png + ".partial", png);
                File.Move(json + ".partial", json); // Sidecar is committed last.
                job.saved = true;
            }
            catch (Exception ex) { job.error = ex.Message; }
            finally
            {
                // Only this request's uniquely named incomplete outputs are removed.
                foreach (var path in new[] { png + ".partial", json + ".partial" })
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                if (!job.saved)
                    foreach (var path in new[] { png, json })
                        try { if (File.Exists(path)) File.Delete(path); } catch { }
                if (job.cancelled && !job.saved) job.error = "Capture cancelled; incomplete outputs discarded.";
                job.completed = true;
            }
        }

        void Update()
        {
            if (!subscribed) return;
            if (navigation.IsBlockingNavigation) Cancel("Navigation opened");
            if ((waiting || work != null) && Time.realtimeSinceStartup >= deadline) Cancel("Capture deadline exceeded");
            if (work != null && work.completed)
            {
                saved = work.saved;
                if (saved) Debug.Log("[CleanImage] Saved " + work.stem + ".png and .json; image detail/orientation/color not physically verified.", this);
                else Debug.LogWarning("[CleanImage] " + work.error, this);
                work = null;
            }
            RefreshButton();
        }

        void RefreshButton()
        {
            if (button == null) return;
            button.interactable = subscribed && !paused && !waiting && work == null && !saved && navigation != null && !navigation.IsBlockingNavigation;
            label.text = saved ? "Frame saved" : waiting ? "Waiting for tracking..." :
                work != null ? (work.cancelled ? "Cancelling..." : "Capturing...") : "Capture Frame";
        }

        void Fail(string reason)
        {
            waiting = false;
            candidate = null;
            Debug.LogWarning("[CleanImage] " + reason, this);
            RefreshButton();
        }

        void Cancel(string reason)
        {
            if (waiting) { waiting = false; candidate = null; Debug.LogWarning("[CleanImage] " + reason, this); }
            if (work != null && !work.cancelled && !work.completed)
            {
                work.cancelled = true; // Readback callback retains/releases resources; do not free an in-flight target.
                Debug.LogWarning("[CleanImage] " + reason + "; pending work cancelled, awaiting safe cleanup.", this);
            }
            RefreshButton();
        }

        void OnApplicationPause(bool value) { paused = value; if (value) Cancel("Application paused"); }
        void OnDisable()
        {
            Cancel("Capture component disabled or scene unloaded");
            if (subscribed)
            {
                if (cameraManager != null) cameraManager.frameReceived -= OnFrame;
                RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            }
            subscribed = false;
            if (canvasObject != null) canvasObject.SetActive(false);
        }
    }
}
