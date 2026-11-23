using System;
using helluvia01.Content.Mechanics;
using helluvia01.Content.Particles;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;

namespace helluvia01.Content.Projectiles;

public sealed class SakuraCrescent : ModProjectile
{
    private const int Lifetime = 84;
    private const int TrailSamples = 7;
    private static Asset<Texture2D> stream;
    private bool bloomed;

    public override string Texture => "helluvia01/Content/Assets/Sakura/SlashMain";

    public override void Load()
    {
        if (!Main.dedServ)
            stream = ModContent.Request<Texture2D>("helluvia01/Content/Assets/Sakura/Ranged");
    }

    public override void Unload() => stream = null;

    public override void SetStaticDefaults()
    {
        ProjectileID.Sets.TrailCacheLength[Type] = TrailSamples;
        ProjectileID.Sets.TrailingMode[Type] = 0;
    }

    public override void SetDefaults()
    {
        Projectile.width = Projectile.height = 20;
        Projectile.friendly = true;
        Projectile.DamageType = DamageClass.Melee;
        Projectile.penetrate = 1;
        Projectile.ignoreWater = true;
        Projectile.tileCollide = true;
        Projectile.timeLeft = Lifetime;
    }

    public override void AI()
    {
        Projectile.rotation = Projectile.velocity.ToRotation();
        if (Main.dedServ)
            return;

        Lighting.AddLight(Projectile.Center, .57f, .17f, .37f);
        SakuraParticles.Flight(Projectile.Center, Projectile.velocity);
    }

    public override bool? Colliding(Rectangle projHitbox, Rectangle targetHitbox)
    {
        // Only the cutting edge of the crescent strikes. The ribbon behind it is decorative.
        Vector2 direction = Projectile.velocity.SafeNormalize(Vector2.UnitX);
        Vector2 tangent = new(-direction.Y, direction.X);
        Vector2 nose = Projectile.Center + 6f * direction;
        Vector2 blade = Projectile.Center - 13f * direction;
        Vector2 box = targetHitbox.TopLeft();
        Vector2 size = targetHitbox.Size();
        float contact = 0f;
        return Collision.CheckAABBvLineCollision(box, size, blade - tangent * 22f, nose, 12f, ref contact)
            || Collision.CheckAABBvLineCollision(box, size, nose, blade + tangent * 22f, 12f, ref contact);
    }

    public override void OnHitNPC(NPC target, NPC.HitInfo hit, int damageDone)
    {
        SakuraHits.OnHit(Projectile, target, hit);
        Vector2 contact = Vector2.Clamp(Projectile.Center, target.Hitbox.TopLeft(), target.Hitbox.BottomRight());
        Bloom(contact, target.whoAmI + 1);
    }

    public override bool OnTileCollide(Vector2 oldVelocity)
    {
        Bloom(Projectile.Center + Projectile.velocity, 0);
        return true;
    }

    private void Bloom(Vector2 center, int struckNpc)
    {
        if (bloomed || Projectile.owner != Main.myPlayer)
            return;

        bloomed = true;
        Projectile.NewProjectile(Projectile.GetSource_FromThis(), center, Vector2.Zero,
            ModContent.ProjectileType<SakuraBloom>(), (int)(Projectile.damage * .8f),
            Projectile.knockBack, Projectile.owner, struckNpc, Projectile.rotation);
    }

    public override void OnKill(int timeLeft)
    {
        if (timeLeft <= 0 && !Main.dedServ)
            SakuraParticles.Burst(Projectile.Center, 8);
    }

    public override bool PreDraw(ref Color lightColor)
    {
        Texture2D crescent = TextureAssets.Projectile[Type].Value;
        Texture2D ribbon = stream.Value;
        Vector2 direction = Projectile.rotation.ToRotationVector2();
        Vector2 normal = new(-direction.Y, direction.X);
        Vector2 head = Projectile.Center - Main.screenPosition;
        Vector2 origin = new(crescent.Width * .85f, crescent.Height * .5f);
        Vector2 scale = new(46f / crescent.Width, 49f / crescent.Height);
        int age = Lifetime - Projectile.timeLeft;
        float phase = age * .27f + Projectile.identity;

        for (int i = TrailSamples - 1; i >= 2; i -= 2)
        {
            if (age <= i || Projectile.oldPos[i] == Vector2.Zero)
                continue;

            float strength = 1f - i / (float)(TrailSamples + 1);
            Vector2 echo = Projectile.oldPos[i] + Projectile.Size * .5f - Main.screenPosition;
            echo += normal * (3.5f * MathF.Sin(phase - i * .5f));
            Main.EntitySpriteDraw(crescent, echo, null, new Color(199, 139, 238, 30) * (.25f * strength),
                Projectile.rotation, origin, scale * (.52f + .4f * strength), SpriteEffects.None);
        }

        // The flower in the supplied horizontal ribbon sits at its leading (right) end.
        Vector2 ribbonOrigin = new(ribbon.Width * .91f, ribbon.Height * .50f);
        float length = 38f + 81f * MathHelper.Clamp(age / 6f, 0f, 1f);
        Main.EntitySpriteDraw(crescent, head, null, new Color(243, 144, 215, 0) * .19f,
            Projectile.rotation, origin, scale * 1.1f, SpriteEffects.None);
        Main.EntitySpriteDraw(crescent, head, null, new Color(232, 186, 242) * .76f,
            Projectile.rotation, origin, scale, SpriteEffects.None);
        Main.EntitySpriteDraw(ribbon, head + direction * 3f, null, Color.White,
            Projectile.rotation, ribbonOrigin, new Vector2(length / ribbon.Width, 35f / ribbon.Height),
            SpriteEffects.None);
        return false;
    }
}
