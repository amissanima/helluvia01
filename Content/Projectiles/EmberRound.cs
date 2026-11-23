using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace helluvia01.Content.Projectiles;

/// <summary>Точный одиночный снаряд. Дополнительные обновления дают плавный полёт и частую проверку столкновений.</summary>
public sealed class EmberRound : ModProjectile
{
    private const float BaseRangeTiles = 10f, MaxRangeTiles = 60f, MaxBonus = 1f;
    private Vector2 start;
    private float TrailLength => MathHelper.Min(400f, Vector2.Distance(start, Projectile.Center));
    public override void OnSpawn(Terraria.DataStructures.IEntitySource source) => start = Projectile.Center;

    public override void SetDefaults()
    {
        Projectile.width = Projectile.height = 8;
        Projectile.friendly = true;
        Projectile.DamageType = DamageClass.Ranged;
        Projectile.penetrate = 1;
        Projectile.extraUpdates = 3;
        Projectile.timeLeft = 360;
    }

    public override void AI()
    {
        Projectile.rotation = Projectile.velocity.ToRotation();
        if (Main.dedServ) return;
        Lighting.AddLight(Projectile.Center, .8f, .26f, .04f);
        Dust.NewDustPerfect(Projectile.Center, DustID.Torch, Vector2.Zero).noGravity = true;
    }

    public override bool PreDraw(ref Color lightColor)
    {
        // Правый край горизонтальной текстуры следует за снарядом, левый остаётся позади него.
        SniperAfterglow.DrawTrail(Projectile.Center, Projectile.rotation, TrailLength, 1f);
        lightColor = Color.White;
        return true;
    }

    public override void OnKill(int timeLeft)
    {
        if (Main.dedServ) return;
        var glow = Dust.NewDustPerfect(Projectile.Center, ModContent.DustType<SniperAfterglow>(), Vector2.Zero);
        glow.rotation = Projectile.rotation;
        glow.scale = TrailLength;
    }

    public override void ModifyHitNPC(NPC target, ref NPC.HitModifiers modifiers)
    {
        // Один тайл равен 16 пикселям. До 10 тайлов бонус нулевой, к 60 тайлам он достигает +100%.
        // Фиксированная точка выстрела исключает влияние перемещения стрелка после спуска.
        var origin = new Vector2(Projectile.ai[0], Projectile.ai[1]);
        float tiles = Vector2.Distance(origin, target.Center) / 16f;
        float fraction = MathHelper.Clamp((tiles - BaseRangeTiles) / (MaxRangeTiles - BaseRangeTiles), 0f, 1f);
        modifiers.FinalDamage *= 1f + MaxBonus * fraction;
    }
}

/// <summary>После попадания оставляет на месте последнюю полосу света на четверть секунды.</summary>
public sealed class SniperAfterglow : ModDust
{
    public override string Texture => "helluvia01/Content/Particles/SniperTracer";
    internal static void DrawTrail(Vector2 head, float rotation, float length, float opacity)
    {
        var texture = ModContent.Request<Texture2D>("helluvia01/Content/Particles/SniperTracer", AssetRequestMode.ImmediateLoad).Value;
        Main.EntitySpriteDraw(texture, head - Main.screenPosition, null, Color.White * opacity, rotation,
            new Vector2(texture.Width, texture.Height * .5f), new Vector2(length / texture.Width, 2f), SpriteEffects.None);
    }

    public override bool Update(Dust dust)
    {
        dust.alpha += 17;
        dust.active = dust.alpha < 255;
        return false;
    }

    public override bool PreDraw(Dust dust)
    {
        DrawTrail(dust.position, dust.rotation, dust.scale, 1f - dust.alpha / 255f);
        return false;
    }
}
