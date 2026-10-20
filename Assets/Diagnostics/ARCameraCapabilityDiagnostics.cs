using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace SwaKaar3DIY.Diagnostics
{
    // One bounded, scene-local snapshot. Never changes camera/session configuration.
    [DisallowMultipleComponent]
    public sealed class ARCameraCapabilityDiagnostics : MonoBehaviour
    {
        [SerializeField] ARSession session;
        [SerializeField] ARCameraManager cameraManager;
        [SerializeField] Camera arCamera;
        const float DeadlineSeconds = 15f;
        const float SampleSeconds = 5f;
        Report report;
        bool subscribed, finished, configurationsRead;
        float deadline, sampleEnd = float.PositiveInfinity, nextProbe;
        int probes;
        long previousTimestamp;
        double intervalSum;
        int intervals;

        [Serializable]
        public sealed class Configuration
        {
            public int cpuWidth, cpuHeight;
            public bool frameRateExposed;
            public int advertisedFrameRate;
        }

        [Serializable]
        public sealed class TextureInfo
        {
            public string format;
            public int shaderPropertyId;
            public int width, height;
        }

        [Serializable]
        public sealed class Report
        {
            public int schemaVersion = 1;
            public string utc, unityVersion, graphicsApi, graphicsDevice, platform, orientation;
            public string completionReason, sessionState, notTrackingReason;
            public bool sessionEnabled, cameraSubsystemRunning, cameraPermissionGranted;
            public string frameRateLimitation = "Unity configurations expose nullable single FPS values, not full min/max ranges. Observed cadence is not encoder throughput.";
            public List<Configuration> supportedConfigurations = new List<Configuration>();
            public Configuration currentConfiguration;
            public bool cpuImageAvailable, intrinsicsAvailable, timestampAvailable, poseAvailable, displayMatrixAvailable;
            public int cpuWidth, cpuHeight;
            public string cpuFormat;
            public double cpuTimestampSeconds;
            public Vector2 focalLength, principalPoint;
            public Vector2Int intrinsicsResolution;
            public List<TextureInfo> gpuTextures = new List<TextureInfo>();
            public Matrix4x4 displayMatrix;
            public long firstFrameTimestampNs, lastFrameTimestampNs;
            public int frameCallbacks, distinctTimestamps, nonIncreasingTimestamps;
            public double observedFrameRate, minimumIntervalMs, maximumIntervalMs;
            public Vector3 cameraWorldPosition;
            public Quaternion cameraWorldRotation;
            public double poseSampleRealtimeSeconds;
            public long poseCallbackTimestampNs;
            public string cameraTrackingState;
            public string poseLimitation = "Unity world-space transform sampled during frame callback, not guaranteed exposure-synchronized. Intrinsics refer to their reported image resolution; GPU calibration/crop is not verified.";
            public AndroidEncoderCapabilities.Report androidEncoders;
            public string gpuSurfaceAssessment;
            public bool recordingPerformancePhysicallyVerified = false;
            public List<string> warnings = new List<string>();
        }

        void OnEnable()
        {
            if (finished) return;
            report = new Report
            {
                utc = DateTime.UtcNow.ToString("O"), unityVersion = Application.unityVersion,
                graphicsApi = SystemInfo.graphicsDeviceType.ToString(), graphicsDevice = SystemInfo.graphicsDeviceName,
                platform = Application.platform.ToString(), orientation = Screen.orientation.ToString()
            };
            deadline = Time.realtimeSinceStartup + DeadlineSeconds;
            nextProbe = Time.realtimeSinceStartup;
            try
            {
                report.androidEncoders = AndroidEncoderCapabilities.Query();
                if (session == null || cameraManager == null || arCamera == null)
                {
                    Warn("Required scene references are missing.");
                    Finish("Missing references");
                    return;
                }
                cameraManager.frameReceived += OnFrame;
                subscribed = true;
            }
            catch (Exception ex) { Warn(ex.Message); Finish("Initialization failed safely"); }
        }

        void Update()
        {
            if (finished || report == null) return;
            float now = Time.realtimeSinceStartup;
            if (now >= deadline || now >= sampleEnd) { Finish("Bounded sample completed"); return; }
            if (now < nextProbe || probes >= 24) return;
            nextProbe = now + 0.5f;
            probes++;
            try
            {
                if (cameraManager == null || cameraManager.subsystem == null || !cameraManager.subsystem.running) return;
                if (!configurationsRead)
                {
                    using (var configs = cameraManager.GetConfigurations(Allocator.Temp))
                    {
                        foreach (var config in configs) report.supportedConfigurations.Add(Describe(config));
                        configurationsRead = configs.Length > 0;
                    }
                }
                var current = cameraManager.currentConfiguration;
                if (current.HasValue) report.currentConfiguration = Describe(current.Value);
                if (!report.intrinsicsAvailable && cameraManager.TryGetIntrinsics(out var intrinsics))
                {
                    report.intrinsicsAvailable = true;
                    report.focalLength = intrinsics.focalLength;
                    report.principalPoint = intrinsics.principalPoint;
                    report.intrinsicsResolution = intrinsics.resolution;
                }
                if (!report.cpuImageAvailable && cameraManager.TryAcquireLatestCpuImage(out var image))
                {
                    using (image)
                    {
                        report.cpuWidth = image.width;
                        report.cpuHeight = image.height;
                        report.cpuTimestampSeconds = image.timestamp;
                        report.cpuFormat = image.format.ToString();
                        report.cpuImageAvailable = true;
                    }
                }
            }
            catch (Exception ex) { Warn("Camera probe: " + ex.Message); }
        }

        void OnFrame(ARCameraFrameEventArgs args)
        {
            if (finished || report == null) return;
            try
            {
                if (report.frameCallbacks++ == 0) sampleEnd = Time.realtimeSinceStartup + SampleSeconds;
                if (report.gpuTextures.Count == 0 && args.textures != null)
                {
                    for (int i = 0; i < args.textures.Count; i++)
                    {
                        var texture = args.textures[i];
                        if (texture == null) continue;
                        report.gpuTextures.Add(new TextureInfo
                        {
                            width = texture.width, height = texture.height, format = texture.graphicsFormat.ToString(),
                            shaderPropertyId = args.propertyNameIds != null && i < args.propertyNameIds.Count ?
                                args.propertyNameIds[i] : 0
                        });
                    }
                }
                if (args.displayMatrix.HasValue)
                {
                    report.displayMatrixAvailable = true;
                    report.displayMatrix = args.displayMatrix.Value;
                }
                if (!report.poseAvailable && arCamera != null)
                {
                    report.poseAvailable = true;
                    report.cameraWorldPosition = arCamera.transform.position;
                    report.cameraWorldRotation = arCamera.transform.rotation;
                    report.poseSampleRealtimeSeconds = Time.realtimeSinceStartupAsDouble;
                    report.poseCallbackTimestampNs = args.timestampNs ?? 0;
                    var parameters = new XRCameraParams
                    {
                        zNear = arCamera.nearClipPlane, zFar = arCamera.farClipPlane,
                        screenWidth = Screen.width, screenHeight = Screen.height, screenOrientation = Screen.orientation
                    };
                    report.cameraTrackingState = cameraManager.subsystem != null &&
                        cameraManager.subsystem.TryGetLatestFrame(parameters, out var frame) ? frame.trackingState.ToString() : "Unavailable";
                }
                if (!args.timestampNs.HasValue) return;
                long timestamp = args.timestampNs.Value;
                if (!report.timestampAvailable)
                {
                    report.timestampAvailable = true;
                    report.firstFrameTimestampNs = timestamp;
                    report.distinctTimestamps = 1;
                }
                else if (timestamp > previousTimestamp)
                {
                    double intervalMs = (timestamp - previousTimestamp) / 1000000.0;
                    report.minimumIntervalMs = intervals == 0 ? intervalMs : Math.Min(report.minimumIntervalMs, intervalMs);
                    report.maximumIntervalMs = Math.Max(report.maximumIntervalMs, intervalMs);
                    intervalSum += intervalMs;
                    intervals++;
                    report.distinctTimestamps++;
                }
                else { report.nonIncreasingTimestamps++; return; }
                previousTimestamp = timestamp;
                report.lastFrameTimestampNs = timestamp;
            }
            catch (Exception ex) { Warn("Frame probe: " + ex.Message); }
        }

        static Configuration Describe(XRCameraConfiguration config) => new Configuration
        {
            cpuWidth = config.width, cpuHeight = config.height,
            frameRateExposed = config.framerate.HasValue, advertisedFrameRate = config.framerate ?? 0
        };

        void Warn(string message)
        {
            if (report.warnings.Count < 8 && !report.warnings.Contains(message)) report.warnings.Add(message);
        }

        void OnApplicationPause(bool paused) { if (paused) Finish("Application paused"); }
        void OnDisable() { Finish("Component disabled or scene unloaded"); }

        void Finish(string reason)
        {
            if (finished || report == null) return;
            finished = true;
            if (subscribed && cameraManager != null) cameraManager.frameReceived -= OnFrame;
            subscribed = false;
            try
            {
                report.completionReason = reason;
                report.sessionState = ARSession.state.ToString();
                report.notTrackingReason = ARSession.notTrackingReason.ToString();
                report.sessionEnabled = session != null && session.isActiveAndEnabled;
                report.cameraSubsystemRunning = cameraManager != null && cameraManager.subsystem != null && cameraManager.subsystem.running;
                report.cameraPermissionGranted = cameraManager != null && cameraManager.permissionGranted;
                report.observedFrameRate = intervalSum > 0 ? intervals * 1000.0 / intervalSum : 0;
                bool largeTexture = report.gpuTextures.Exists(t => t.width >= 1920 && t.height >= 1080);
                bool encoder = report.androidEncoders != null && report.androidEncoders.encoders.Exists(e =>
                    e.hardwareClassification == "Hardware accelerated" && e.surfaceInput && e.advertised1080p30 && string.IsNullOrEmpty(e.error));
                report.gpuSurfaceAssessment = largeTexture && encoder && report.graphicsApi == "OpenGLES3" ?
                    "Advertised prerequisites present; clean-image transforms, EGL surface interoperability, synchronization and recording performance UNVERIFIED" :
                    "Prerequisites incomplete or unknown; inspect camera textures, hardware classification, surface input and 1080p30 support. No recording attempted";
                string path = "Not written outside Android player";
#if UNITY_ANDROID && !UNITY_EDITOR
                try
                {
                    var folder = Path.Combine(Application.persistentDataPath, "Diagnostics");
                    Directory.CreateDirectory(folder);
                    path = Path.Combine(folder, "camera-capabilities-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".json");
                    File.WriteAllText(path, JsonUtility.ToJson(report, true));
                }
                catch (Exception ex) { path = "Report write failed: " + ex.Message; Debug.LogWarning("[ARCapabilities] " + path, this); }
#endif
                Debug.Log($"[ARCapabilities] {reason}; configs={report.supportedConfigurations.Count}; CPU={report.cpuWidth}x{report.cpuHeight}; GPU textures={report.gpuTextures.Count}; observed cadence={report.observedFrameRate:F1}fps; H.264 encoders={report.androidEncoders?.encoders.Count ?? 0}; warnings={report.warnings.Count}. {report.gpuSurfaceAssessment}. Report: {path}", this);
            }
            catch (Exception ex) { Debug.LogWarning("[ARCapabilities] Finalization failed safely: " + ex.Message, this); }
        }
    }
}
