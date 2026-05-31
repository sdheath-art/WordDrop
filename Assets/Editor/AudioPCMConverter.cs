#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace WordDrop.EditorTools
{
    /// <summary>
    /// One-shot batch tool: walks every AudioClip under Assets/Resources/SFX/
    /// and Assets/Resources/Audio/ and forces import settings to PCM
    /// (uncompressed) + Decompress On Load. Eliminates Vorbis/MP3 compression
    /// artifacts on high-frequency content (sizzles, sharp transients,
    /// high-pitched tones) at the cost of ~3-5x larger audio asset size.
    ///
    /// Per Spencer 2026-05-30: SFX with sizzle/high-pitch content were
    /// sounding garbled compared to the source files; PCM restores fidelity.
    ///
    /// Run from menu: WordDrop → Audio → Force PCM on all SFX
    /// </summary>
    public static class AudioPCMConverter
    {
        // Folders to walk. Add more here if you keep audio elsewhere.
        private static readonly string[] AudioRoots = new[]
        {
            "Assets/Resources/SFX",
            "Assets/Resources/Audio",
        };

        [MenuItem("WordDrop/Audio/Force PCM on all SFX")]
        public static void ConvertAllToPCM()
        {
            string[] guids = AssetDatabase.FindAssets("t:AudioClip", AudioRoots);
            if (guids == null || guids.Length == 0)
            {
                Debug.LogWarning("[AudioPCMConverter] No AudioClips found under: " +
                    string.Join(", ", AudioRoots));
                return;
            }

            int converted = 0;
            int skipped   = 0;
            var unchanged = new List<string>();

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                EditorUtility.DisplayProgressBar(
                    "Audio PCM Converter",
                    $"Processing {System.IO.Path.GetFileName(path)} ({i + 1}/{guids.Length})",
                    (float)i / guids.Length);

                var importer = AssetImporter.GetAtPath(path) as AudioImporter;
                if (importer == null) { skipped++; continue; }

                var settings = importer.defaultSampleSettings;
                bool needsChange =
                    settings.compressionFormat != AudioCompressionFormat.PCM ||
                    settings.loadType          != AudioClipLoadType.DecompressOnLoad;

                if (!needsChange) { unchanged.Add(path); continue; }

                settings.loadType          = AudioClipLoadType.DecompressOnLoad;
                settings.compressionFormat = AudioCompressionFormat.PCM;
                settings.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;
                importer.defaultSampleSettings = settings;

                // Also clear platform-specific overrides so iOS/Android use
                // the new default. Without this, mobile builds keep using
                // whatever override was set (often Vorbis at lower quality).
                importer.ClearSampleSettingOverride("Standalone");
                importer.ClearSampleSettingOverride("iOS");
                importer.ClearSampleSettingOverride("Android");
                importer.ClearSampleSettingOverride("WebGL");

                importer.SaveAndReimport();
                converted++;
            }

            EditorUtility.ClearProgressBar();
            Debug.Log($"[AudioPCMConverter] Done. Converted: {converted}, " +
                $"already-PCM: {unchanged.Count}, skipped (non-importer): {skipped}. " +
                $"Total scanned: {guids.Length}.");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        [MenuItem("WordDrop/Audio/Report current compression settings")]
        public static void ReportCurrentSettings()
        {
            string[] guids = AssetDatabase.FindAssets("t:AudioClip", AudioRoots);
            int pcm = 0, vorbis = 0, adpcm = 0, mp3 = 0, other = 0;
            var nonPCM = new List<string>();

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var importer = AssetImporter.GetAtPath(path) as AudioImporter;
                if (importer == null) continue;

                var fmt = importer.defaultSampleSettings.compressionFormat;
                switch (fmt)
                {
                    case AudioCompressionFormat.PCM:    pcm++; break;
                    case AudioCompressionFormat.Vorbis: vorbis++; nonPCM.Add(path); break;
                    case AudioCompressionFormat.ADPCM:  adpcm++; nonPCM.Add(path); break;
                    case AudioCompressionFormat.MP3:    mp3++; nonPCM.Add(path); break;
                    default:                            other++; nonPCM.Add(path); break;
                }
            }

            Debug.Log($"[AudioPCMConverter] Audio compression report — " +
                $"PCM: {pcm}, Vorbis: {vorbis}, ADPCM: {adpcm}, MP3: {mp3}, Other: {other}");
            if (nonPCM.Count > 0)
                Debug.Log("[AudioPCMConverter] Non-PCM files:\n" + string.Join("\n", nonPCM));
        }
    }
}
#endif
