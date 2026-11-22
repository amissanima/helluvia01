using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ModLoader;

namespace HelluviaTestHarness;

public class FrameCapture : ModSystem
{
    private static readonly List<string> pending = new();
    public override void Load() => Main.OnPostDraw += Capture;
    public override void Unload() => Main.OnPostDraw -= Capture;

    public static void Request(string name)
    {
        if (!Regex.IsMatch(name, @"\A[a-zA-Z0-9_-]{1,48}\z"))
            throw new ArgumentException("Invalid screenshot name.");
        pending.Add(name);
    }

    private static void Capture(GameTime time)
    {
        Entry.ObserveWorldFrame();
        if (pending.Count == 0 || !Entry.InTestWorld)
            return;
        try
        {
            GraphicsDevice device = Main.instance.GraphicsDevice;
            var size = device.PresentationParameters;
            int width = size.BackBufferWidth, height = size.BackBufferHeight;
            Color[] pixels = new Color[checked(width * height)];
            device.GetBackBufferData(pixels);
            using var texture = new Texture2D(device, width, height);
            // The swap chain can contain valid RGB with undefined alpha.
            for (int i = 0; i < pixels.Length; i++)
                pixels[i].A = 255;
            texture.SetData(pixels);
            foreach (var name in pending)
            {
                var path = Path.Combine(Entry.RunDirectory, name + ".png");
                using (var stream = File.Create(path + ".tmp"))
                    texture.SaveAsPng(stream, width, height);
                File.Move(path + ".tmp", path);
                Entry.Emit("frame", new { name, width, height, source = "game-backbuffer" });
            }
        }
        catch (Exception e)
        {
            Entry.Emit("captureError", new { names = pending.ToArray(), error = e.ToString() });
        }
        finally { pending.Clear(); }
    }
}
