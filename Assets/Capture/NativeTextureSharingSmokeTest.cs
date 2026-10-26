using System;
using System.Runtime.InteropServices;
using TMPro;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace SwaKaar3DIY.Capture
{
    // Development-only, synthetic texture test. Never acquires or configures a camera.
    [DisallowMultipleComponent]
    public sealed class NativeTextureSharingSmokeTest : MonoBehaviour
    {
        [SerializeField] ARSession session;
        [SerializeField] ARCameraManager cameraManager;
        [SerializeField] Camera arCamera;
        [SerializeField] TMP_FontAsset font;
        [SerializeField] SwaKaar3DIY.UI.NavigationController navigation;
        static NativeTextureSharingSmokeTest active;
        bool owner, submitted, cancelled, subscribed, paused, finished;
        int pattern, trackingSamples, trackingFailures;
        float deadline;
        IntPtr job, callback, textureHandle;
        RenderTexture target;
        Texture2D source;
        UnityEngine.UI.Button button;
        TMP_Text label;
        static string result = "Native texture test";
#if UNITY_ANDROID && !UNITY_EDITOR
        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)] static extern int S2B_Abi();
        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr S2B_Callback();
        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr S2B_Create(uint texture, int pattern);
        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)] static extern int S2B_Done(IntPtr job);
        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr S2B_Report(IntPtr job);
        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)] static extern int S2B_Passed(IntPtr job);
        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)] static extern void S2B_Cancel(IntPtr job);
        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)] static extern void S2B_Destroy(IntPtr job);
#endif
        void Start()
        {
            if (owner || !Application.isPlaying || (!Application.isEditor && !Debug.isDebugBuild)) return;
            if (session == null || cameraManager == null || arCamera == null || font == null || navigation == null)
            { Debug.LogWarning("[TextureSmoke] Missing scene references; disabled.", this); return; }
            CreateButton();
        }

        void CreateButton()
        {
            var go = new GameObject("NativeTextureSmokeDevelopmentCanvas", typeof(RectTransform),
                typeof(Canvas), typeof(UnityEngine.UI.CanvasScaler), typeof(UnityEngine.UI.GraphicRaycaster));
            go.transform.SetParent(transform, false);
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = -1;
            var scaler = go.GetComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0.5f;
            var area = new GameObject("SafeArea", typeof(RectTransform), typeof(SwaKaar3DIY.UI.SafeAreaLayout));
            area.transform.SetParent(go.transform, false);
            var control = new GameObject("NativeTextureTest", typeof(RectTransform),
                typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
            var rect = control.GetComponent<RectTransform>();
            rect.SetParent(area.transform, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0);
            rect.anchoredPosition = new Vector2(0, 136); // Above the existing Capture Frame button.
            rect.sizeDelta = new Vector2(440, 88);
            var image = control.GetComponent<UnityEngine.UI.Image>();
            image.color = new Color(0.12f, 0.16f, 0.18f, 0.92f);
            button = control.GetComponent<UnityEngine.UI.Button>();
            button.targetGraphic = image;
            button.navigation = new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.None };
            button.onClick.AddListener(RequestTest);
            label = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>();
            label.rectTransform.SetParent(rect, false);
            label.rectTransform.anchorMin = Vector2.zero;
            label.rectTransform.anchorMax = Vector2.one;
            label.rectTransform.offsetMin = new Vector2(12, 8);
            label.rectTransform.offsetMax = new Vector2(-12, -8);
            label.font = font;
            label.fontSize = 28;
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;
        }

        bool Tracking()
        {
            try {
            if (session == null || !session.isActiveAndEnabled || cameraManager == null ||
                !cameraManager.isActiveAndEnabled || arCamera == null || !arCamera.isActiveAndEnabled ||
                !cameraManager.permissionGranted || cameraManager.subsystem == null ||
                !cameraManager.subsystem.running || ARSession.state != ARSessionState.SessionTracking ||
                ARSession.notTrackingReason != NotTrackingReason.None) return false;
            var p = new XRCameraParams {
                zNear = arCamera.nearClipPlane, zFar = arCamera.farClipPlane,
                screenWidth = Screen.width, screenHeight = Screen.height, screenOrientation = Screen.orientation
            };
            return cameraManager.subsystem.TryGetLatestFrame(p, out var f) && f.trackingState == TrackingState.Tracking;
            } catch (Exception) { return false; }
        }

        public void RequestTest()
        {
            if (!Application.isPlaying || (!Application.isEditor && !Debug.isDebugBuild)) return;
            if (active != null || paused || navigation == null || navigation.IsBlockingNavigation) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.OpenGLES3 ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R8G8B8A8_UNorm, GraphicsFormatUsage.Render))
            { result = "Requires GLES3 RGBA8"; return; }
            if (!Tracking()) { result = "Wait for AR Tracking"; return; }
            try {
                if (S2B_Abi() != 1) throw new InvalidOperationException("Native ABI mismatch");
                var cb = S2B_Callback();
                if (cb == IntPtr.Zero) throw new InvalidOperationException("Missing native render callback");
                Debug.Log("[TextureSmoke] Native ABI=1; render callback export resolved; awaiting execution.", this);
                var go = new GameObject("NativeTextureSmokeResourceOwner");
                var runner = go.AddComponent<NativeTextureSharingSmokeTest>();
                runner.owner = true;
                runner.session = session; runner.cameraManager = cameraManager;
                runner.arCamera = arCamera; runner.navigation = navigation;
                runner.callback = cb; runner.deadline = Time.realtimeSinceStartup + 10f;
                DontDestroyOnLoad(go);
                active = runner;
                runner.InitializeResources();
                result = "Testing shared texture...";
            } catch (Exception ex) {
                result = "Native test unavailable";
                Debug.LogWarning("[TextureSmoke] " + ex, this);
                if (active != null) active.Cancel();
            }
#else
            result = "Android device required";
            Debug.Log("[TextureSmoke] Editor does not execute Android native code.", this);
#endif
        }

        void InitializeResources()
        {
            target = new RenderTexture(new RenderTextureDescriptor(64, 64) {
                graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm, depthBufferBits = 0, msaaSamples = 1,
                useMipMap = false, autoGenerateMips = false
            }) { name = "SyntheticShared64", hideFlags = HideFlags.HideAndDontSave };
            if (!target.Create() || target.sRGB) throw new InvalidOperationException("Cannot allocate non-sRGB RGBA8");
            textureHandle = target.GetNativeTexturePtr(); // Exactly once after allocation.
            if (textureHandle == IntPtr.Zero) throw new InvalidOperationException("Missing GL texture name");
            source = new Texture2D(2, 2, TextureFormat.RGBA32, false, true) {
                filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            SetPattern();
            RenderPipelineManager.beginCameraRendering += Render;
            subscribed = true;
        }

        void SetPattern()
        {
            // Unity SetPixels32 and native verification both use bottom-left first.
            Color32[] colors = {
                new Color32(255,0,0,255), new Color32(0,255,0,255),
                new Color32(0,0,255,255), new Color32(255,255,0,255)
            };
            if (pattern == 1) Array.Reverse(colors);
            source.SetPixels32(colors);
            source.Apply(false, false);
        }

        void Render(ScriptableRenderContext context, Camera camera)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!owner || submitted || cancelled || camera != arCamera) return;
            if (!Tracking()) { Cancel(); return; }
            try {
                job = S2B_Create(unchecked((uint)textureHandle.ToInt64()), pattern);
                if (job == IntPtr.Zero) throw new InvalidOperationException("Native job allocation failed");
                using (var commands = new CommandBuffer { name = "Synthetic texture update then native callback" }) {
                    commands.Blit(source, target);
                    commands.IssuePluginEventAndData(callback, 1, job);
                    context.ExecuteCommandBuffer(commands);
                    submitted = true;
                }
            } catch (Exception ex) {
                Debug.LogWarning("[TextureSmoke] Submission failed: " + ex, this);
                // A potentially submitted pointer must never be freed speculatively.
                if (job != IntPtr.Zero) submitted = true;
                Cancel();
            }
#endif
        }

        void Update()
        {
            if (!owner) {
                if (button != null) {
                    button.interactable = active == null && !paused && navigation != null && !navigation.IsBlockingNavigation;
                    label.text = active != null ? (active.cancelled ? "Cancelling native test..." : "Testing shared texture...") : result;
                }
                return;
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!cancelled) {
                trackingSamples++;
                if (!Tracking()) trackingFailures++;
                if (paused || navigation == null || navigation.IsBlockingNavigation ||
                    Time.realtimeSinceStartup > deadline || trackingFailures > 0) Cancel();
            }
            if (job != IntPtr.Zero && S2B_Done(job) != 0) {
                bool pass = S2B_Passed(job) != 0;
                Debug.Log("[TextureSmoke] " + Marshal.PtrToStringAnsi(S2B_Report(job)) +
                    "; tolerance=8/255; coordinates=GLES lower-left; callbackMs includes symbol lookup and worker dispatch; " +
                    "readback=worker only; trackingSamples=" + trackingSamples + "; trackingFailures=" + trackingFailures);
                S2B_Destroy(job); job = IntPtr.Zero; submitted = false;
                if (pass && !cancelled && pattern == 0) { pattern = 1; SetPattern(); return; }
                finished = true;
                result = pass && !cancelled && pattern == 1 ? "Texture test passed" : "Texture test failed/cancelled";
                Debug.Log("[TextureSmoke] Overall " + (pass && !cancelled && pattern == 1 ? "PASS" : "FAIL/CANCELLED") +
                    "; both patterns required; no recording performance established.");
            }
            if ((cancelled || finished) && job == IntPtr.Zero) Release();
#endif
        }

        void Cancel()
        {
            if (!cancelled)
                Debug.LogWarning("[TextureSmoke] Cancelling; pending native work retains its texture until completion. A driver/callback hang cannot be safely forced to release.", this);
            cancelled = true;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (job != IntPtr.Zero) S2B_Cancel(job);
#endif
            // If callback/GPU work never completes, retain resources rather than free in-flight memory.
        }
        void Release()
        {
            if (subscribed) RenderPipelineManager.beginCameraRendering -= Render;
            subscribed = false;
            if (target != null) { target.Release(); Destroy(target); }
            if (source != null) Destroy(source);
            if (cancelled) result = "Native test cancelled";
            if (active == this) active = null;
            Debug.Log("[TextureSmoke] Managed textures released after native completion.");
            Destroy(gameObject);
        }
        void OnApplicationPause(bool value) { paused = value; if (owner && value) Cancel(); }
        void OnDisable()
        {
            if (owner) Cancel();
            else if (active != null) active.Cancel();
        }
        void OnApplicationQuit() { if (owner) Cancel(); }
    }
}
