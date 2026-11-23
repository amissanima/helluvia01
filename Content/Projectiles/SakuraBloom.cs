using helluvia01.Content.Mechanics;
using helluvia01.Content.Particles;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;

namespace helluvia01.Content.Projectiles;

public sealed class SakuraBloom : ModProjectile
{
    private const int Radius = 55;
    private const int Duration = 39;
    private static Asset<Texture2D> spark;
    private int Age => Duration - Projectile.timeLeft;

    public override string Texture => "helluvia01/Content/Assets/Sakura/Bloom";

    public override void Load()
    {
        if (!Main.dedServ)
            spark = ModContent.Request<Texture2D>("helluvia01/Content/Assets/Sakura/Spark");
    }

    public override void Unload() => spark = null;

    public override void SetDefaults()
    {
        Projectile.width = Projectile.height = 2 * Radius;
        Projectile.DamageType = DamageClass.Melee;
        Projectile.friendly = true;
        Projectile.penetrate = -1;
        Projectile.usesLocalNPCImmunity = true;
        Projectile.localNPCHitCooldown = -1;
        Projectile.ignoreWater = true;
        Projectile.tileCollide = false;
        Projectile.timeLeft = Duration;
    }

    public override bool? CanDamage() => Age == 0 ? null : false;
    public override bool? CanCutTiles() => false;

    // ai[0] is the original target's index plus one. A tile impact has no excluded target.
    public override bool? CanHitNPC(NPC target) => target.whoAmI + 1 == (int)Projectile.ai[0] ? false : null;

    public override bool? Colliding(Rectangle projHitbox, Rectangle targetHitbox)
    {
        Vector2 nearest = Vector2.Clamp(Projectile.Center, targetHitbox.TopLeft(), targetHitbox.BottomRight());
        return Vector2.DistanceSquared(nearest, Projectile.Center) <= Radius * Radius;
    }

    public override void OnHitNPC(NPC target, NPC.HitInfo hit, int damageDone)
        => SakuraHits.OnHit(Projectile, target, hit);

    public override void AI()
    {
        if (Main.dedServ)
            return;

        switch (Age)
        {
            case 0:
                SoundEngine.PlaySound(SoundID.Item29 with { Volume = .65f, Pitch = .23f }, Projectile.Center);
                SakuraParticles.Burst(Projectile.Center, 12);
                break;
            case 3:
                SakuraParticles.Burst(Projectile.Center, 66);
                break;
            case 8:
                SakuraParticles.Burst(Projectile.Center, 18);
                break;
        }

        float light = Projectile.timeLeft / (float)Duration;
        Lighting.AddLight(Projectile.Center, .9f * light, .36f * light, .58f * light);
    }

    public override bool PreDraw(ref Color lightColor)
    {
        Texture2D bloom = TextureAssets.Projectile[Type].Value;
        Texture2D glint = spark.Value;
        Vector2 center = Projectile.Center - Main.screenPosition;
        Vector2 origin = new(bloom.Width * .51f, bloom.Height * .51f);
        float opening = MathHelper.Clamp((Age + 1) / 9f, 0f, 1f);
        float unfurl = 1f - (1f - opening) * (1f - opening);
        float decay = MathHelper.Clamp((Age - 8f) / (Duration - 8f), 0f, 1f);
        float opacity = (1f - decay * decay) * MathHelper.Lerp(.28f, .92f, unfurl);
        float scale = (24f + 83f * unfurl + 22f * decay) / bloom.Width;
        float twist = MathHelper.Lerp(-.27f, .03f, unfurl) + .14f * decay + Projectile.ai[1] * .12f;

        Main.EntitySpriteDraw(bloom, center, null, new Color(255, 142, 205, 0) * (.28f * opacity),
            twist - .08f, origin, scale * 1.08f, SpriteEffects.None);
        Main.EntitySpriteDraw(bloom, center, null, Color.White * opacity, twist, origin,
            scale, SpriteEffects.None);

        float flash = MathHelper.Clamp(1f - Age / 10f, 0f, 1f);
        if (flash > 0f)
            Main.EntitySpriteDraw(glint, center, null, new Color(255, 229, 196, 0) * flash,
                twist + .1f, glint.Size() * .5f, (18f + 36f * flash) / glint.Width, SpriteEffects.None);
        return false;
    }
}
