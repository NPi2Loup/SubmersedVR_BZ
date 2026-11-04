using System.IO;
using System.Text;
using UnityEngine;

namespace SubmersedVR
{
    // The HUD canvas is built before the game's async options load
    // (GameSettings.LoadAsync) completes, so it would use the C# defaults
    // (HudCanvasScaleY 0.30...) instead of the saved values: the boot canvas
    // was sized wrong until the options load finished a moment later. Read
    // the saved float keys straight from the options file (plain tag+key+
    // float records, see the forensics in .agents\WIP.md) so the boot canvas
    // already uses the user's values. If the file is missing or a key is
    // absent, the C# default is kept.
    static class OptionsBootstrap
    {
        public static void LoadEarlyFloats()
        {
            try
            {
                string gameRoot = Path.GetFullPath(Path.Combine(BepInEx.Paths.PluginPath, "..", ".."));
                string optionsDir = Path.Combine(gameRoot, "SNAppData", "SavedGames", "options");
                if (!Directory.Exists(optionsDir))
                {
                    return;
                }
                // The device-id file is written first by the game; prefer it
                // when present, fall back to options.bin.
                FileInfo primary = null;
                foreach (FileInfo f in new DirectoryInfo(optionsDir).GetFiles("*.bin"))
                {
                    if (f.Name != "options.bin" && (primary == null || f.LastWriteTime >= primary.LastWriteTime))
                    {
                        primary = f;
                    }
                }
                FileInfo fallback = new FileInfo(Path.Combine(optionsDir, "options.bin"));
                byte[] data = primary != null && primary.Exists
                    ? File.ReadAllBytes(primary.FullName)
                    : (fallback.Exists ? File.ReadAllBytes(fallback.FullName) : null);
                if (data == null)
                {
                    return;
                }

                // READ-ONLY: the mod never rewrites the options file. A
                // 26/10 file "repair" (LoadingScreenScale 3.0 -> 1.0 in
                // place) was removed 28/10: rewriting the user's options
                // while the game is running lets the game's own reload
                // propagate the write (the -10 -> 0 offset incident).
                string ns = "SubmersedVR/";
                Apply(data, ns + "HudCanvasScaleX", out float x); if (x > 0f) Settings.HudCanvasScaleX = x;
                Apply(data, ns + "HudCanvasScaleY", out float y); if (y > 0f) Settings.HudCanvasScaleY = y;
                // The offset semantics changed from raw anchoredPosition
                // units (meters here, which flew the HUD out of sight) to
                // centimeters: clamp the legacy file values into the new
                // slider range. Only when the key exists: a missing key
                // must keep the C# default (-10 cm on Y), not reset it to 0.
                if (Apply(data, ns + "HudOffsetX", out float ox)) Settings.HudOffsetX = Mathf.Clamp(ox, -100f, 100f);
                if (Apply(data, ns + "HudOffsetY", out float oy)) Settings.HudOffsetY = Mathf.Clamp(oy, -100f, 100f);
                string from = primary != null ? primary.Name : fallback.Name;
                Mod.logger.LogInfo($"Options bootstrap: canvasX={Settings.HudCanvasScaleX:0.##} canvasY={Settings.HudCanvasScaleY:0.##} offsetX={Settings.HudOffsetX:0.#} offsetY={Settings.HudOffsetY:0.#} (from {from})");
            }
            catch (System.Exception e)
            {
                Mod.logger.LogDebug("Options bootstrap skipped: " + e);
            }
        }

        private static bool Apply(byte[] data, string key, out float value)
        {
            return FindFloat(data, key, out value, out _);
        }

        private static bool FindFloat(byte[] data, string key, out float value, out int offset)
        {
            value = 0f;
            offset = -1;
            byte[] kb = Encoding.ASCII.GetBytes(key);
            for (int i = 0; i <= data.Length - kb.Length - 5; i++)
            {
                bool ok = true;
                for (int j = 0; j < kb.Length; j++)
                {
                    if (data[i + j] != kb[j])
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok && data[i + kb.Length] == 0x15)
                {
                    offset = i + kb.Length + 1;
                    value = System.BitConverter.ToSingle(data, offset);
                    return true;
                }
            }
            return false;
        }
    }
}
