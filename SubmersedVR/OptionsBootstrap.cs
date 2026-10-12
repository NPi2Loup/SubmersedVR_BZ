using System.IO;
using System.Text;

namespace SubmersedVR
{
    // The first loading screen of the process is built before the game's
    // async options load (GameSettings.LoadAsync) completes, so it would use
    // the C# defaults (LoadingScreenScale 1.5, HudCanvasScaleY 0.30...)
    // instead of the saved values: the very first loading screen of every
    // boot was sized wrong until the options load finished a moment later.
    // Read the saved float keys straight from the options file (plain
    // tag+key+float records, see the forensics in .agents\WIP.md) so the
    // boot canvas and first loading screen already use the user's values.
    // If the file is missing or a key is absent, the C# default is kept.
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

                string ns = "SubmersedVR/";
                Apply(data, ns + "HudCanvasScaleX", out float x); if (x > 0f) Settings.HudCanvasScaleX = x;
                Apply(data, ns + "HudCanvasScaleY", out float y); if (y > 0f) Settings.HudCanvasScaleY = y;
                Apply(data, ns + "HudOffsetX", out float ox); Settings.HudOffsetX = ox;
                Apply(data, ns + "HudOffsetY", out float oy); Settings.HudOffsetY = oy;
                Apply(data, ns + "LoadingScreenScale", out float lss); if (lss > 0f) Settings.LoadingScreenScale = lss;
                string from = primary != null ? primary.Name : fallback.Name;
                Mod.logger.LogInfo($"Options bootstrap: canvasX={Settings.HudCanvasScaleX:0.##} canvasY={Settings.HudCanvasScaleY:0.##} offsetX={Settings.HudOffsetX:0.#} offsetY={Settings.HudOffsetY:0.#} loadingScreenScale={Settings.LoadingScreenScale:0.##} (from {from})");
            }
            catch (System.Exception e)
            {
                Mod.logger.LogDebug("Options bootstrap skipped: " + e);
            }
        }

        private static bool Apply(byte[] data, string key, out float value)
        {
            value = 0f;
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
                    value = System.BitConverter.ToSingle(data, i + kb.Length + 1);
                    return true;
                }
            }
            return false;
        }
    }
}
