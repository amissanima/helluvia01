using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.UI;

namespace HelluviaTestHarness;

public sealed class TestLoadout
{
    private readonly Player player;
    private readonly int originalSlot;
    private readonly Item[] inventory, armor, dye, miscEquips, miscDyes;
    private bool restored;

    public TestLoadout(Player player)
    {
        if (!Entry.InTestWorld || player != Main.LocalPlayer || !player.active || player.dead)
            throw new InvalidOperationException("A loadout requires the verified local test player.");
        this.player = player;
        originalSlot = player.selectedItem;
        // Preserve the original item instances, including custom item data and prefixes.
        inventory = player.inventory;
        armor = player.armor;
        dye = player.dye;
        miscEquips = player.miscEquips;
        miscDyes = player.miscDyes;
        player.inventory = Empty(inventory.Length);
        player.armor = Empty(armor.Length);
        player.dye = Empty(dye.Length);
        player.miscEquips = Empty(miscEquips.Length);
        player.miscDyes = Empty(miscDyes.Length);
    }

    private static Item[] Empty(int length) =>
        Enumerable.Range(0, length).Select(_ => new Item()).ToArray();

    public static int ResolveItem(string fullName)
    {
        if (fullName.StartsWith("Terraria/", StringComparison.Ordinal))
            return ItemID.Search.GetId(fullName["Terraria/".Length..]);
        if (ModContent.TryFind(fullName, out ModItem item))
            return item.Type;
        throw new ArgumentException($"Unknown item '{fullName}'.");
    }

    public static int ResolveProjectile(string fullName)
    {
        if (fullName.StartsWith("Terraria/", StringComparison.Ordinal))
            return ProjectileID.Search.GetId(fullName["Terraria/".Length..]);
        if (ModContent.TryFind(fullName, out ModProjectile projectile))
            return projectile.Type;
        throw new ArgumentException($"Unknown projectile '{fullName}'.");
    }

    private void RequireActive()
    {
        if (restored || !Entry.InTestWorld || player != Main.LocalPlayer || player.dead)
            throw new InvalidOperationException("The test loadout is no longer active.");
    }

    private static bool Accepts(int slot, Item item) => slot switch
    {
        >= 0 and < 50 => true,
        >= 50 and < 54 => item.IsACoin,
        >= 54 and < 58 => item.FitsAmmoSlot(),
        _ => false
    };

    public void Give(string fullName, int slot, int quantity)
    {
        RequireActive();
        int type = ResolveItem(fullName);
        var prototype = new Item(type);
        if (prototype.IsAir || quantity < 1 || !Accepts(slot, prototype))
            throw new ArgumentException($"Cannot give {quantity} {fullName} in inventory slot {slot}.");
        if (!player.inventory[slot].IsAir)
            throw new InvalidOperationException($"Requested inventory slot {slot} is occupied.");

        // Build every stack before publishing any. Prefer slots in the same compartment.
        var candidates = Enumerable.Range(0, 58)
            .Where(i => i != slot && player.inventory[i].IsAir && Accepts(i, prototype))
            .OrderBy(i => (i < 50) == (slot < 50) ? 0 : 1)
            .Prepend(slot);
        var stacks = new List<(int Slot, Item Item)>();
        int remaining = quantity;
        foreach (int index in candidates) {
            var item = stacks.Count == 0 ? prototype : new Item(type);
            item.stack = Math.Min(remaining, item.maxStack);
            stacks.Add((index, item));
            remaining -= item.stack;
            if (remaining == 0)
                break;
        }
        if (remaining != 0)
            throw new InvalidOperationException($"Not enough empty inventory slots for {quantity} {fullName}.");
        foreach (var stack in stacks)
            player.inventory[stack.Slot] = stack.Item;
        if (stacks.Any(stack => player.inventory[stack.Slot].type != type) ||
            stacks.Sum(stack => player.inventory[stack.Slot].stack) != quantity)
            throw new InvalidOperationException($"Inventory verification failed for {fullName}.");
    }

    public void Select(int slot)
    {
        RequireActive();
        if (slot < 0 || slot > 9 || player.inventory[slot].IsAir)
            throw new ArgumentException($"Hotbar slot {slot} is empty or invalid.");
        player.selectedItem = slot;
        if (!ReferenceEquals(player.HeldItem, player.inventory[slot]))
            throw new InvalidOperationException($"The engine did not select hotbar slot {slot}.");
    }

    public void Equip(string fullName, int slot)
    {
        RequireActive();
        if (slot < 3 || slot > 9 || !player.IsItemSlotUnlockedAndUsable(slot) || !player.armor[slot].IsAir)
            throw new ArgumentException($"Functional accessory slot {slot} is invalid, locked or occupied.");
        int type = ResolveItem(fullName);
        int sourceSlot = Array.FindIndex(player.inventory, item => !item.IsAir && item.type == type);
        Item source = sourceSlot < 0 ? new Item(type) : player.inventory[sourceSlot];
        if (!source.accessory || source.expertOnly && !Main.expertMode || source.masterOnly && !Main.masterMode ||
            ItemSlot.AccCheck(player.armor, source, slot) || !ItemLoader.CanEquipAccessory(player, source, slot, false) ||
            player.armor.Skip(3).Take(7).Any(other => !other.IsAir && !ItemLoader.CanAccessoryBeEquippedWith(player, other, source)))
            throw new InvalidOperationException($"The game cannot equip {fullName} in slot {slot}.");
        Item equipped = source.stack == 1 ? source : source.Clone();
        equipped.stack = 1;
        if (sourceSlot >= 0) {
            if (ReferenceEquals(equipped, source))
                player.inventory[sourceSlot] = new Item();
            else
                source.stack--;
        }
        player.armor[slot] = equipped;
        if (player.armor[slot].type != type || !player.IsItemSlotUnlockedAndUsable(slot))
            throw new InvalidOperationException($"Accessory verification failed in slot {slot}.");
    }

    public void Restore()
    {
        if (restored)
            return;
        player.controlUseItem = false;
        player.controlUseTile = false;
        player.inventory = inventory;
        player.armor = armor;
        player.dye = dye;
        player.miscEquips = miscEquips;
        player.miscDyes = miscDyes;
        player.selectedItem = originalSlot;
        restored = true;
        Entry.Emit("restore", new { selectedSlot = player.selectedItem, inventorySlots = player.inventory.Length });
    }
}
