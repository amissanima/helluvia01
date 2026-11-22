using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria;
using helluvia01.Content.Items;
using helluvia01.Content.Mechanics;
using helluvia01.Content.Particles;

namespace helluvia01.Content.Projectiles
{
    public sealed class EnergyPellet : ModProjectile
    {
        private const int UpdatesPerTick = 4;
        private const int LifetimeTicks = 44;
        private const int TrailSamples = 16;
        private const int HitboxPixels = 8;

        // Only a locally spawned, server-authorized shot has an item source on the server.
        private Player shooter;
        private string shooterName;

        public override void OnSpawn(IEntitySource source)
        {
            if (Main.netMode == NetmodeID.Server && source is IEntitySource_WithStatsFromItem itemUse &&
                itemUse.Item.ModItem is EmberShotgun)
            {
                shooter = itemUse.Player;
                shooterName = shooter.name;
            }
        }

        private bool ShooterPresent => shooter != null && shooter.active &&
            ReferenceEquals(Main.player[shooter.whoAmI], shooter) && shooter.name == shooterName;

        public override string Texture => "helluvia01/Content/Projectiles/EnergyPellet";

        public override void SetStaticDefaults()
        {
            ProjectileID.Sets.TrailCacheLength[Type] = TrailSamples;
            ProjectileID.Sets.TrailingMode[Type] = 0;
        }

        public override void SetDefaults()
        {
            Projectile.width = Projectile.height = HitboxPixels;
            Projectile.DamageType = DamageClass.Ranged;
            Projectile.friendly = true;
            // Single-penetration vanilla projectiles bypass shared owner immunity.
            Projectile.penetrate = 1;
            Projectile.tileCollide = true;
            Projectile.ignoreWater = true;
            Projectile.extraUpdates = UpdatesPerTick - 1;
            Projectile.timeLeft = LifetimeTicks * UpdatesPerTick;
        }

        public override bool? CanDamage() => Main.netMode switch
        {
            NetmodeID.MultiplayerClient => false,
            NetmodeID.Server when Projectile.owner != Main.maxPlayers || shooter == null => false,
            _ => null
        };

        public override bool CanHitPvp(Player target) => false;

        public override bool? CanHitNPC(NPC target)
        {
            if (Main.netMode == NetmodeID.Server && ShooterPresent &&
                !shooter.CanNPCBeHitByPlayerOrPlayerProjectile(target, Projectile))
                return false;

            return null;
        }

        public override void ModifyHitNPC(NPC target, ref NPC.HitModifiers modifiers)
        {
            // Mark the actual player before a potentially lethal server-owned strike.
            // The engine already uses the item source for the pellet's crit and penetration.
            if (Main.netMode == NetmodeID.Server && ShooterPresent)
                target.PlayerInteraction(shooter.whoAmI);
        }

        public override void OnHitNPC(NPC target, NPC.HitInfo hit, int damageDone)
        {
            Fracture.Apply(target);
        }

        public override bool PreDraw(ref Color lightColor)
        {
            var visuals = ModContent.GetInstance<ShotVisualSystem>();
            Texture2D glowHead = ModContent.Request<Texture2D>("helluvia01/Content/Particles/GlowHead").Value;
            Texture2D glowTail = ModContent.Request<Texture2D>("helluvia01/Content/Particles/GlowTail").Value;
            Texture2D glowTailLarge = ModContent.Request<Texture2D>("helluvia01/Content/Particles/GlowTailLarge").Value;

            Texture2D core = visuals.Core.Value;
            Vector2 position = Projectile.Center - Main.screenPosition;
            // float rotation = Projectile.velocity.ToRotation();
            
            Vector2 direction = Projectile.velocity.SafeNormalize(Vector2.UnitX);
            float rotation = direction.ToRotation();
            Vector2 drawPosition = Projectile.Center - Main.screenPosition;
            Vector2 origin = new Vector2(171f, 64f);

            float glowScale = 0.32f;
            Color glowTint = new Color(255, 220, 140);

            Main.EntitySpriteDraw(glowTailLarge, drawPosition, null, glowTint * 0.05f,
                rotation, origin, glowScale - 0.08f, SpriteEffects.None);
            Main.EntitySpriteDraw(glowTail, drawPosition, null, glowTint * 0.25f,
                rotation, origin, glowScale, SpriteEffects.None);
            Main.EntitySpriteDraw(glowHead, drawPosition, null, glowTint * 0.50f,
                rotation, origin, glowScale, SpriteEffects.None);

            Main.EntitySpriteDraw(
                core,
                position ,
                null,
                Color.White,
                rotation,
                core.Size() * 0.2f,
                1f,
                SpriteEffects.None);

                

            return false;
        }

        public override void OnKill(int timeLeft)
        {
            if (!Main.dedServ && timeLeft > 0)
                ModContent.GetInstance<ShotVisualSystem>().EmitImpact(Projectile);
        }
    }

}
