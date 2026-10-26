using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace SwaKaar3DIY.Capture
{
    // Isolated development utility. Never calls the recorder or opens camera resources.
    public sealed class ExistingSyntheticRecordingExport : MonoBehaviour
    {
        const string Basename = "synthetic-20261023-142051-384-91482aa522d741f680c237df9d26c47e";
        const int BufferBytes = 64 * 1024;
        const long MaximumSourceBytes = 512L * 1024 * 1024;
        static ExistingSyntheticRecordingExport instance;
        GameObject canvasObject;
        UnityEngine.UI.Button exportButton;
        UnityEngine.UI.Text information;
        SwaKaar3DIY.UI.NavigationController navigation;
        Scene uiScene;
        string rootPath, selectedDescription, status = "Explicit export only; no recording will start.";
        bool paused;
        Work work;

        [Serializable] public sealed class Frame { public int frame; public long ptsNs; }
        [Serializable] public sealed class RecordingMetadata
        {
            public int schemaVersion, width, height, fps, targetFrames, requestedFrames;
            public int skippedPacing, skippedQueue, unscheduledSlots;
            public bool nativePass, cancelled;
            public string output, nativeReport;
            public Frame[] frames;
        }
        [Serializable] public sealed class ExportResult
        {
            public bool success, published, preservedEntry;
            public string source, uri, filename, sha256, error, cleanupError;
            public long bytes, expectedBytes, copiedBytes, readbackBytes, descriptorBytes;
            public string sourceSha256, destinationSha256;
            public string[] metadataAttempts;
            public MetadataObservation[] metadata;
            public bool insertedByThisAttempt, ownerAccessConfirmed, publicationStarted, publishedVerificationPassed;
            public string ownerPackage, publicationState = "not-started", postPublicationSha256;
            public int publicationUpdateCount;
            public long postPublicationDescriptorBytes, postPublicationReadbackBytes;
        }
        sealed class Work
        {
            public string root;
            public volatile bool cancel, done;
            public ExportResult result;
#if UNITY_ANDROID && !UNITY_EDITOR
            // Owned by this dedicated worker; disposed before JNI detach, or by main on attach failure.
            public AndroidJavaObject context;
#endif
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Debug.isDebugBuild || Application.platform != RuntimePlatform.Android || instance != null) return;
            var go = new GameObject("ExistingSyntheticRecordingExportDevelopment");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<ExistingSyntheticRecordingExport>();
            instance.rootPath = Path.Combine(Application.persistentDataPath, "SyntheticEncoderCheckpoint");
            SceneManager.sceneLoaded += instance.SceneLoaded;
            SceneManager.sceneUnloaded += instance.SceneUnloaded;
            instance.AttachUi(SceneManager.GetActiveScene());
#endif
        }
        void SceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.path == "Assets/Scenes/ARTestScene.unity") AttachUi(scene);
        }
        void SceneUnloaded(Scene scene)
        {
            if (scene != uiScene) return;
            CancelExport();
            if (canvasObject != null) Destroy(canvasObject);
            canvasObject = null;
            exportButton = null;
            information = null;
            navigation = null;
        }
        void AttachUi(Scene scene)
        {
            if (!Debug.isDebugBuild || scene.path != "Assets/Scenes/ARTestScene.unity" || canvasObject != null) return;
            foreach (var root in scene.GetRootGameObjects())
                if (navigation == null) navigation = root.GetComponentInChildren<SwaKaar3DIY.UI.NavigationController>(true);
            if (navigation == null) { Debug.LogWarning("[ExistingMP4Export] Navigation reference missing; export UI disabled."); return; }
            uiScene = scene;
            canvasObject = new GameObject("ExistingMP4ExportCanvas", typeof(RectTransform), typeof(Canvas),
                typeof(UnityEngine.UI.CanvasScaler), typeof(UnityEngine.UI.GraphicRaycaster));
            SceneManager.MoveGameObjectToScene(canvasObject, scene);
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = -1; // Existing navigation dialogs retain input priority.
            var scaler = canvasObject.GetComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0.5f;
            var safe = new GameObject("SafeArea", typeof(RectTransform), typeof(SwaKaar3DIY.UI.SafeAreaLayout));
            safe.transform.SetParent(canvasObject.transform, false);
            var control = new GameObject("ExportExistingMP4", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
            var rect = control.GetComponent<RectTransform>();
            rect.SetParent(safe.transform, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0);
            rect.anchoredPosition = new Vector2(0, 344);
            rect.sizeDelta = new Vector2(640, 88);
            var image = control.GetComponent<UnityEngine.UI.Image>();
            image.color = new Color(0.12f, 0.16f, 0.18f, 0.94f);
            exportButton = control.GetComponent<UnityEngine.UI.Button>();
            exportButton.targetGraphic = image;
            exportButton.navigation = new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.None };
            exportButton.onClick.AddListener(RequestExport);
            var label = MakeText(rect, "Export existing Sequential150 MP4", 25);
            label.rectTransform.anchorMin = Vector2.zero;
            label.rectTransform.anchorMax = Vector2.one;
            label.rectTransform.offsetMin = new Vector2(8, 8);
            label.rectTransform.offsetMax = new Vector2(-8, -8);
            information = MakeText(safe.transform, "", 20);
            information.rectTransform.anchorMin = information.rectTransform.anchorMax = information.rectTransform.pivot = new Vector2(0.5f, 0);
            information.rectTransform.anchoredPosition = new Vector2(0, 440);
            information.rectTransform.sizeDelta = new Vector2(960, 160);
            RefreshSelection();
        }
        static UnityEngine.UI.Text MakeText(Transform parent, string text, int size)
        {
            var label = new GameObject("Label", typeof(RectTransform), typeof(UnityEngine.UI.Text)).GetComponent<UnityEngine.UI.Text>();
            label.transform.SetParent(parent, false);
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = size;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = Color.white;
            label.raycastTarget = false;
            var shadow = label.gameObject.AddComponent<UnityEngine.UI.Shadow>();
            shadow.effectColor = new Color(0, 0, 0, 0.95f);
            shadow.effectDistance = new Vector2(2, -2);
            label.text = text;
            return label;
        }
        void RefreshSelection()
        {
            try
            {
                string path = SafePath(rootPath, ".mp4");
                var file = new FileInfo(path);
                selectedDescription = Basename + ".mp4\nSource: " + file.Length.ToString(CultureInfo.InvariantCulture) + " bytes";
            }
            catch (Exception ex) { selectedDescription = Basename + ".mp4\nUnavailable: " + ex.Message; }
        }
        void RequestExport()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Debug.isDebugBuild || paused || work != null || navigation == null || navigation.IsBlockingNavigation) return;
            AndroidJavaObject context = null;
            try
            {
                using (var version = new AndroidJavaClass("android.os.Build$VERSION"))
                    if (version.GetStatic<int>("SDK_INT") < 29) throw new NotSupportedException("MediaStore Downloads requires Android API 29+.");
                // Selection is fixed. Validate it before creating any destination or worker.
                ValidateMetadata(rootPath);
                SafePath(rootPath, ".mp4");
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                    context = activity.Call<AndroidJavaObject>("getApplicationContext");
                var pending = new Work { root = rootPath, context = context };
                var thread = new Thread(() => Run(pending)) { IsBackground = true, Name = "ExistingMP4Export" };
                work = pending;
                try { thread.Start(); }
                catch { work = null; throw; }
                context = null; // Dedicated worker now owns this global Java reference.
                status = "Exporting existing file; original recording is preserved.";
            }
            catch (Exception ex)
            {
                context?.Dispose();
                status = "Export unavailable: " + ex.Message;
                Debug.LogWarning("[ExistingMP4Export] " + status);
            }
#else
            status = "Android development player required.";
#endif
        }
        void Update()
        {
            if (work != null && (paused || canvasObject == null || navigation == null || navigation.IsBlockingNavigation))
                CancelExport();
            if (work != null && work.done)
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                try { work.context?.Dispose(); } // Only needed if JNI attachment failed.
                catch (Exception ex) { Debug.LogWarning("[ExistingMP4Export] Main-thread JNI reference cleanup: " + ex.Message); }
#endif
                var result = work.result;
                status = result.success
                    ? "Exported " + result.bytes + " bytes\n" + result.filename + "\n" + result.uri + "\nSHA-256: " + result.sha256
                    : "Export failed: " + result.error + (result.preservedEntry ? "\nPreserved destination: " + result.uri : "") +
                        (string.IsNullOrEmpty(result.cleanupError) ? "" : "\nCleanup: " + result.cleanupError);
                Debug.Log("[ExistingMP4Export] " + JsonUtility.ToJson(result));
                work = null;
                RefreshSelection();
            }
            if (exportButton != null) exportButton.interactable = Debug.isDebugBuild && !paused && work == null &&
                navigation != null && !navigation.IsBlockingNavigation;
            if (information != null) information.text = selectedDescription + "\n" + status;
        }
        void CancelExport() { if (work != null) work.cancel = true; }
        void OnApplicationPause(bool value) { paused = value; if (value) CancelExport(); }
        void OnApplicationQuit() { CancelExport(); }
        void OnDestroy()
        {
            CancelExport();
            SceneManager.sceneLoaded -= SceneLoaded;
            SceneManager.sceneUnloaded -= SceneUnloaded;
            if (instance == this) instance = null;
        }

        static string SafePath(string root, string extension)
        {
            if (string.IsNullOrEmpty(root) || (extension != ".mp4" && extension != ".json")) throw new IOException("Unsafe source selection.");
            string folder = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string file = Path.GetFullPath(Path.Combine(folder, Basename + extension));
            if (Path.GetDirectoryName(file) != folder || Path.GetFileName(file) != Basename + extension ||
                file.EndsWith(".partial", StringComparison.Ordinal)) throw new IOException("Unsafe source path.");
            var info = new FileInfo(file);
            long limit = extension == ".json" ? 1024 * 1024 : MaximumSourceBytes;
            if (!info.Exists || info.Length <= 0 || info.Length > limit || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Source missing, empty, linked or oversized.");
            return file;
        }
        static void ValidateMetadata(string root)
        {
            string mp4 = SafePath(root, ".mp4"), json = SafePath(root, ".json");
            var report = JsonUtility.FromJson<RecordingMetadata>(File.ReadAllText(json));
            if (report == null || report.schemaVersion != 2 || !report.nativePass || report.cancelled ||
                report.width != 1920 || report.height != 1080 || report.fps != 30 || report.targetFrames != 150 ||
                report.requestedFrames != 150 || report.skippedPacing != 0 || report.skippedQueue != 0 ||
                report.unscheduledSlots != 0 || report.frames == null || report.frames.Length != 150 ||
                string.IsNullOrEmpty(report.output) || Path.GetFullPath(report.output) != mp4)
                throw new InvalidDataException("Companion JSON is not successful uncancelled Sequential150 schema 2.");
            for (int i = 0; i < 150; i++)
                if (report.frames[i] == null || report.frames[i].frame != i || report.frames[i].ptsNs != (long)i * 1000000000L / 30)
                    throw new InvalidDataException("Companion frame sequence/timestamps are invalid.");
            string summary = (report.nativeReport ?? "").Split(new[] { "; trace=" }, StringSplitOptions.None)[0];
            foreach (string key in new[] { "pass", "cleanup", "finalized", "EOS" }) RequireToken(summary, key, "1");
            foreach (string key in new[] { "cancelled" }) RequireToken(summary, key, "0");
            foreach (string key in new[] { "submitted", "swapped", "encodedSamples" }) RequireToken(summary, key, "150");
            RequireToken(summary, "firstFailure", "none");
        }
        static void RequireToken(string summary, string key, string expected)
        {
            var matches = Regex.Matches(summary, "(?:^|\\s)" + Regex.Escape(key) + "=([^\\s;]+)");
            if (matches.Count != 1 || matches[0].Groups[1].Value != expected) throw new InvalidDataException("Invalid native completion token: " + key);
        }
        static void Check(Work work, Stopwatch timer)
        {
            if (work.cancel) throw new OperationCanceledException("Export cancelled.");
            if (timer.Elapsed.TotalSeconds > 45) throw new TimeoutException("Export deadline exceeded.");
        }
        static string FileHash(string path, Work work, Stopwatch timer)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[BufferBytes];
                for (;;)
                {
                    Check(work, timer);
                    int count = input.Read(buffer, 0, buffer.Length);
                    if (count == 0) break;
                    sha.TransformBlock(buffer, 0, count, buffer, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        [Serializable] public sealed class MetadataColumn
        {
            public string name, state, value;
            public int index, type;
        }
        [Serializable] public sealed class MetadataObservation
        {
            public string phase, warning;
            public int attempt;
            public double elapsedMs;
            public MetadataColumn[] columns;
        }
        sealed class Row
        {
            public MetadataColumn name, size, pending, owner;
            public MetadataColumn[] Columns => new[] { name, size, pending, owner };
        }
        static MetadataColumn Column(string name, int index, int type, bool isNull, string value)
        {
            return new MetadataColumn { name = name, index = index, type = type,
                state = index < 0 ? "missing" : isNull ? "null" : type == 1 ? "integer" : "unexpected-type",
                value = index < 0 || isNull ? null : value };
        }
        static bool PendingEquals(Row row, int expected)
        {
            return row != null && row.pending != null && row.pending.state == "integer" &&
                row.pending.value == expected.ToString(CultureInfo.InvariantCulture);
        }
        static bool OwnerAvailable(Row row)
        {
            return row != null && row.owner != null && row.owner.index >= 0 &&
                row.owner.type == 3 && row.owner.value != null;
        }
        static string IdentityMismatch(Row row, string package, int requiredPending)
        {
            if (row == null) return "Destination cursor row missing.";
            if (!PendingEquals(row, requiredPending)) return "Destination pending-state unavailable or mismatched.";
            if (row.name == null || row.name.index < 0 || row.name.type != 3 || string.IsNullOrEmpty(row.name.value))
                return "Destination display-name unavailable or invalid.";
            if (OwnerAvailable(row) && row.owner.value != package) return "Destination owner mismatch.";
            return null;
        }
        static bool NumericSize(Row row, out double value)
        {
            value = 0;
            var column = row == null ? null : row.size;
            return column != null && column.index >= 0 && column.state != "null" &&
                (column.type == 1 || column.type == 2 || column.type == 3) &&
                double.TryParse(column.value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
        static string MetadataMismatch(Row row, long expected, int requiredPending, string package)
        {
            string issue = IdentityMismatch(row, package, requiredPending);
            if (issue != null) return issue;
            double value;
            if (NumericSize(row, out value) && value != expected) return "Destination numeric metadata-size mismatch.";
            return null; // Unavailable indexed size never replaces descriptor/readback/hash verification.
        }
        static void Observe(Row row, string phase, int attempt, Stopwatch timer, ExportResult result)
        {
            double ignored;
            string warning = NumericSize(row, out ignored) ? null : "_size unavailable; byte-level verification required.";
            if (!OwnerAvailable(row)) warning = (warning ?? "") + " Owner metadata unavailable; insert provenance and pending owner access required.";
            var items = new List<MetadataObservation>(result.metadata ?? Array.Empty<MetadataObservation>());
            items.Add(new MetadataObservation { phase = phase, attempt = attempt, elapsedMs = timer.Elapsed.TotalMilliseconds,
                warning = warning, columns = row == null ? Array.Empty<MetadataColumn>() : row.Columns });
            result.metadata = items.ToArray();
        }
        static Row WaitForMetadata(Func<Row> read, Action<int> delay, Work work, Stopwatch timer,
            long expected, int requiredPending, string phase, ExportResult result)
        {
            const int attempts = 5, intervalMs = 200;
            var trace = new List<string>(result.metadataAttempts ?? Array.Empty<string>());
            string mismatch = null;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                Check(work, timer);
                var row = read();
                Observe(row, phase, attempt + 1, timer, result);
                if (!string.IsNullOrEmpty(result.cleanupError)) throw new IOException("Destination query resource close failed.");
                mismatch = MetadataMismatch(row, expected, requiredPending, result.ownerPackage);
                trace.Add(phase + " attempt=" + (attempt + 1) + " issue=" + (mismatch ?? "none"));
                result.metadataAttempts = trace.ToArray();
                Check(work, timer);
                if (mismatch == null) return row;
                if (attempt + 1 < attempts)
                    for (int elapsed = 0; elapsed < intervalMs; elapsed += 20) { Check(work, timer); delay(20); }
            }
            throw new IOException(mismatch);
        }
        static bool MayDeletePending(Row row, ExportResult result)
        {
            // Publication attempts are irreversible for cleanup, even when update throws or returns zero.
            return result.insertedByThisAttempt && !result.publicationStarted && !result.published &&
                PendingEquals(row, 1) && IdentityMismatch(row, result.ownerPackage, 1) == null &&
                (result.ownerAccessConfirmed || (OwnerAvailable(row) && row.owner.value == result.ownerPackage));
        }
        static void InspectState(Row row, ExportResult result)
        {
            if (PendingEquals(row, 0)) { result.published = true; result.publicationState = "published"; }
            else if (PendingEquals(row, 1)) result.publicationState = result.published ? "inconsistent-after-update" : "pending";
            else result.publicationState = "unknown";
        }
        static void Publish(Func<int> update, Func<Row> read, Action verifyPublished, Action<int> delay,
            Work work, Stopwatch timer, long expected, ExportResult result)
        {
            if (!result.insertedByThisAttempt || !result.ownerAccessConfirmed) throw new IOException("Pending owner access not confirmed.");
            WaitForMetadata(read, delay, work, timer, expected, 1, "before-publication", result);
            Check(work, timer);
            result.publicationStarted = true;
            result.publicationState = "unknown";
            result.publicationUpdateCount = update();
            if (result.publicationUpdateCount != 1) throw new IOException("Publication did not update exactly one entry.");
            result.published = true; // A successful update is evidence even if the following query fails.
            result.publicationState = "published";
            var final = WaitForMetadata(read, delay, work, timer, expected, 0, "after-publication", result);
            result.filename = final.name.value;
            verifyPublished();
            if (!string.IsNullOrEmpty(result.cleanupError)) throw new IOException("Published verification resource close failed.");
            result.publishedVerificationPassed = true;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        static MetadataColumn ReadColumn(AndroidJavaObject cursor, string name)
        {
            int index = cursor.Call<int>("getColumnIndex", name);
            if (index < 0) return Column(name, index, -1, false, null);
            bool isNull = cursor.Call<bool>("isNull", index);
            int type = cursor.Call<int>("getType", index);
            string value = null;
            if (!isNull)
            {
                if (type == 1) value = cursor.Call<long>("getLong", index).ToString(CultureInfo.InvariantCulture);
                else if (type == 2) value = cursor.Call<double>("getDouble", index).ToString("R", CultureInfo.InvariantCulture);
                else if (type == 3) value = cursor.Call<string>("getString", index);
            }
            var column = Column(name, index, type, isNull, value);
            if (index >= 0 && !isNull && type == 3) column.state = "string";
            return column;
        }
        static Row ReadRow(AndroidJavaObject resolver, AndroidJavaObject uri, ExportResult result)
        {
            using (var cursor = resolver.Call<AndroidJavaObject>("query", uri,
                new[] { "_display_name", "_size", "is_pending", "owner_package_name" }, null, null, null))
            {
                if (cursor == null) throw new IOException("Cannot query destination entry.");
                try
                {
                    if (!cursor.Call<bool>("moveToFirst")) throw new IOException("Destination cursor row missing.");
                    return new Row { name = ReadColumn(cursor, "_display_name"), size = ReadColumn(cursor, "_size"),
                        pending = ReadColumn(cursor, "is_pending"), owner = ReadColumn(cursor, "owner_package_name") };
                }
                finally { CloseJava(cursor, result); }
            }
        }
        static void CloseJava(AndroidJavaObject obj, ExportResult result)
        {
            try { obj.Call("close"); }
            catch (Exception ex) { result.cleanupError = (result.cleanupError ?? "") + " Close: " + ex.Message; }
        }
        static void DisposeJava(AndroidJavaObject obj, ExportResult result)
        {
            try { obj?.Dispose(); }
            catch (Exception ex) { result.cleanupError = (result.cleanupError ?? "") + " JNI reference: " + ex.Message; }
        }
        static void PutPending(AndroidJavaObject values, int value)
        {
            using (var integer = new AndroidJavaObject("java.lang.Integer", value))
                values.Call("put", "is_pending", integer);
        }
        static string Canonical(string path)
        {
            using (var file = new AndroidJavaObject("java.io.File", path)) return file.Call<string>("getCanonicalPath");
        }
        static string Digest(AndroidJavaObject digest)
        {
            var signed = digest.Call<sbyte[]>("digest");
            var chars = new char[signed.Length * 2];
            const string hex = "0123456789abcdef";
            for (int i = 0; i < signed.Length; i++) { byte b = unchecked((byte)signed[i]); chars[2*i] = hex[b >> 4]; chars[2*i+1] = hex[b & 15]; }
            return new string(chars);
        }
        static AndroidJavaObject NewDigest()
        {
            using (var type = new AndroidJavaClass("java.security.MessageDigest")) return type.CallStatic<AndroidJavaObject>("getInstance", "SHA-256");
        }
        static AndroidJavaObject NewBuffer()
        {
            using (var type = new AndroidJavaClass("java.nio.ByteBuffer")) return type.CallStatic<AndroidJavaObject>("allocateDirect", BufferBytes);
        }
        // Calls returning Buffer/ByteBuffer create local/global references in Unity's bridge; dispose each wrapper.
        static void BufferCall(AndroidJavaObject buffer, string method)
        {
            using (var ignored = buffer.Call<AndroidJavaObject>(method)) { }
        }
        static void Run(Work work)
        {
            var result = new ExportResult();
            bool attached = false;
            AndroidJavaObject resolver = null, uri = null;
            var timer = Stopwatch.StartNew();
            try
            {
                if (AndroidJNI.AttachCurrentThread() != 0) throw new IOException("Cannot attach export worker to JNI.");
                attached = true; // This is a new dedicated managed thread, not a reused JVM/pool thread.
                Check(work, timer);
                string folder = Canonical(work.root);
                string source = SafePath(work.root, ".mp4"), json = SafePath(work.root, ".json");
                if (Canonical(source) != folder + "/" + Basename + ".mp4" || Canonical(json) != folder + "/" + Basename + ".json")
                    throw new IOException("Canonical source escaped its fixed recording directory.");
                ValidateMetadata(work.root);
                result.source = source;
                var sourceInfo = new FileInfo(source);
                long expected = sourceInfo.Length;
                result.expectedBytes = expected;
                long originalMtime = sourceInfo.LastWriteTimeUtc.Ticks;
                string sourceHash = FileHash(source, work, timer), jsonHash = FileHash(json, work, timer);
                result.sourceSha256 = sourceHash;
                Check(work, timer);
                result.ownerPackage = work.context.Call<string>("getPackageName");
                if (result.ownerPackage != "com.swakaar3diy.app") throw new IOException("Unexpected application package.");
                resolver = work.context.Call<AndroidJavaObject>("getContentResolver");
                string name = Basename + "-export-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) +
                    "-" + Guid.NewGuid().ToString("N") + ".mp4";
                using (var downloads = new AndroidJavaClass("android.provider.MediaStore$Downloads"))
                using (var collection = downloads.CallStatic<AndroidJavaObject>("getContentUri", "external_primary"))
                using (var values = new AndroidJavaObject("android.content.ContentValues"))
                {
                    values.Call("put", "_display_name", name);
                    values.Call("put", "mime_type", "video/mp4");
                    values.Call("put", "relative_path", "Download/SwaKaar3DIY/");
                    PutPending(values, 1);
                    uri = resolver.Call<AndroidJavaObject>("insert", collection, values);
                }
                if (uri == null) throw new IOException("MediaStore insert returned no URI.");
                result.uri = uri.Call<string>("toString");
                result.filename = name;
                result.insertedByThisAttempt = true;
                var inserted = ReadRow(resolver, uri, result);
                Observe(inserted, "inserted", 1, timer, result);
                string identityIssue = IdentityMismatch(inserted, result.ownerPackage, 1);
                if (identityIssue != null) throw new IOException(identityIssue);
                if (!string.IsNullOrEmpty(result.cleanupError)) throw new IOException("Initial cursor close failed.");
                long copied = Copy(work, timer, source, resolver, uri, result);
                result.copiedBytes = copied;
                if (!string.IsNullOrEmpty(result.cleanupError)) throw new IOException("Copy resource close failed.");
                if (copied != expected) throw new IOException("Copied byte count differs from source.");
                Check(work, timer);
                string destinationHash = VerifyDestination(work, timer, resolver, uri, expected, result);
                result.destinationSha256 = destinationHash;
                if (!string.IsNullOrEmpty(result.cleanupError)) throw new IOException("Verification resource close failed.");
                sourceInfo.Refresh();
                if (sourceInfo.Length != expected || sourceInfo.LastWriteTimeUtc.Ticks != originalMtime ||
                    FileHash(source, work, timer) != sourceHash || FileHash(json, work, timer) != jsonHash || destinationHash != sourceHash)
                    throw new IOException("Source or destination verification mismatch.");
                Publish(() =>
                {
                    using (var values = new AndroidJavaObject("android.content.ContentValues"))
                    {
                        PutPending(values, 0);
                        return resolver.Call<int>("update", uri, values, null, null);
                    }
                }, () => ReadRow(resolver, uri, result), () =>
                {
                    result.postPublicationSha256 = VerifyDestination(work, timer, resolver, uri, expected, result, true);
                    if (result.postPublicationSha256 != sourceHash) throw new IOException("Published SHA-256 mismatch.");
                }, Thread.Sleep, work, timer, expected, result);
                result.bytes = copied; result.sha256 = sourceHash;
                result.success = true;
            }
            catch (Exception ex)
            {
                result.error = ex.GetType().Name + ": " + ex.Message; // Preserve first failure separately from cleanup.
                if (uri != null)
                {
                    result.preservedEntry = true;
                    try
                    {
                        var row = ReadRow(resolver, uri, result);
                        Observe(row, "failure-state", 1, timer, result);
                        InspectState(row, result);
                        if (MayDeletePending(row, result))
                        {
                            if (resolver.Call<int>("delete", uri, null, null) != 1) throw new IOException("Pending-entry cleanup did not delete exactly one row.");
                            result.preservedEntry = false;
                        }

                    }
                    catch (Exception cleanup) { result.cleanupError = (result.cleanupError ?? "") + " Pending entry: " + cleanup.GetType().Name + ": " + cleanup.Message; }
                }
                if (result.published) result.error = "published, verification failed/incomplete: " + result.error;
                else if (result.publicationStarted) result.error = "Publication attempted; entry preserved, verification incomplete: " + result.error;
            }
            finally
            {
                if (attached)
                {
                    DisposeJava(uri, result);
                    DisposeJava(resolver, result);
                    DisposeJava(work.context, result); work.context = null;
                    try
                    {
                        if (AndroidJNI.DetachCurrentThread() != 0)
                            result.cleanupError = (result.cleanupError ?? "") + " JNI detach failed.";
                    }
                    catch (Exception ex) { result.cleanupError = (result.cleanupError ?? "") + " JNI detach: " + ex.Message; }
                    if (!string.IsNullOrEmpty(result.cleanupError) && result.success)
                    {
                        result.success = false; result.preservedEntry = true;
                        result.publishedVerificationPassed = false;
                        result.error = "published, verification failed/incomplete: final JNI/resource cleanup reported an error.";
                    }
                }
                work.result = result;
                work.done = true; // Volatile publication; main thread owns all UI/logging.
            }
        }
        static long Copy(Work work, Stopwatch timer, string source, AndroidJavaObject resolver, AndroidJavaObject uri, ExportResult result)
        {
            using (var input = new AndroidJavaObject("java.io.FileInputStream", source))
            {
                try
                {
                    using (var inputChannel = input.Call<AndroidJavaObject>("getChannel"))
                    using (var pfd = resolver.Call<AndroidJavaObject>("openFileDescriptor", uri, "w"))
                    {
                        if (pfd == null) throw new IOException("Cannot open pending output.");
                        result.ownerAccessConfirmed = true; // Same inserted URI was confirmed pending before owner-only open.
                        AndroidJavaObject output = null;
                        try
                        {
                            output = new AndroidJavaObject("android.os.ParcelFileDescriptor$AutoCloseOutputStream", pfd);
                            using (var outputChannel = output.Call<AndroidJavaObject>("getChannel"))
                            using (var buffer = NewBuffer())
                            {
                                long total = 0;
                                for (;;)
                                {
                                    Check(work, timer); BufferCall(buffer, "clear");
                                    int count = inputChannel.Call<int>("read", buffer);
                                    if (count == -1) break;
                                    if (count <= 0 || count > BufferBytes || total + count > MaximumSourceBytes) throw new IOException("Invalid source read progress.");
                                    BufferCall(buffer, "flip");
                                    while (buffer.Call<bool>("hasRemaining"))
                                    {
                                        Check(work, timer);
                                        if (outputChannel.Call<int>("write", buffer) <= 0) throw new IOException("Output write made no progress.");
                                    }
                                    total += count;
                                }
                                outputChannel.Call("force", true);
                                return total;
                            }
                        }
                        finally
                        {
                            if (output != null) { try { CloseJava(output, result); } finally { output.Dispose(); } }
                            else CloseJava(pfd, result);
                        }
                    }
                }
                finally { CloseJava(input, result); }
            }
        }
        static string VerifyDestination(Work work, Stopwatch timer, AndroidJavaObject resolver, AndroidJavaObject uri, long expected, ExportResult result, bool postPublication = false)
        {
            using (var pfd = resolver.Call<AndroidJavaObject>("openFileDescriptor", uri, "r"))
            {
                if (pfd == null) throw new IOException("Cannot reopen pending destination.");
                AndroidJavaObject input = null;
                try
                {
                    input = new AndroidJavaObject("android.os.ParcelFileDescriptor$AutoCloseInputStream", pfd);
                    using (var channel = input.Call<AndroidJavaObject>("getChannel"))
                    using (var digest = NewDigest())
                    using (var buffer = NewBuffer())
                    {
                        long descriptor = channel.Call<long>("size");
                        if (postPublication) result.postPublicationDescriptorBytes = descriptor;
                        else result.descriptorBytes = descriptor;
                        if (descriptor != expected) throw new IOException("Destination descriptor size mismatch.");
                        long total = 0;
                        for (;;)
                        {
                            Check(work, timer); BufferCall(buffer, "clear");
                            int count = channel.Call<int>("read", buffer);
                            if (count == -1) break;
                            if (count <= 0 || count > BufferBytes || total + count > expected) throw new IOException("Invalid verification read progress.");
                            total += count;
                            if (postPublication) result.postPublicationReadbackBytes = total;
                            else result.readbackBytes = total;
                            BufferCall(buffer, "flip"); digest.Call("update", buffer);
                        }
                        if (total != expected) throw new IOException("Destination verification byte count mismatch.");
                        return Digest(digest);
                    }
                }
                finally
                {
                    if (input != null) { try { CloseJava(input, result); } finally { input.Dispose(); } }
                    else pfd.Call("close");
                }
            }
        }
#endif
    }
}
