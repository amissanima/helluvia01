using System;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameInput;
using Terraria.ModLoader;

namespace HelluviaTestHarness;

// Commands supplied after a player update take effect during the following game's input poll.
public sealed class ScriptInput : ModSystem
{
    private static string button, lastButton, hotkey, lastHotkey;
    private static Vector2 aim = Vector2.UnitX * 160;
    private static bool applied;
    private static bool Enabled => Entry.InTestWorld && Entry.Scenario["steps"] is JsonArray;
    public static bool AwaitingReleaseSample => lastButton != null && button == null;
    public static bool AwaitingHotkeyRelease => lastHotkey != null;
    public static int PressEdges { get; private set; }
    public static int HotkeyEdges { get; private set; }

    public static void Press(string which, Vector2 aimOffset)
    {
        if (!float.IsFinite(aimOffset.LengthSquared()) || aimOffset.LengthSquared() < 1)
            throw new ArgumentException("The aim offset must be finite and nonzero.");
        button = which switch {
            "primary" or "normal" => "MouseLeft",
            "alternate" => "MouseRight",
            _ => throw new ArgumentException($"Unknown attack button: {which}.")
        };
        aim = aimOffset;
    }

    public static void Release() => button = null;

    public static void Hotkey(string fullName)
    {
        // SetupKeys populates KeyStatus with vanilla trigger names and the full
        // names of every registered ModKeybind. ProcessTriggers reads this same map.
        string key = fullName?.StartsWith("Terraria/", StringComparison.Ordinal) == true
            ? fullName.Substring("Terraria/".Length) : fullName;
        if (string.IsNullOrWhiteSpace(key) || !PlayerInput.Triggers.Current.KeyStatus.ContainsKey(key))
            throw new ArgumentException($"Unregistered input trigger: {fullName}.");
        if (hotkey != null || key == lastHotkey)
            throw new InvalidOperationException("Allow a released input tick between consecutive hotkey pulses.");
        hotkey = key;
    }

    private static void SetKeys(TriggersSet set, string attack, string key)
    {
        set.Reset();
        if (attack != null)
            set.KeyStatus[attack] = true;
        if (key != null)
            set.KeyStatus[key] = true;
    }

    public override void PostUpdateInput()
    {
        if (!Enabled)
            return;
        applied = true;
        var pack = PlayerInput.Triggers;
        SetKeys(pack.Old, lastButton, lastHotkey);
        SetKeys(pack.Current, button, hotkey);
        pack.Update(); // Have Terraria compute JustPressed and JustReleased from the two samples.
        Main.mouseLeft = pack.Current.MouseLeft;
        Main.mouseRight = pack.Current.MouseRight;
        Main.mouseMiddle = pack.Current.MouseMiddle;
        Main.mouseXButton1 = pack.Current.MouseXButton1;
        Main.mouseXButton2 = pack.Current.MouseXButton2;
        PlayerInput.ScrollWheelDelta = PlayerInput.ScrollWheelDeltaForUI = 0;

        // The UI and world have different zoom contexts. Cache the unscaled
        // physical screen coordinate before the engine switches between them.
        Vector2 cursor = Cursor(Main.LocalPlayer);
        Vector2 center = new(Main.screenWidth * .5f, Main.screenHeight * .5f);
        Vector2 screen = center + (cursor - center) * Main.GameViewMatrix.Zoom;
        Main.mouseX = PlayerInput.MouseX = (int)screen.X;
        Main.mouseY = PlayerInput.MouseY = (int)screen.Y;
        PlayerInput.CacheMousePositionForZoom();

        if (button != null && pack.JustPressed.KeyStatus[button])
            PressEdges++;
        if (hotkey != null && pack.JustPressed.KeyStatus[hotkey])
            HotkeyEdges++;
        if (button != lastButton || hotkey != lastHotkey)
            Entry.Emit("input", new { button, hotkey, previousButton = lastButton, previousHotkey = lastHotkey,
                leftPressed = pack.JustPressed.MouseLeft, rightPressed = pack.JustPressed.MouseRight,
                leftReleased = pack.JustReleased.MouseLeft, rightReleased = pack.JustReleased.MouseRight,
                hotkeyPressed = hotkey != null && pack.JustPressed.KeyStatus[hotkey],
                pressEdges = PressEdges, hotkeyEdges = HotkeyEdges, aimX = aim.X, aimY = aim.Y });
        lastButton = button;
        lastHotkey = hotkey;
        hotkey = null;
    }

    private static Vector2 Cursor(Player player)
    {
        Vector2 cursor = player.MountedCenter + aim - Main.screenPosition;
        if (player.gravDir < 0)
            cursor.Y = Main.screenHeight - cursor.Y;
        return cursor;
    }

    internal static void SetAim(Player player)
    {
        if (!Enabled || player != Main.LocalPlayer)
            return;
        Vector2 cursor = Cursor(player);
        Main.mouseX = (int)cursor.X;
        Main.mouseY = (int)cursor.Y;
        // CopyInto already used MouseLeft/MouseRight to set controlUseItem/
        // controlUseTile, respecting gameplay/UI gates and AltFunctionUse.
    }

    public static void Reset()
    {
        button = lastButton = hotkey = lastHotkey = null;
        PressEdges = HotkeyEdges = 0;
        aim = Vector2.UnitX * 160;
        if (!applied)
            return;
        applied = false;
        var pack = PlayerInput.Triggers;
        pack.Current.Reset();
        pack.Old.Reset();
        pack.Update();
        Main.mouseLeft = Main.mouseRight = Main.mouseMiddle = Main.mouseXButton1 = Main.mouseXButton2 = false;
    }

    public override void OnWorldUnload() => Reset();
}

public sealed class ScriptAimPlayer : ModPlayer
{
    public override void SetControls() => ScriptInput.SetAim(Player);
}
