using System;
using System.IO;
using BepInEx;
using UnityEngine;

namespace ValheimTestKit
{
    /// <summary>
    /// Screenshot helper for client-side suites. Renders the game camera into a texture, so it also works on a
    /// headless X server (Xvfb) where there is no window to photograph.
    /// </summary>
    public static class Screen
    {
        /// <summary>Saves a PNG of the current view and returns its path. Returns null on a dedicated server.</summary>
        public static string Capture(string name, int width = 0, int height = 0)
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                return null;
            }

            int w = width > 0 ? width : UnityEngine.Screen.width;
            int h = height > 0 ? height : UnityEngine.Screen.height;
            if (w <= 0 || h <= 0)
            {
                w = 1280;
                h = 720;
            }

            RenderTexture previous = RenderTexture.active;
            RenderTexture target = RenderTexture.GetTemporary(w, h, 24);
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;

                var texture = new Texture2D(w, h, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                texture.Apply();

                byte[] png = texture.EncodeToPNG();
                UnityEngine.Object.Destroy(texture);

                string dir = Path.Combine(Paths.ConfigPath, "valheim-testkit", "screenshots");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, name + ".png");
                File.WriteAllBytes(path, png);
                return path;
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
            }
        }
    }
}
