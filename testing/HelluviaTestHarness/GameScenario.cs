using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace HelluviaTestHarness;

// All state transitions and all changes to the player happen on the game thread.
public sealed class GameScenario : ModSystem
{
    private enum Phase { Idle, Ready, Pressing, Released, AwaitingPicture, Done }

    private Phase phase;
    private int elapsed, held, uses, weaponId, ammoId, weaponSlot, ammoSlot;
    private int holdTicks, timeout, expectedUses, ammoMin, ammoMax;
    private Vector2 aimOffset;
    private Player subject;
    private TestLoadout loadout;
    private readonly Dictionary<int, int> expected = new();
    private readonly Dictionary<int, int> observed = new();
    private bool sourceError, captureRequested, passed;

    internal static GameScenario Current => ModContent.GetInstance<GameScenario>();

    public override void PostUpdatePlayers()
    {
        if (!Entry.InTestWorld || Entry.Scenario.ContainsKey("steps") || phase == Phase.Done)
            return;
        try {
            if (phase == Phase.Idle) {
                Prepare(Main.LocalPlayer, Entry.Scenario);
                return;
            }
            if (++elapsed > timeout)
                throw new TimeoutException($"Timeout during {phase} after {elapsed} world ticks.");
            if (Main.LocalPlayer != subject || !subject.active || subject.dead)
                throw new InvalidOperationException("The test player left the scenario or died.");
            if (!captureRequested && uses > 0) {
                captureRequested = true;
                Entry.Emit("capture", new { name = "shot", uses, spawns = observed.Values.Sum() });
            }
            if (phase == Phase.Released && subject.itemAnimation == 0 && subject.itemTime == 0)
                CheckResult();
            if (phase == Phase.AwaitingPicture && File.Exists(Path.Combine(Entry.RunDirectory, "capture-shot.ack")))
                End(passed ? "passed" : "failed", passed ? "Attack and projectile checks passed." : "Attack expectations failed.");
        }
        catch (Exception e) {
            End("failed", e.ToString());
        }
    }

    private void Prepare(Player player, JsonObject s)
    {
        if (!player.active || player.dead || Main.netMode != NetmodeID.SinglePlayer)
            throw new InvalidOperationException("This scenario needs a living, local single-player character.");
        var weapon = s["weapon"];
        var ammo = s["ammo"];
        var action = s["action"];
        var expect = s["expect"];
        weaponId = TestLoadout.ResolveItem((string)weapon["item"]);
        ammoId = TestLoadout.ResolveItem((string)ammo["item"]);
        weaponSlot = (int)weapon["slot"];
        ammoSlot = (int)ammo["slot"];
        int quantity = (int)ammo["quantity"];
        holdTicks = (int)action["holdTicks"];
        aimOffset = new Vector2((float)action["aimOffset"][0], (float)action["aimOffset"][1]);
        timeout = (int)s["timeoutTicks"];
        expectedUses = (int)expect["uses"];
        ammoMin = (int)expect["ammoRemaining"][0];
        ammoMax = (int)expect["ammoRemaining"][1];
        foreach (var pair in expect["projectiles"].AsObject())
            expected.Add(TestLoadout.ResolveProjectile(pair.Key), (int)pair.Value);
        if (weaponSlot < 0 || weaponSlot > 9 || ammoSlot < 54 || ammoSlot > 57 ||
            holdTicks < 1 || timeout <= holdTicks || expectedUses < 1 ||
            aimOffset.LengthSquared() < 1 || !float.IsFinite(aimOffset.LengthSquared()) ||
            expected.Count == 0 || expected.Values.Any(count => count < 1) ||
            ammoMin < 0 || ammoMax < ammoMin || ammoMax > quantity)
            throw new ArgumentException("Invalid slot, action, timeout or expected range in scenario.");

        subject = player;
        loadout = new TestLoadout(player);
        loadout.Give((string)weapon["item"], weaponSlot, 1);
        loadout.Give((string)ammo["item"], ammoSlot, quantity);
        loadout.Select(weaponSlot);
        if (player.HeldItem.type != weaponId || !player.HasAmmo(player.HeldItem))
            throw new InvalidOperationException("The selected weapon or usable ammunition was not found.");
        Entry.Emit("inventory", new {
            weapon = (string)weapon["item"], weaponId, weaponSlot, selectedSlot = player.selectedItem,
            ammo = (string)ammo["item"], ammoId, ammoSlot, stack = player.inventory[ammoSlot].stack
        });
        phase = Phase.Ready;
    }

    internal void Controls(Player player)
    {
        if (!Entry.InTestWorld || subject != player || phase is Phase.Idle or Phase.Done)
            return;
        player.controlUseItem = false;
        player.controlUseTile = false;
        if (phase == Phase.Ready && elapsed >= 2 && player.releaseUseItem &&
            player.itemAnimation == 0 && player.itemTime == 0) {
            phase = Phase.Pressing;
        }
        if (phase != Phase.Pressing)
            return;
        if (held == holdTicks) {
            phase = Phase.Released;
            Entry.Emit("release", new { heldTicks = held });
            return;
        }
        Vector2 target = player.MountedCenter + aimOffset;
        Vector2 cursor = target - Main.screenPosition;
        Main.mouseX = (int)cursor.X;
        Main.mouseY = (int)(player.gravDir < 0 ? Main.screenHeight - cursor.Y : cursor.Y);
        player.controlUseItem = true;
        if (held++ == 0)
            Entry.Emit("press", new { button = "primary", slot = player.selectedItem, targetX = target.X, targetY = target.Y });
    }

    internal void Used(Item item, Player player)
    {
        if (!Entry.InTestWorld || phase != Phase.Pressing || player != subject || item.type != weaponId ||
            player.selectedItem != weaponSlot || !player.controlUseItem)
            return;
        uses++;
        Entry.Emit("attack", new { uses, weaponId, slot = player.selectedItem, player = player.whoAmI });
    }

    internal void Spawned(Projectile shot, IEntitySource source)
    {
        if (!Entry.InTestWorld || phase is not (Phase.Pressing or Phase.Released) ||
            source is not IEntitySource_WithStatsFromItem use || use.Player != subject || use.Item.type != weaponId)
            return;
        int ammoUsed = source is EntitySource_ItemUse_WithAmmo withAmmo ? withAmmo.AmmoItemIdUsed : -1;
        sourceError |= shot.owner != subject.whoAmI || ammoUsed != ammoId;
        observed[shot.type] = observed.GetValueOrDefault(shot.type) + 1;
        Entry.Emit("projectile", new { type = shot.type, shot.identity, shot.owner, weaponId, ammoUsed,
            x = shot.Center.X, y = shot.Center.Y, vx = shot.velocity.X, vy = shot.velocity.Y });
    }

    private void CheckResult()
    {
        Item ammo = subject.inventory[ammoSlot];
        int remaining = ammo.IsAir ? 0 : ammo.stack;
        passed = uses == expectedUses && expected.Count == observed.Count &&
            expected.All(p => observed.GetValueOrDefault(p.Key) == p.Value) && !sourceError &&
            (ammo.IsAir || ammo.type == ammoId) && remaining >= ammoMin && remaining <= ammoMax &&
            subject.selectedItem == weaponSlot && subject.HeldItem.type == weaponId;
        Entry.Emit("mechanic", new { passed, uses, expectedUses, expected, observed, remaining, ammoMin, ammoMax, sourceError });
        if (!captureRequested) {
            End("failed", "The game never started the item's use animation.");
            return;
        }
        phase = Phase.AwaitingPicture;
    }

    private void End(string status, string message)
    {
        phase = Phase.Done;
        Restore();
        Entry.Finish(status, message);
    }

    private void Restore()
    {
        loadout?.Restore();
        loadout = null;
    }

    public override void OnWorldUnload() => Restore();
}

public sealed class ScenarioControls : ModPlayer
{
    public override void SetControls() => GameScenario.Current.Controls(Player);
}

public sealed class ScenarioItemObserver : GlobalItem
{
    public override void UseAnimation(Item item, Player player) => GameScenario.Current.Used(item, player);
}

public sealed class ScenarioProjectileObserver : GlobalProjectile
{
    public override void OnSpawn(Projectile projectile, IEntitySource source) =>
        GameScenario.Current.Spawned(projectile, source);
}
