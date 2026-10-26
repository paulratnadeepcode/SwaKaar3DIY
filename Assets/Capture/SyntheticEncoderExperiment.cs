using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace SwaKaar3DIY.Capture
{
    // Development-only five-second synthetic GPU recording; ARCore retains sole camera ownership.
    public sealed class SyntheticEncoderExperiment : MonoBehaviour
    {
        static SyntheticEncoderExperiment active;
        static string result = "Record synthetic 5 seconds";
        [SerializeField] Shader patternShader;
        [SerializeField] Shader cameraCopyShader;
        static string cameraResult = "Start Recording";
        public static bool IsRecording => active != null;
        bool realCamera, cameraSubscribed;
        Texture cameraTexture;
        int cameraUnityFrame = -1, sourceWidth, sourceHeight, skippedCameraFrames;
        long candidateTimestamp = -1, observedTimestamp = -1, firstCameraTimestamp = -1, lastSubmittedTimestamp = -1;
        double lastCameraFrameRealtime;
        UnityEngine.UI.Button cameraButton;
        UnityEngine.UI.Text cameraLabel;
        readonly List<CameraTrackingRecord> cameraTracking = new List<CameraTrackingRecord>(240);
        [Serializable] public sealed class CameraTrackingRecord {
            public double realtime;
            public string phase, session, camera, reason;
            public bool tracking;
        }
        [Serializable] public sealed class CameraRecordingReport {
            public int schemaVersion=3, width=1920, height=1080, fps=30, sourceWidth, sourceHeight;
            public string recordingKind="ar-camera", utc, output, nativeReport, cancellationReason, screenOrientation;
            public bool nativePass, cancelled, finalized;
            public int requestedFrames, encodedFrames, skippedFrames;
            public double recordingWallClockSeconds, cameraTimelineSeconds;
            public long firstCameraTimestampNs;
            public string uvPolicy="sourceUV=(u,1-v), full source coverage, native identity sampling; no display transform";
            public string colorPolicy="External OES RGB -> non-sRGB RGBA8 UNorm -> hardware H264 RGB/YUV conversion";
            public string timingPolicy="AR camera frame callback timestamp matched to latest frame and same Unity render frame; exposure association and video orientation require device validation";
            public List<FrameRecord> frames;
            public List<CameraTrackingRecord> trackingSamples;
        }
        RenderTexture[] textures;
        Material pattern;
        string outputBase;
        double recordingStart = -1, productionEndRealtime = -1, nextTrackingSample;
        int nextSlot, requested, queueRetryAttempts;
        bool productionEnded;
        readonly List<FrameRecord> frames = new List<FrameRecord>(150);
        readonly List<TrackingRecord> tracking = new List<TrackingRecord>(240);
        [Serializable] public sealed class FrameRecord {
            public int frame, textureSlot, unityFrame;
            public long ptsNs, cameraTimestampNs;
            public double submittedRealtime;
        }
        [Serializable] public sealed class TrackingRecord {
            public double realtime;
            public string phase, session, camera, reason;
            public bool tracking;
            public Vector3 position;
            public Quaternion rotation;
        }
        [Serializable] public sealed class RecordingReport {
            public int schemaVersion=2, width=1920, height=1080, fps=30, targetFrames=150;
            public int requestedFrames, skippedPacing, skippedQueue, unscheduledSlots, queueRetryAttempts;
            public double recordingWallClockSeconds, submissionSpanSeconds, completionWallClockSeconds;
            public double nominalSubmittedMediaDurationSeconds;
            public double encodedMediaDurationSeconds = -1;
            public string durationPolicy="Wall clock includes scheduling/retries; nominal media duration is submitted frames / 30. Actual encoded duration is unverified (-1) until MP4 inspection.";
            public bool nativePass, cancelled;
            public string utc, nativeReport, output, cancellationReason, orientation, colorPolicy;
            public string decodedFrames="UNVERIFIED: encoded samples are not a decoder validation";
            public List<FrameRecord> frames;
            public List<TrackingRecord> trackingSamples;
        }
        string cancellationReason;
        ARSession session;
        ARCameraManager cameraManager;
        Camera arCamera;
        SwaKaar3DIY.UI.NavigationController navigation;
        UnityEngine.UI.Button button;
        UnityEngine.UI.Text label;
        bool owner, submitted, subscribed, paused, cancelled;
        float deadline;
        IntPtr job, callback;
#if UNITY_ANDROID && !UNITY_EDITOR
        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)] static extern int EW1_Abi();
        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr EW1_Callback();
        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr EW1_Create(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string requested,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string canonical);
        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)] static extern int EW1_Done(IntPtr p);
        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr EW1_Report(IntPtr p);
        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)] static extern int EW1_Passed(IntPtr p);
        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)] static extern void EW1_Cancel(IntPtr p);
        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)] static extern void EW1_Destroy(IntPtr p);
        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr EW2_Create(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string requested,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string canonical,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string partialPath, UIntPtr a, UIntPtr b, UIntPtr c);
        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr EW3_Create(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string requested,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string canonical,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string partialPath, UIntPtr a, UIntPtr b, UIntPtr c);
        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)] static extern int EW2_Ready(IntPtr p);
        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)] static extern int EW2_Reserve(IntPtr p,int frame,long pts);
        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)] static extern void EW2_End(IntPtr p);
#endif
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (!Application.isEditor && (Application.platform != RuntimePlatform.Android || !Debug.isDebugBuild)) return;
            SceneManager.sceneLoaded -= SceneLoaded;
            SceneManager.sceneLoaded += SceneLoaded;
            SceneLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single);
        }
        static void SceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.path != "Assets/Scenes/ARTestScene.unity") return;
            foreach (var root in scene.GetRootGameObjects())
                if (root.GetComponentInChildren<SyntheticEncoderExperiment>(true) != null) return;
            var go = new GameObject("EncoderWindowSetupDevelopment");
            SceneManager.MoveGameObjectToScene(go, scene);
            go.AddComponent<SyntheticEncoderExperiment>();
        }
        void Start()
        {
            if (owner) return;
            if (!Application.isEditor && (Application.platform!=RuntimePlatform.Android || !Debug.isDebugBuild)) { enabled=false; return; }
            foreach (var root in gameObject.scene.GetRootGameObjects())
            {
                if (session == null) session = root.GetComponentInChildren<ARSession>(true);
                if (cameraManager == null) cameraManager = root.GetComponentInChildren<ARCameraManager>(true);
                if (navigation == null) navigation = root.GetComponentInChildren<SwaKaar3DIY.UI.NavigationController>(true);
            }
            arCamera = cameraManager != null ? cameraManager.GetComponent<Camera>() : null;
            if (session == null || cameraManager == null || arCamera == null || navigation == null)
            {
                Debug.LogWarning("[EncoderWindow] Missing AR/navigation reference; checkpoint UI disabled.", this);
                return;
            }
            CreateButton();
        }
        void CreateButton()
        {
            var go = new GameObject("EncoderWindowCanvas", typeof(RectTransform), typeof(Canvas),
                typeof(UnityEngine.UI.CanvasScaler), typeof(UnityEngine.UI.GraphicRaycaster));
            go.transform.SetParent(transform, false);
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = -1;
            var scaler = go.GetComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0.5f;
            var safe = new GameObject("SafeArea", typeof(RectTransform), typeof(SwaKaar3DIY.UI.SafeAreaLayout));
            safe.transform.SetParent(go.transform, false);
            var control = new GameObject("EncoderWindowSetup", typeof(RectTransform),
                typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
            var rect = control.GetComponent<RectTransform>();
            rect.SetParent(safe.transform, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0);
            rect.anchoredPosition = new Vector2(0, 240);
            rect.sizeDelta = new Vector2(520, 88);
            var image = control.GetComponent<UnityEngine.UI.Image>();
            image.color = new Color(0.12f, 0.16f, 0.18f, 0.92f);
            button = control.GetComponent<UnityEngine.UI.Button>();
            button.targetGraphic = image;
            button.navigation = new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.None };
            button.onClick.AddListener(RequestSetup);
            label = new GameObject("Label", typeof(RectTransform), typeof(UnityEngine.UI.Text)).GetComponent<UnityEngine.UI.Text>();
            label.rectTransform.SetParent(rect, false);
            label.rectTransform.anchorMin = Vector2.zero;
            label.rectTransform.anchorMax = Vector2.one;
            label.rectTransform.offsetMin = new Vector2(12, 8);
            label.rectTransform.offsetMax = new Vector2(-12, -8);
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = 28;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = Color.white;
            label.raycastTarget = false;
            // Camera Start/Stop occupies its own centered safe-area row.
            var cameraControl = Instantiate(control, safe.transform);
            cameraControl.name = "CameraStartStop";
            var cameraRect = cameraControl.GetComponent<RectTransform>();
            cameraRect.anchoredPosition = new Vector2(0, 448);
            cameraRect.sizeDelta = new Vector2(520, 88);
            cameraButton = cameraControl.GetComponent<UnityEngine.UI.Button>();
            cameraButton.onClick.RemoveAllListeners();
            cameraButton.onClick.AddListener(ToggleCameraRecording);
            cameraLabel = cameraControl.GetComponentInChildren<UnityEngine.UI.Text>();
            cameraLabel.fontSize = 22;
            cameraLabel.text = "Start Recording";
        }
        void ToggleCameraRecording()
        {
            if (active != null)
            {
                if (active.realCamera && !active.productionEnded && navigation != null && !navigation.IsBlockingNavigation)
                    active.EndProduction();
                return;
            }
            StartExperiment(true);
        }
        bool Tracking()
        {
            try
            {
                if (session == null || !session.isActiveAndEnabled || cameraManager == null ||
                    !cameraManager.isActiveAndEnabled || arCamera == null || !arCamera.isActiveAndEnabled ||
                    !cameraManager.permissionGranted || cameraManager.subsystem == null || !cameraManager.subsystem.running ||
                    ARSession.state != ARSessionState.SessionTracking || ARSession.notTrackingReason != NotTrackingReason.None)
                    return false;
                var p = new XRCameraParams {
                    zNear = arCamera.nearClipPlane, zFar = arCamera.farClipPlane,
                    screenWidth = Screen.width, screenHeight = Screen.height, screenOrientation = Screen.orientation
                };
                return cameraManager.subsystem.TryGetLatestFrame(p, out var f) && f.trackingState == TrackingState.Tracking;
            }
            catch (Exception) { return false; }
        }
#if UNITY_ANDROID && !UNITY_EDITOR
        static string CanonicalName(string selected)
        {
            using (var list = new AndroidJavaObject("android.media.MediaCodecList", 0))
            {
                var infos = list.Call<AndroidJavaObject[]>("getCodecInfos");
                try
                {
                    for (int i = 0; i < Math.Min(infos.Length, 256); i++)
                    {
                        var info = infos[i];
                        if (info.Call<string>("getName") != selected) continue;
                        if (!info.Call<bool>("isEncoder") || !info.Call<bool>("isHardwareAccelerated") ||
                            info.Call<bool>("isSoftwareOnly"))
                            throw new InvalidOperationException("Selected codec hardware classification changed");
                        string canonical = info.Call<string>("getCanonicalName");
                        Debug.Log("[EncoderWindow] Android CodecInfo: name=" + selected + "; canonical=" + canonical +
                            "; hardwareAccelerated=true; softwareOnly=false; vendor=" + info.Call<bool>("isVendor") +
                            "; alias=" + info.Call<bool>("isAlias") + "; manufacturer declaration, not independent silicon verification.");
                        if (string.IsNullOrEmpty(canonical)) throw new InvalidOperationException("Missing canonical codec name");
                        return canonical;
                    }
                }
                finally { foreach (var info in infos) info?.Dispose(); }
            }
            throw new InvalidOperationException("Selected codec metadata no longer available");
        }
#endif
        public void RequestSetup() { StartExperiment(false); }
        void StartExperiment(bool cameraMode)
        {
            if (!Application.isPlaying || (!Application.isEditor && !Debug.isDebugBuild) ||
                active != null || paused || navigation == null || navigation.IsBlockingNavigation) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.OpenGLES3) { result = "Requires OpenGL ES3"; return; }
            if (!Tracking()) { if(cameraMode) cameraResult="Wait for camera Tracking"; else result="Wait for camera Tracking"; return; }
            IntPtr pendingJob = IntPtr.Zero;
            RenderTexture[] pendingTextures = null;
            Material pendingPattern = null;
            GameObject pendingOwner = null;
            SyntheticEncoderExperiment runner = null;
            try
            {
                var capabilities = SwaKaar3DIY.Diagnostics.AndroidEncoderCapabilities.Query();
                if (capabilities.androidApi < 29)
                    throw new InvalidOperationException("This hardware-verification checkpoint requires API29+ CodecInfo flags; project minimum is unchanged.");
                var eligible = capabilities.encoders.FindAll(e => e.hardwareClassification == "Hardware accelerated" &&
                    e.surfaceInput && e.advertised1080p30 && string.IsNullOrEmpty(e.error));
                var selected = eligible.Find(e => e.name == "c2.qti.avc.encoder") ??
                    eligible.Find(e => e.name == "OMX.qcom.video.encoder.avc") ??
                    (eligible.Count > 0 ? eligible[0] : null);
                if (selected == null) throw new InvalidOperationException("No advertised hardware AVC surface encoder supports 1080p30");
                string canonical = CanonicalName(selected.name);
                Debug.Log("[EncoderWindow] Starting " + (cameraMode ? "real camera" : "synthetic") + " recording POC; current device performance remains unverified.");
                if (EW1_Abi() != 1) throw new InvalidOperationException("Native ABI mismatch");
                var cb = EW1_Callback();
                if (cb == IntPtr.Zero) throw new InvalidOperationException("Native callback export missing");
                var copy = cameraMode ? cameraCopyShader : patternShader;
                if (copy == null || !copy.isSupported)
                    throw new InvalidOperationException("Serialized recording shader missing or unsupported");
                if (!SystemInfo.IsFormatSupported(GraphicsFormat.R8G8B8A8_UNorm, GraphicsFormatUsage.Render))
                    throw new InvalidOperationException("RGBA8 render target unavailable");
                string directory = Path.Combine(Application.persistentDataPath, "SyntheticEncoderCheckpoint");
                Directory.CreateDirectory(directory);
                using (var os = new AndroidJavaObject("android.os.StatFs", directory))
                    if (os.Call<long>("getAvailableBytes") < 128L*1024*1024)
                        throw new IOException("At least 128 MiB free storage required");
                string basename = Path.Combine(directory, (cameraMode ? "camera-" : "synthetic-") + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") +
                    "-" + Guid.NewGuid().ToString("N"));
                pendingTextures = new RenderTexture[3];
                var native = new UIntPtr[3];
                for (int i=0;i<3;i++) {
                    var desc = new RenderTextureDescriptor(1920,1080) {
                        graphicsFormat=GraphicsFormat.R8G8B8A8_UNorm, depthBufferBits=0,
                        msaaSamples=1, useMipMap=false, autoGenerateMips=false
                    };
                    var texture = pendingTextures[i] = new RenderTexture(desc) {
                        name="SyntheticEncoderSlot"+i, filterMode=FilterMode.Point,
                        wrapMode=TextureWrapMode.Clamp
                    };
                    if (!texture.Create() || texture.graphicsFormat != GraphicsFormat.R8G8B8A8_UNorm || texture.sRGB)
                        throw new InvalidOperationException("Exact non-sRGB RGBA8 texture allocation failed");
                    // Exactly one native-handle query per allocated texture.
                    long handle=texture.GetNativeTexturePtr().ToInt64();
                    if (handle<=0 || handle>uint.MaxValue) throw new InvalidOperationException("Invalid GLES texture name");
                    native[i]=new UIntPtr((ulong)handle);
                }
                pendingPattern = new Material(copy) { hideFlags=HideFlags.HideAndDontSave };
                pendingJob = cameraMode
                    ? EW3_Create(selected.name, canonical, basename+".mp4.partial", native[0],native[1],native[2])
                    : EW2_Create(selected.name, canonical, basename+".mp4.partial", native[0],native[1],native[2]);
                if (pendingJob == IntPtr.Zero) throw new InvalidOperationException("Native setup job allocation failed");
                var go = pendingOwner = new GameObject("EncoderWindowResourceOwner");
                runner = go.AddComponent<SyntheticEncoderExperiment>();
                runner.owner = true;
                runner.realCamera = cameraMode;
                runner.textures=pendingTextures; pendingTextures=null;
                runner.pattern=pendingPattern; pendingPattern=null;
                runner.outputBase=basename;
                runner.job = pendingJob; pendingJob = IntPtr.Zero; runner.callback = cb;
                runner.session = session; runner.cameraManager = cameraManager; runner.arCamera = arCamera;
                runner.navigation = navigation; runner.deadline = Time.realtimeSinceStartup + 30f;
                DontDestroyOnLoad(go);
                active = runner;
                RenderPipelineManager.beginCameraRendering += runner.Render;
                runner.subscribed = true;
                if (cameraMode) {
                    cameraManager.frameReceived += runner.OnCameraFrame;
                    runner.cameraSubscribed = true;
                    runner.lastCameraFrameRealtime = Time.realtimeSinceStartupAsDouble;
                    cameraResult = "Stop Recording";
                }
                runner.SampleTracking("before");
                if (!cameraMode) result = "Recording synthetic pattern...";
            }
            catch (Exception ex)
            {
                if (pendingJob != IntPtr.Zero) EW1_Destroy(pendingJob);
                ReleaseTextures(pendingTextures);
                if (pendingPattern != null) Destroy(pendingPattern);
                if (runner != null)
                {
                    if (runner.submitted) runner.Cancel();
                    else
                    {
                        if (runner.job != IntPtr.Zero) EW1_Destroy(runner.job);
                        runner.job = IntPtr.Zero;
                        runner.Release();
                    }
                }
                else if (pendingOwner != null) Destroy(pendingOwner);
                Debug.LogWarning("[EncoderWindow] " + ex, this);
                if(cameraMode) cameraResult="Camera setup unavailable"; else result="Encoder setup unavailable";
            }
#else
            result = "Android device required";
            Debug.Log("[EncoderWindow] Synthetic native recording is not executed in Editor.", this);
#endif
        }

        void OnCameraFrame(ARCameraFrameEventArgs args)
        {
            if (!realCamera || cancelled || productionEnded || !args.timestampNs.HasValue) return;
            long timestamp = args.timestampNs.Value;
            if (timestamp <= observedTimestamp) return;
            if (cameraTexture != null) ++skippedCameraFrames;
            cameraTexture = null;
            observedTimestamp = timestamp;
            lastCameraFrameRealtime = Time.realtimeSinceStartupAsDouble;
            if (args.textures == null || args.propertyNameIds == null) { ++skippedCameraFrames; return; }
            int main = Shader.PropertyToID("_MainTex");
            for (int i=0;i<Math.Min(args.textures.Count,args.propertyNameIds.Count);i++)
                if (args.propertyNameIds[i] == main) { cameraTexture = args.textures[i]; break; }
            if (cameraTexture == null) { ++skippedCameraFrames; return; }
            sourceWidth = cameraTexture.width; sourceHeight = cameraTexture.height;
            if (sourceWidth != 1920 || sourceHeight != 1080) { Cancel("Camera source is not 1920x1080; no upscaling"); cameraTexture=null; return; }
            candidateTimestamp = timestamp;
            cameraUnityFrame = Time.frameCount;
        }
        bool CameraFrameMatches()
        {
            if (cameraManager == null || cameraManager.subsystem == null || !cameraManager.subsystem.running || arCamera == null) return false;
            var p = new XRCameraParams { zNear=arCamera.nearClipPlane,zFar=arCamera.farClipPlane,
                screenWidth=Screen.width,screenHeight=Screen.height,screenOrientation=Screen.orientation };
            return cameraManager.subsystem.TryGetLatestFrame(p,out var frame) &&
                frame.TryGetTimestamp(out long timestamp) && timestamp == candidateTimestamp;
        }
        void RenderCamera(ScriptableRenderContext context)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            double now=Time.realtimeSinceStartupAsDouble;
            if (recordingStart<0) recordingStart=now;
            if (now-recordingStart>=10 || requested>=320) { EndProduction(); return; }
            if (cameraTexture==null) return;
            var source=cameraTexture;
            cameraTexture=null; // Never retain transient source texture for asynchronous native work.
            if (cameraUnityFrame!=Time.frameCount || candidateTimestamp<=lastSubmittedTimestamp || !CameraFrameMatches()) {
                ++skippedCameraFrames; return;
            }
            long origin=firstCameraTimestamp<0 ? candidateTimestamp : firstCameraTimestamp;
            long pts=candidateTimestamp-origin;
            if (pts>=10000000000L) { EndProduction(); return; }
            if (pts<0 || (requested>0 && pts/1000<=frames[frames.Count-1].ptsNs/1000)) { ++skippedCameraFrames; return; }
            int slot=EW2_Reserve(job,requested,pts);
            if (slot<0) { ++skippedCameraFrames; return; } // Drop incoming frame; never retry a transient texture.
            using(var commands=new CommandBuffer {name="Clean AR camera GPU encoder producer"}) {
                commands.Blit(source,textures[slot],pattern,0);
                commands.IssuePluginEventAndData(callback,slot+2,job);
                context.ExecuteCommandBuffer(commands);
            }
            firstCameraTimestamp=origin;
            lastSubmittedTimestamp=candidateTimestamp;
            frames.Add(new FrameRecord {frame=requested,textureSlot=slot,ptsNs=pts,cameraTimestampNs=candidateTimestamp,
                unityFrame=Time.frameCount,submittedRealtime=now});
            ++requested;
#endif
        }
        void Render(ScriptableRenderContext context, Camera camera)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!owner || cancelled || camera != arCamera || job==IntPtr.Zero) return;
            if (!realCamera && !Tracking()) { Cancel("Tracking lost at render submission"); return; }
            try {
                using (var commands = new CommandBuffer { name="Synthetic encoder GPU producer" }) {
                    if (!submitted) {
                        commands.IssuePluginEventAndData(callback,1,job);
                        submitted=true; // Retain resources conservatively after any attempted submission.
                        context.ExecuteCommandBuffer(commands);
                        return;
                    }
                    if (productionEnded || EW2_Ready(job)!=1) return;
                    if (realCamera) { RenderCamera(context); return; }
                    double now=Time.realtimeSinceStartupAsDouble;
                    if (recordingStart<0) recordingStart=now;
                    // Absolute deadlines never discard overdue IDs. At most one submission per callback.
                    if (nextSlot >= 150) { EndProduction(); return; }
                    double frameDeadline = recordingStart + nextSlot / 30.0;
                    if (now < frameDeadline) return;
                    int frameId = nextSlot;
                    long pts = (long)frameId * 1000000000L / 30;
                    int slot = EW2_Reserve(job,frameId,pts);
                    // No reservation means no accepted frame: keep this ID and retry on a later callback.
                    if (slot < 0) { ++queueRetryAttempts; return; }
                    // Reserve BEFORE scheduling any texture writes. No reuse until native consumer retires it.
                    commands.SetRenderTarget(textures[slot]);
                    commands.SetViewport(new Rect(0,0,1920,1080));
                    commands.SetGlobalFloat("_SyntheticEncoderFrame",frameId);
                    commands.DrawProcedural(Matrix4x4.identity,pattern,0,MeshTopology.Triangles,3);
                    commands.IssuePluginEventAndData(callback,slot+2,job);
                    context.ExecuteCommandBuffer(commands);
                    // Advance only after ExecuteCommandBuffer returns successfully. If it throws,
                    // submission is ambiguous: the catch cancels rather than retrying this reservation.
                    ++nextSlot;
                    ++requested;
                    frames.Add(new FrameRecord { frame=frameId,textureSlot=slot,ptsNs=pts,
                        unityFrame=Time.frameCount,submittedRealtime=now });
                    if (nextSlot == 150) EndProduction();
                }
            } catch(Exception ex) {
                // A reserved slot whose callback might have been queued is never freed speculatively.
                Debug.LogWarning("[EncoderWindow] GPU submission failed: "+ex,this);
                Cancel("GPU submission exception");
            }
#endif
        }
        void EndProduction()
        {
            if(productionEnded) return;
            productionEnded=true;
            productionEndRealtime=Time.realtimeSinceStartupAsDouble;
#if UNITY_ANDROID && !UNITY_EDITOR
            if(job!=IntPtr.Zero) EW2_End(job);
#endif
        }
        void SampleTracking(string phase)
        {
            if((realCamera ? cameraTracking.Count : tracking.Count)>=240) return;
            string cameraState="Unavailable";
            try {
                if(cameraManager!=null && cameraManager.subsystem!=null && arCamera!=null) {
                    var p=new XRCameraParams { zNear=arCamera.nearClipPlane,zFar=arCamera.farClipPlane,
                        screenWidth=Screen.width,screenHeight=Screen.height,screenOrientation=Screen.orientation };
                    if(cameraManager.subsystem.TryGetLatestFrame(p,out var frame)) cameraState=frame.trackingState.ToString();
                }
            } catch(Exception) { }
            if (realCamera) {
                cameraTracking.Add(new CameraTrackingRecord {realtime=Time.realtimeSinceStartupAsDouble,phase=phase,
                    session=ARSession.state.ToString(),camera=cameraState,reason=ARSession.notTrackingReason.ToString(),tracking=Tracking()});
                return; // Camera mode intentionally records no poses.
            }
            tracking.Add(new TrackingRecord {
                realtime=Time.realtimeSinceStartupAsDouble,phase=phase,session=ARSession.state.ToString(),
                camera=cameraState,reason=ARSession.notTrackingReason.ToString(),tracking=Tracking(),
                position=arCamera!=null?arCamera.transform.position:Vector3.zero,
                rotation=arCamera!=null?arCamera.transform.rotation:Quaternion.identity
            }); // Approximate samples, NOT synchronized to synthetic presentation timestamps.
        }
        void Update()
        {
            if (!owner)
            {
                if (button != null)
                {
                    button.interactable = active == null && !paused && navigation != null && !navigation.IsBlockingNavigation;
                    label.text = active != null ? (active.cancelled ? "Waiting for safe cleanup..." : "Recording/finalizing...") : result;
                }
                if (cameraButton != null) {
                    cameraButton.interactable = !paused && navigation != null && !navigation.IsBlockingNavigation &&
                        (active==null || (active.realCamera && !active.productionEnded && !active.cancelled));
                    cameraLabel.text = active==null ? cameraResult : active.realCamera && !active.productionEnded
                        ? "Stop Recording" : "Finalizing...";
                }
                return;
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            if (realCamera && !productionEnded && recordingStart>=0) {
                double now=Time.realtimeSinceStartupAsDouble;
                if (now-recordingStart>=10) EndProduction();
                else if (now-lastCameraFrameRealtime>2) Cancel("No fresh camera frames for two seconds");
            }
            if (Time.realtimeSinceStartupAsDouble>=nextTrackingSample) {
                SampleTracking(productionEnded?"finalizing":"during");
                nextTrackingSample=Time.realtimeSinceStartupAsDouble+0.1;
            }
            if (!cancelled && (paused || navigation==null || navigation.IsBlockingNavigation))
                Cancel("Pause, Home/Back dialog, or scene navigation");
            if (!realCamera && !cancelled && !Tracking()) Cancel("Session or camera tracking lost");
            if (!cancelled && Time.realtimeSinceStartup>deadline) Cancel("Managed 30-second deadline");
            if (!submitted)
            {
                if (cancelled) { EW1_Destroy(job); job = IntPtr.Zero; Release(); }
                return;
            }
            if (EW1_Done(job) == 0) return;
            bool pass = EW1_Passed(job) != 0 && !cancelled;
            string report = Marshal.PtrToStringAnsi(EW1_Report(job)) ?? "Missing native report";
            // Bounded chunks keep complete diagnostics below Android log-entry limits.
            for (int offset = 0, part = 0; offset < report.Length; offset += 900, part++)
                Debug.Log("[EncoderWindow part=" + part + "] " + report.Substring(offset, Math.Min(900, report.Length - offset)));
            SampleTracking("after");
            var sidecar=new RecordingReport {
                utc=DateTime.UtcNow.ToString("O"),requestedFrames=requested,skippedPacing=0,
                skippedQueue=0,queueRetryAttempts=queueRetryAttempts,unscheduledSlots=Math.Max(0,150-nextSlot),
                recordingWallClockSeconds=recordingStart<0 ? 0 : Math.Max(0,
                    (productionEndRealtime>=0 ? productionEndRealtime : Time.realtimeSinceStartupAsDouble)-recordingStart),
                submissionSpanSeconds=frames.Count<2 ? 0 : frames[frames.Count-1].submittedRealtime-frames[0].submittedRealtime,
                completionWallClockSeconds=recordingStart<0 ? 0 : Time.realtimeSinceStartupAsDouble-recordingStart,
                nominalSubmittedMediaDurationSeconds=requested/30.0,nativePass=pass,cancelled=cancelled,
                cancellationReason=cancellationReason,nativeReport=report,output=outputBase+".mp4",
                orientation="Fixed landscape 1920x1080, top-left red, top-right green, bottom-left blue, bottom-right yellow; native identity UV; no phone/display rotation",
                colorPolicy="Synthetic raw RGB -> RGBA8 UNorm -> identity GLES copy; hardware RGB/YUV conversion; compressed colors need device validation",
                frames=frames,trackingSamples=tracking
            };
            bool metadataSaved=false;
            bool cameraFinalized=pass && requested>0 && NativeCount(report,"encodedSamples")==requested &&
                NativeCount(report,"finalized")==1 && File.Exists(outputBase+".mp4") && new FileInfo(outputBase+".mp4").Length>0;
            var cameraReport = new CameraRecordingReport {
                utc=sidecar.utc,output=sidecar.output,nativeReport=report,nativePass=pass,cancelled=cancelled,
                finalized=cameraFinalized,cancellationReason=cancellationReason,requestedFrames=requested,
                encodedFrames=NativeCount(report,"encodedSamples"),skippedFrames=skippedCameraFrames,
                sourceWidth=sourceWidth,sourceHeight=sourceHeight,firstCameraTimestampNs=firstCameraTimestamp,
                recordingWallClockSeconds=sidecar.recordingWallClockSeconds,
                cameraTimelineSeconds=frames.Count==0 ? 0 : frames[frames.Count-1].ptsNs/1e9,
                screenOrientation=Screen.orientation.ToString(),frames=frames,trackingSamples=cameraTracking
            };
            try {
                using(var writer=new StreamWriter(new FileStream(outputBase+".json",FileMode.CreateNew,FileAccess.Write)))
                    writer.Write(realCamera ? JsonUtility.ToJson(cameraReport,true) : JsonUtility.ToJson(sidecar,true));
                metadataSaved=true;
            } catch(Exception ex) { Debug.LogWarning("[EncoderWindow] Metadata write failed: "+ex); }
            if(realCamera && cameraFinalized && metadataSaved) {
                try { ExistingSyntheticRecordingExport.OfferCameraRecording(outputBase+".mp4"); }
                catch(Exception ex) { Debug.LogWarning("[EncoderWindow] Camera export selection unavailable: "+ex.Message); }
            }
            Debug.Log("[EncoderWindow] END "+(pass?"MP4 FINALIZED":"FAILED/CANCELLED")+
                "; requested="+requested+" queueRetries="+queueRetryAttempts+
                " recordingWallSeconds="+sidecar.recordingWallClockSeconds.ToString("F6",System.Globalization.CultureInfo.InvariantCulture)+
                " nominalMediaSeconds="+sidecar.nominalSubmittedMediaDurationSeconds.ToString("F6",System.Globalization.CultureInfo.InvariantCulture)+
                " actualEncodedDuration=UNVERIFIED"+
                "; output="+outputBase+".mp4; decoded frame count and playback UNVERIFIED");
            EW1_Destroy(job); job = IntPtr.Zero;
            if(realCamera) cameraResult=cameraFinalized && metadataSaved ? "Camera MP4 saved" : "Camera recording failed";
            else result = pass ? (requested==150 ? "Synthetic MP4 saved" : "MP4 saved (skipped frames)") : "Synthetic recording failed";
            Release();
#endif
        }
        static int NativeCount(string report,string key)
        {
            var match=System.Text.RegularExpressions.Regex.Match(report,"(?:^|\\s)"+key+"=([0-9]+)(?:\\s|;|$)");
            return match.Success && int.TryParse(match.Groups[1].Value,out int value) ? value : -1;
        }
        void Cancel(string reason="Component disabled or app paused")
        {
            if (cancelled) return;
            cancelled = true;
            cancellationReason=reason;
            EndProduction();
#if UNITY_ANDROID && !UNITY_EDITOR
            if (job != IntPtr.Zero) EW1_Cancel(job);
#endif
            Debug.LogWarning("[EncoderWindow] Cancelled: "+reason+"; native resources retained until GPU/callback/worker completion. Partial files are not published.", this);
        }
        static void ReleaseTextures(RenderTexture[] owned)
        {
            if(owned==null) return;
            foreach(var texture in owned) if(texture!=null) { texture.Release(); Destroy(texture); }
        }
        void Release()
        {
            ReleaseTextures(textures); textures=null;
            if(pattern!=null) { Destroy(pattern); pattern=null; }
            if (subscribed) RenderPipelineManager.beginCameraRendering -= Render;
            subscribed = false;
            if(cameraSubscribed && cameraManager!=null) cameraManager.frameReceived -= OnCameraFrame;
            cameraSubscribed=false; cameraTexture=null;
            if (active == this) active = null;
            Destroy(gameObject);
        }
        void OnApplicationPause(bool value) { paused = value; if (owner && value) Cancel(); }
        void OnDisable()
        {
            if (owner) { if (job != IntPtr.Zero) Cancel(); }
            else if (active != null) active.Cancel();
        }
    }
}
