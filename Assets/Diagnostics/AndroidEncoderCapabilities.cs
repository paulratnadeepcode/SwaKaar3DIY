using System;
using System.Collections.Generic;
using UnityEngine;

namespace SwaKaar3DIY.Diagnostics
{
    // Capability enumeration only. Never creates/configures a MediaCodec or opens a camera.
    public static class AndroidEncoderCapabilities
    {
        [Serializable]
        public sealed class Report
        {
            public string status = "Not queried";
            public int androidApi;
            public List<Encoder> encoders = new List<Encoder>();
            public List<string> warnings = new List<string>();
        }

        [Serializable]
        public sealed class Encoder
        {
            public string name;
            public string hardwareClassification = "Unknown (requires API 29+)";
            public int[] colorFormats;
            public bool surfaceInput;
            public bool advertised1080p30;
            public string widths, heights, frameRates, frameRatesAt1080p, bitrate;
            public int widthAlignment, heightAlignment;
            public List<string> profileLevels = new List<string>();
            public string error;
        }

        public static Report Query()
        {
            var report = new Report();
            if (Application.platform != RuntimePlatform.Android || Application.isEditor)
            {
                report.status = "Unavailable outside Android player";
                return report;
            }
            try
            {
                using (var version = new AndroidJavaClass("android.os.Build$VERSION"))
                    report.androidApi = version.GetStatic<int>("SDK_INT");
                using (var list = new AndroidJavaObject("android.media.MediaCodecList", 0))
                {
                    var codecs = list.Call<AndroidJavaObject[]>("getCodecInfos");
                    try
                    {
                        int count = Math.Min(codecs.Length, 256);
                        if (count != codecs.Length) report.warnings.Add("Codec enumeration capped at 256 entries.");
                        for (int i = 0; i < count; i++)
                        {
                            var codec = codecs[i];
                            try
                            {
                                if (!codec.Call<bool>("isEncoder")) continue;
                                foreach (var mime in codec.Call<string[]>("getSupportedTypes"))
                                {
                                    if (!string.Equals(mime, "video/avc", StringComparison.OrdinalIgnoreCase)) continue;
                                    var entry = new Encoder { name = codec.Call<string>("getName") };
                                    report.encoders.Add(entry);
                                    try { Read(codec, mime, report.androidApi, entry); }
                                    catch (Exception ex) { entry.error = ex.Message; }
                                    break;
                                }
                            }
                            catch (Exception ex) { report.warnings.Add("Codec query failed: " + ex.Message); }
                        }
                    }
                    finally { foreach (var codec in codecs) codec?.Dispose(); }
                }
                report.status = "Advertised capabilities only; no encoder instantiated or recording tested";
            }
            catch (Exception ex) { report.status = "Unavailable: " + ex.Message; }
            return report;
        }

        static void Read(AndroidJavaObject codec, string mime, int api, Encoder entry)
        {
            if (api >= 29)
                entry.hardwareClassification = codec.Call<bool>("isHardwareAccelerated") ? "Hardware accelerated" :
                    codec.Call<bool>("isSoftwareOnly") ? "Software only" : "Neither hardware nor software-only flag set";
            using (var caps = codec.Call<AndroidJavaObject>("getCapabilitiesForType", mime))
            {
                entry.colorFormats = caps.Get<int[]>("colorFormats");
                entry.surfaceInput = Array.IndexOf(entry.colorFormats, 0x7F000789) >= 0; // COLOR_FormatSurface
                var profiles = caps.Get<AndroidJavaObject[]>("profileLevels");
                try
                {
                    foreach (var profile in profiles)
                        entry.profileLevels.Add("profile=" + profile.Get<int>("profile") + ", level=" + profile.Get<int>("level"));
                }
                finally { foreach (var profile in profiles) profile?.Dispose(); }
                using (var video = caps.Call<AndroidJavaObject>("getVideoCapabilities"))
                {
                    entry.widths = Range(video, "getSupportedWidths");
                    entry.heights = Range(video, "getSupportedHeights");
                    entry.frameRates = Range(video, "getSupportedFrameRates");
                    entry.bitrate = Range(video, "getBitrateRange");
                    entry.widthAlignment = video.Call<int>("getWidthAlignment");
                    entry.heightAlignment = video.Call<int>("getHeightAlignment");
                    entry.advertised1080p30 = video.Call<bool>("areSizeAndRateSupported", 1920, 1080, 30.0);
                    if (video.Call<bool>("isSizeSupported", 1920, 1080))
                    {
                        using (var range = video.Call<AndroidJavaObject>("getSupportedFrameRatesFor", 1920, 1080))
                            entry.frameRatesAt1080p = range.Call<string>("toString");
                    }
                    else entry.frameRatesAt1080p = "1920x1080 not advertised";
                }
            }
        }

        static string Range(AndroidJavaObject video, string method)
        {
            using (var range = video.Call<AndroidJavaObject>(method)) return range.Call<string>("toString");
        }
    }
}
