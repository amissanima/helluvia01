using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.IO;
using Terraria.ModLoader;

namespace HelluviaTestHarness;

public sealed class HelluviaTestHarnessMod : Mod { }

public sealed class Entry : ModSystem
{
    private const string PlayerName = "ModTester";
    private const string WorldName = "ModTestWorld";
    private static string token;
    private static string saveRoot;
    private static string playerPath;
    private static string worldPath;
    private static readonly object outputLock = new();
    private static volatile bool ready;
    private static bool entering;
    private static bool worldVerified;
    private static volatile bool finished;
    private static bool exitRequested;
    private int lastMenuMode = int.MinValue;

    public static string RunDirectory { get; private set; }
    public static JsonObject Scenario { get; private set; }
    private static bool sceneReady;
    public static bool InTestWorld => worldVerified && sceneReady && !finished && MatchesActiveWorld();

    internal static void ObserveWorldFrame()
    {
        if (worldVerified && !sceneReady && Main.GameUpdateCount >= 15 && Main.BlackFadeIn <= 0)
        {
            sceneReady = true;
            Emit("sceneReady", new { fade = Main.BlackFadeIn });
        }
    }

    public override void PostSetupContent()
    {
        string run = Environment.GetEnvironmentVariable("HELLUVIA_TEST_RUN");
        token = Environment.GetEnvironmentVariable("HELLUVIA_TEST_TOKEN");
        if (Main.dedServ || string.IsNullOrWhiteSpace(run) || string.IsNullOrWhiteSpace(token))
            return;

        if (!Path.IsPathFullyQualified(run) || !Directory.Exists(run))
            throw new InvalidDataException("HELLUVIA_TEST_RUN must be an existing absolute directory.");
        RunDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(run));
        try
        {
            string save = Environment.GetEnvironmentVariable("HELLUVIA_TEST_SAVE");
            if (string.IsNullOrWhiteSpace(save) || !Path.IsPathFullyQualified(save))
                throw new InvalidDataException("Missing absolute HELLUVIA_TEST_SAVE.");
            saveRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(save));
            string relative = Path.GetRelativePath(RunDirectory, saveRoot);
            if (relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)
                || Path.IsPathRooted(relative) || !SamePath(Main.SavePath, saveRoot))
                throw new InvalidDataException($"Unexpected game save directory: {Main.SavePath}.");
            playerPath = Path.Combine(saveRoot, "Players", PlayerName + ".plr");
            worldPath = Path.Combine(saveRoot, "Worlds", WorldName + ".wld");
            if (!SamePath(Main.PlayerPath, Path.GetDirectoryName(playerPath))
                || !SamePath(Main.WorldPath, Path.GetDirectoryName(worldPath)))
                throw new InvalidDataException("The game resolved a different player or world save directory.");
            Scenario = JsonNode.Parse(File.ReadAllText(Path.Combine(RunDirectory, "scenario.json")))?.AsObject()
                ?? throw new InvalidDataException("Empty scenario.");
            Emit("loaded", new { version = Mod.Version.ToString(), gameSaveDirectory = Main.SavePath });
            ready = true;
        }
        catch (Exception ex)
        {
            Finish("failed", ex.ToString());
        }
    }

    public override void PostUpdateInput()
    {
        if (exitRequested)
            return;
        if (finished)
        {
            string request = Path.Combine(RunDirectory, "exit.request");
            try
            {
                if (!File.Exists(request) || File.ReadAllText(request).Trim() != token)
                    return;
            }
            catch (IOException) { return; } // The runner may still be publishing the request.
            exitRequested = true;
            Main.instance.Exit();
            return;
        }
        if (!ready)
            return;
        if (!entering && Main.gameMenu && Main.menuMode != lastMenuMode)
        {
            lastMenuMode = Main.menuMode;
            Emit("menu", new { mode = Main.menuMode, screen = Main.MenuUI?.CurrentState?.GetType().Name });
        }
        if (entering || !Main.gameMenu || Main.menuMode != 0)
            return;
        entering = true;
        try
        {
            if (Main.netMode != NetmodeID.SinglePlayer || Main.menuMultiplayer || Main.menuServer)
                throw new InvalidOperationException("Tests require a single-player client.");
            var player = Player.GetFileData(playerPath, false);
            var world = WorldFile.GetAllMetadata(worldPath, false);
            if (player?.Player == null || player.Player.loadStatus != 0 || player.Player.name != PlayerName
                || player.IsCloudSave || !SamePath(player.Path, playerPath))
                throw new InvalidDataException($"Invalid test player: {playerPath}.");
            if (world == null || !world.IsValid || world.Name != WorldName
                || world.IsCloudSave || !SamePath(world.Path, worldPath))
                throw new InvalidDataException($"Invalid test world: {worldPath}.");
            if ((player.Player.difficulty == PlayerDifficultyID.Creative) != (world.GameMode == GameModeID.Creative)
                || !SystemLoader.CanWorldBePlayed(player, world, out _))
                throw new InvalidDataException("The game rejected this player/world combination.");

            // Follow the single-player selection path using only the two verified local save files.
            Main.ServerSideCharacter = false;
            Main.myPlayer = 0;
            player.SetAsActive();
            Main.LocalPlayer.position = Vector2.Zero;
            world.SetAsActive();
            Main.clrInput();
            Main.menuMode = 10;
            Emit("entering", new { player = playerPath, world = worldPath });
            WorldGen.playWorld();
        }
        catch (Exception ex)
        {
            Finish("failed", ex.ToString());
        }
    }

    public override void PostUpdateEverything()
    {
        if (!entering || finished || Main.gameMenu || !Main.LocalPlayer.active)
            return;
        if (!MatchesActiveWorld())
        {
            Finish("failed", "The active world or player differs from the isolated fixture.");
            return;
        }
        if (!worldVerified)
        {
            Emit("world", new { player = Main.LocalPlayer.name, world = Main.worldName, playerPath, worldPath });
            worldVerified = true;
        }
    }

    private static bool MatchesActiveWorld() =>
        !Main.dedServ && !Main.gameMenu && Main.netMode == NetmodeID.SinglePlayer
        && Main.LocalPlayer.active && Main.LocalPlayer.name == PlayerName && Main.worldName == WorldName
        && Main.ActivePlayerFileData is { IsCloudSave: false } player && SamePath(player.Path, playerPath)
        && Main.ActiveWorldFileData is { IsCloudSave: false } world && SamePath(world.Path, worldPath);

    private static bool SamePath(string left, string right) =>
        left != null && right != null && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    public static void Emit(string kind, object data)
    {
        if (kind == "capture")
        {
            var capture = (JsonObject)JsonSerializer.SerializeToNode(data);
            FrameCapture.Request(capture["name"].GetValue<string>());
        }
        string line = JsonSerializer.Serialize(new { kind, data, tick = Main.GameUpdateCount, token, pid = Environment.ProcessId });
        lock (outputLock)
            File.AppendAllText(Path.Combine(RunDirectory, "events.jsonl"), line + "\n");
    }

    public static void Finish(string status, string message)
    {
        lock (outputLock)
        {
            if (finished)
                return;
            string result = Path.Combine(RunDirectory, "result.json");
            string temp = result + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new { status, message, token, pid = Environment.ProcessId }));
            File.Move(temp, result);
            finished = true;
        }
    }
}
