using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace HelluviaTestHarness;

// A step is evaluated after player update. Input is consumed by the following normal game update.
public sealed class ScriptScenario : ModSystem
{
    private TestLoadout loadout;
    private WorldFixture fixture;
    private JsonArray steps;
    private int index, age, elapsed, assertions, attacks;
    private int inputStartTick, previousEdges;
    private bool finished;
    private bool inputStarted;
    private readonly Dictionary<string, double> samples = new();
    private readonly HashSet<string> captures = new();
    private readonly Dictionary<int, int> spawned = new(), killed = new();
    private readonly Dictionary<int, (int Type, int Item)> ownedShots = new();
    private Func<Player, int> shotgunRemaining;
    private Player Player => Main.LocalPlayer;
    internal static ScriptScenario Active
    {
        get
        {
            var scenario = ModContent.GetInstance<ScriptScenario>();
            return Entry.InTestWorld && scenario.loadout != null && !scenario.finished ? scenario : null;
        }
    }

    public override void PostUpdatePlayers()
    {
        if (!Entry.InTestWorld || finished || Entry.Scenario["steps"] is not JsonArray script)
            return;
        try
        {
            if (steps == null)
            {
                steps = script;
                Prepare();
                return;
            }
            if (++elapsed > (int)Entry.Scenario["timeoutTicks"])
                throw new TimeoutException($"Scenario exceeded its tick limit at step {index}.");
            // Immediate assertions inspect the same state as the event they follow.
            while (index < steps.Count)
            {
                var step = steps[index].AsObject();
                if (age == 0)
                    Entry.Emit("step", new { index, instruction = step });
                bool done = Advance(step);
                if (!done)
                {
                    age++;
                    return;
                }
                index++;
                age = 0;
                inputStarted = false;
            }
            if (assertions == 0)
                throw new InvalidDataException("A script needs at least one measured assertion.");
            End("passed", $"{assertions} measured assertions passed.");
        }
        catch (Exception e)
        {
            End("failed", $"Step {index}: {e.Message}");
        }
    }

    private void Prepare()
    {
        var spec = Entry.Scenario;
        if (Player.dead || steps.Count == 0 || (int)spec["timeoutTicks"] < 1)
            throw new InvalidDataException("A living player, steps and a positive timeout are required.");
        loadout = new TestLoadout(Player);
        fixture = new WorldFixture(Player);
        fixture.Prepare(spec["initial"]?.AsObject() ?? new JsonObject());
        foreach (var item in spec["items"]?.AsArray() ?? new JsonArray())
            loadout.Give((string)item["item"], (int)item["slot"], (int?)item["quantity"] ?? 1);
        foreach (var item in spec["accessories"]?.AsArray() ?? new JsonArray())
            loadout.Equip((string)item["item"], (int)item["slot"]);
        if (spec["selectedSlot"] != null)
            loadout.Select((int)spec["selectedSlot"]);
        Entry.Emit("inventory", new { slot = Player.selectedItem, selectedType = Player.HeldItem.type,
            items = spec["items"], accessories = spec["accessories"] });
    }

    private bool Advance(JsonObject step)
    {
        string op = (string)step["op"];
        switch (op)
        {
            case "give":
                loadout.Give((string)step["item"], (int)step["slot"], (int?)step["quantity"] ?? 1);
                return true;
            case "equip":
                loadout.Equip((string)step["item"], (int)step["slot"]);
                Entry.Emit("equip", new { slot = (int)step["slot"], type = Player.armor[(int)step["slot"]].type });
                return true;
            case "select":
                loadout.Select((int)step["slot"]);
                return true;
            case "press":
                int ticks = Positive(step, "ticks");
                if (!inputStarted)
                {
                    if (ScriptInput.AwaitingReleaseSample)
                        return false;
                    inputStartTick = elapsed;
                    previousEdges = ScriptInput.PressEdges;
                    ScriptInput.Press((string)step["button"], Aim(step));
                    inputStarted = true;
                }
                if (elapsed - inputStartTick < ticks)
                    return false;
                ScriptInput.Release();
                if (ScriptInput.PressEdges != previousEdges + 1)
                    throw new InvalidOperationException("The game did not receive exactly one button press.");
                Entry.Emit("release", new { index, heldTicks = ticks, pressEdges = ScriptInput.PressEdges });
                return true;
            case "hotkey":
                if (!inputStarted)
                {
                    if (ScriptInput.AwaitingHotkeyRelease)
                        return false;
                    previousEdges = ScriptInput.HotkeyEdges;
                    ScriptInput.Hotkey((string)step["name"]);
                    inputStarted = true;
                    return false;
                }
                if (ScriptInput.HotkeyEdges != previousEdges + 1)
                    throw new InvalidOperationException("The game did not receive the requested hotkey.");
                return true;
            case "wait":
                return age >= Positive(step, "ticks");
            case "sample":
                samples.Add((string)step["name"], Metric((string)step["metric"]));
                return true;
            case "assert":
            case "await":
                string name = (string)step["metric"];
                double actual = Metric(name);
                if (step["relativeTo"] != null)
                    actual -= samples[(string)step["relativeTo"]];
                if (!Matches(step, actual))
                {
                    if (op == "await" && age < Positive(step, "timeout"))
                        return false;
                    Entry.Emit("assertion", new { index, name, actual, expected = step, passed = false });
                    throw new InvalidOperationException($"{name} was {actual} at step {index}.");
                }
                assertions++;
                Entry.Emit("assertion", new { index, name, actual, expected = step, passed = true });
                return true;
            case "capture":
                string picture = (string)step["name"];
                if (age == 0)
                {
                    if (!captures.Add(picture))
                        throw new InvalidDataException($"Duplicate capture: {picture}");
                    Entry.Emit("capture", new { name = picture, index });
                }
                // Capturing must not pause a simulation (e.g. a 90-tick cooldown).
                return true;
            case "kill":
                fixture.KillPlayer();
                return true;
            case "respawn":
                fixture.Respawn();
                return true;
            default:
                throw new InvalidDataException($"Unknown scenario operation '{op}'.");
        }
    }

    private static int Positive(JsonObject step, string name) =>
        (int)step[name] > 0 ? (int)step[name] : throw new InvalidDataException($"Expected positive {name}.");

    private static Vector2 Aim(JsonObject step)
    {
        var point = step["aimOffset"].AsArray();
        var aim = new Vector2((float)point[0], (float)point[1]);
        if (point.Count != 2 || !float.IsFinite(aim.LengthSquared()) || aim.LengthSquared() < 1)
            throw new InvalidDataException("aimOffset needs two finite values and a nonzero direction.");
        return aim;
    }

    private static bool Matches(JsonObject step, double actual)
    {
        double? eq = (double?)step["eq"], min = (double?)step["min"], max = (double?)step["max"];
        if (eq == null && min == null && max == null)
            throw new InvalidDataException("Specify eq, min or max.");
        return double.IsFinite(actual) && (eq == null || actual == eq) &&
            (min == null || actual >= min) && (max == null || actual <= max);
    }

    private double Metric(string name)
    {
        switch (name)
        {
            case "attacks": return attacks;
            case "animation": return Player.itemAnimation;
            case "selectedSlot": return Player.selectedItem;
            case "helluvia01/EmberShotgun.cooldown":
                // Query the mod's actual read-only state. A changed mod contract fails the scenario.
                if (shotgunRemaining == null)
                {
                    var mod = ModLoader.GetMod("helluvia01");
                    var type = mod.Code.GetType("helluvia01.Content.Mechanics.ShotSession", true);
                    var method = type.GetMethod("Remaining", BindingFlags.Static | BindingFlags.NonPublic |
                        BindingFlags.Public, null, new[] { typeof(Player) }, null);
                    shotgunRemaining = (method ?? throw new MissingMethodException(type.FullName, "Remaining"))
                        .CreateDelegate<Func<Player, int>>();
                }
                return shotgunRemaining(Player);
        }
        if (name.StartsWith("spawned/") || name.StartsWith("killed/"))
        {
            int slash = name.IndexOf('/');
            int type = TestLoadout.ResolveProjectile(name.Substring(slash + 1));
            return (name.StartsWith("spawned/") ? spawned : killed).GetValueOrDefault(type);
        }
        if (name.StartsWith("stack/"))
        {
            int slot = int.Parse(name.Substring(6));
            return Player.inventory[slot].stack;
        }
        if (name.StartsWith("buff/"))
        {
            string buff = name.Substring(5);
            int type = buff.StartsWith("Terraria/") ? BuffID.Search.GetId(buff.Substring(9))
                : ModContent.Find<ModBuff>(buff).Type;
            return Player.HasBuff(type) ? 1 : 0;
        }
        return fixture.GetMetric(name);
    }

    internal void Attack(Item item, Player who)
    {
        if (who != Player || who.HeldItem != item)
            return;
        attacks++;
        Entry.Emit("attack", new { uses = attacks, type = item.type, slot = who.selectedItem });
    }

    internal void Spawn(Projectile shot, IEntitySource source)
    {
        if (shot.owner != Player.whoAmI)
            return;
        int originItem;
        if (source is IEntitySource_WithStatsFromItem from && from.Player == Player && from.Item == Player.HeldItem)
            originItem = from.Item.type;
        else if (source is EntitySource_Parent { Entity: Projectile parent } &&
            ownedShots.TryGetValue(parent.whoAmI, out var tracked) && tracked.Type == parent.type)
            originItem = tracked.Item;
        else
            return;
        ownedShots[shot.whoAmI] = (shot.type, originItem);
        spawned[shot.type] = spawned.GetValueOrDefault(shot.type) + 1;
        Entry.Emit("projectile", new { shot.type, shot.identity, shot.owner, item = originItem, mode = shot.ai[0],
            x = shot.Center.X, y = shot.Center.Y, vx = shot.velocity.X, vy = shot.velocity.Y });
    }

    internal void Killed(Projectile shot)
    {
        if (ownedShots.Remove(shot.whoAmI, out var tracked))
        {
            int type = tracked.Type;
            killed[type] = killed.GetValueOrDefault(type) + 1;
            Entry.Emit("projectileKilled", new { shot.identity, type });
        }
    }

    private void End(string status, string message)
    {
        if (status == "passed")
            foreach (string name in captures)
                if (!File.Exists(Path.Combine(Entry.RunDirectory, $"capture-{name}.ack")))
                    return;
        finished = true;
        try
        {
            Cleanup();
            Entry.Emit("mechanic", new { passed = status == "passed", assertions, attacks, message });
        }
        catch (Exception e)
        {
            status = "failed";
            message += $" Cleanup failed: {e.Message}";
        }
        Entry.Finish(status, message);
    }

    private void Cleanup()
    {
        ScriptInput.Reset();
        try { fixture?.Cleanup(); }
        finally { loadout?.Restore(); }
        loadout = null;
    }

    public override void OnWorldUnload()
    {
        if (loadout != null)
            Cleanup();
    }
}

public class ScriptItems : GlobalItem
{
    public override void UseAnimation(Item item, Player player) => ScriptScenario.Active?.Attack(item, player);
}

public class ScriptShots : GlobalProjectile
{
    public override void OnSpawn(Projectile projectile, IEntitySource source) => ScriptScenario.Active?.Spawn(projectile, source);
    public override void OnKill(Projectile projectile, int timeLeft) => ScriptScenario.Active?.Killed(projectile);
}
