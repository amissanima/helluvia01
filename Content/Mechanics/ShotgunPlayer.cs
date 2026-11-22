using Microsoft.Xna.Framework;
using System.Collections.Generic;
using System;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria;
using helluvia01.Content.Items;
using helluvia01.Content.Particles;
using helluvia01.Content.Projectiles;

namespace helluvia01.Content.Mechanics
{
    public sealed class PumpPlayer : ModPlayer
    {
        internal bool Waiting { get; set; }
        internal ulong RequestNumber;

        public override void OnEnterWorld()
        {
            Waiting = false;
            RequestNumber = 0;
            if (Main.netMode == NetmodeID.MultiplayerClient)
                ShotPackets.Query();
        }

        public override void PlayerDisconnect() => RequestNumber = 0;

        internal bool AcceptSequence(ulong sequence)
        {
            if (sequence <= RequestNumber)
                return false;

            RequestNumber = sequence;
            return true;
        }
    }

    // Only the authority writes deadlines. An expired offline entry represents one undelivered
    // ready sound and is consumed when this character next updates alive in the same world.
    public sealed class ShotSession : ModSystem
    {
        private static readonly Dictionary<string, ulong> deadlines = new(StringComparer.OrdinalIgnoreCase);
        public static ulong Tick { get; private set; }

        public override void OnWorldLoad() => Reset();
        public override void OnWorldUnload() => Reset();

        private static void Reset()
        {
            Tick = 0;
            deadlines.Clear();
        }

        public override void PreUpdatePlayers()
        {
            Tick++; // This hook runs only when world simulation advances (including while dead).
            if (Main.netMode == NetmodeID.MultiplayerClient)
                return;

            foreach (Player player in Main.ActivePlayers)
                DeliverReady(player);
        }

        internal static int Remaining(Player player)
        {
            return deadlines.TryGetValue(player.name, out ulong until) && until > Tick
                ? (int)(until - Tick)
                : 0;
        }

        private static void DeliverReady(Player player)
        {
            if (player.dead || !deadlines.TryGetValue(player.name, out ulong until) || Tick < until)
                return;

            deadlines.Remove(player.name);
            if (Main.netMode == NetmodeID.Server)
                ShotPackets.SendReady(player.whoAmI);
            else
                ShotEffects.OnReady(player);
        }

        internal static bool TryNormalize(Vector2 aim, out Vector2 direction)
        {
            float squared = aim.LengthSquared();
            direction = default;
            if (!float.IsFinite(squared) || squared < 0.000001f)
                return false;

            direction = aim / MathF.Sqrt(squared);
            return true;
        }

        internal static bool TryFire(Player player, Vector2 direction, int damage, float knockback)
        {
            if (Main.netMode == NetmodeID.MultiplayerClient || !player.active || player.dead || player.ghost ||
                player.CCed || player.noItems || player.cursed || player.HeldItem.ModItem is not EmberShotgun ||
                Remaining(player) > 0)
                return false;

            Item item = player.HeldItem;
            if (Main.netMode == NetmodeID.Server)
            {
                if (!player.controlUseItem || player.reuseDelay > 0 ||
                    (player.mount.Active && player.mount.Type == MountID.Drill) ||
                    !CombinedHooks.CanUseItem(player, item) || !CombinedHooks.CanShoot(player, item))
                    return false;

                damage = player.GetWeaponDamage(item);
                knockback = player.GetWeaponKnockback(item);
            }

            // Reserve enough free slots for the entire volley.
            int free = 0;
            for (int i = 0; i < Main.maxProjectiles && free < EmberShotgun.PelletCount; i++)
            {
                if (!Main.projectile[i].active)
                    free++;
            }
            if (free < EmberShotgun.PelletCount)
                return false;

            Vector2 origin = player.RotatedRelativePoint(player.MountedCenter);
            Vector2 muzzle = EmberShotgun.WorldPoint(player, direction, EmberShotgun.MuzzleFromGrip);
            Vector2 spawn = Collision.CanHitLine(origin, 1, 1, muzzle, 1, 1) ? muzzle : origin;
            int pelletType = ModContent.ProjectileType<EnergyPellet>();
            int owner = Main.myPlayer; // Server sentinel on dedicated/listen servers, real owner in SP.
            var source = player.GetSource_ItemUse_WithPotentialAmmo(item, 0);

            DeliverReady(player);
            for (int i = 0; i < EmberShotgun.PelletCount; i++)
            {
                float angle = MathHelper.ToRadians(EmberShotgun.SpreadDegrees) * (Main.rand.NextFloat() - 0.5f);
                Vector2 velocity = direction.RotatedBy(angle) * EmberShotgun.PelletStepSpeed;
                Projectile.NewProjectile(source, spawn, velocity, pelletType, damage, knockback, owner);
            }

            deadlines[player.name] = Tick + EmberShotgun.CooldownTicks;
            if (Main.netMode == NetmodeID.Server)
                ShotPackets.BroadcastShot(player.whoAmI, muzzle, direction);
            else
                ShotEffects.OnShot(player, muzzle, direction);
            return true;
        }

    }
}
