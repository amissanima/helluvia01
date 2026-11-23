using System;
using helluvia01.Content.Items;
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

public sealed class SakuraSwing : ModProjectile
{
    private const string Art = "helluvia01/Content/Assets/Sakura/";
    private const float WindupEnd = .22f, CutEnd = .73f, LaunchTime = .45f;
    private static Asset<Texture2D> mainArc, midArc, backArc, chargeSpark;

    private int age;
    private float progress, previousProgress, aimAngle, handedness;
    private Player Owner => Main.player[Projectile.owner];
    private int Stroke => (int)Projectile.ai[0];
    private bool Casting => Stroke == 3;
    private float EdgeLength => SakuraBlade.Length * Projectile.scale;
    private bool Cutting => !Casting && progress > WindupEnd && previousProgress < CutEnd;
    public override string Texture => SakuraBlade.Path;

    public override void Load()
    {
        if (Main.dedServ)
            return;
        mainArc = ModContent.Request<Texture2D>(Art + "SlashMain");
        midArc = ModContent.Request<Texture2D>(Art + "SlashMid");
        backArc = ModContent.Request<Texture2D>(Art + "SlashBack");
        chargeSpark = ModContent.Request<Texture2D>(Art + "Spark");
    }

    public override void Unload() => mainArc = midArc = backArc = chargeSpark = null;

    public override void SetStaticDefaults()
    {
        ProjectileID.Sets.HeldProjDoesNotUsePlayerGfxOffY[Type] = true;
        ProjectileID.Sets.AllowsContactDamageFromJellyfish[Type] = true;
    }

    public override void SetDefaults()
    {
        Projectile.width = Projectile.height = 10;
        Projectile.friendly = true;
        Projectile.hide = true;
        Projectile.tileCollide = false;
        Projectile.ignoreWater = true;
        Projectile.ownerHitCheck = true;
        Projectile.penetrate = -1;
        Projectile.usesLocalNPCImmunity = true;
        Projectile.localNPCHitCooldown = -1;
        Projectile.DamageType = DamageClass.Melee;
        Projectile.timeLeft = 120;
    }

    public override bool ShouldUpdatePosition() => false;
    public override bool? CanDamage() => Cutting ? null : false;
    public override bool? CanCutTiles() => false;

    public override void AI()
    {
        Player player = Owner;
        if (!player.active || player.dead || player.CCed || player.noItems ||
            (Projectile.owner == Main.myPlayer && player.HeldItem.ModItem is not SakuraKatana))
        {
            Projectile.Kill();
            return;
        }

        int duration = Math.Max(1, (int)Projectile.ai[1]);
        if (age == 0)
        {
            Projectile.timeLeft = duration;
            aimAngle = Projectile.velocity.ToRotation();
            Projectile.spriteDirection = Projectile.velocity.X == 0f ? player.direction : Math.Sign(Projectile.velocity.X);
        }
        previousProgress = progress;
        progress = Math.Min(1f, ++age / (float)duration);
        player.ChangeDir(Projectile.spriteDirection);
        handedness = player.direction * player.gravDir;
        Projectile.rotation = AngleAt(progress);
        Projectile.scale = .65f * Projectile.ai[2];
        player.heldProj = Projectile.whoAmI;
        player.SetCompositeArmFront(true, Player.CompositeArmStretchAmount.Full, ArmRotation(Projectile.rotation));
        Projectile.Center = HandAt(Projectile.rotation);

        if (Casting)
        {
            if (previousProgress < LaunchTime && progress >= LaunchTime)
                ReleaseCrescent();
        }
        else if (Cutting && !Main.dedServ)
        {
            EmitSlash();
            if (previousProgress <= WindupEnd)
                SoundEngine.PlaySound(SoundID.Item1, Projectile.Center);
        }

        if (!Main.dedServ)
        {
            float light = Casting ? ChargeIntensity() : TrailIntensity(progress, 0f);
            Lighting.AddLight(Projectile.Center + Projectile.rotation.ToRotationVector2() * EdgeLength * .82f,
                .48f * light, .13f * light, .30f * light);
        }
    }

    private static float Ease(float t) => MathHelper.SmoothStep(0f, 1f, Math.Clamp(t, 0f, 1f));

    private float AngleAt(float t)
    {
        if (Casting)
        {
            float cast = .2f - 1.05f * Ease(t / .24f) + .85f * Ease((t - .24f) / (LaunchTime - .24f))
                + .22f * Ease((t - LaunchTime) / (1f - LaunchTime));
            return aimAngle + cast * handedness;
        }

        // Adjacent strokes share a recovery / ready pose, including the return from the finisher.
        (float ready, float drawn, float follow, float rest) = Stroke switch
        {
            1 => (-1.06f, -1.62f, 1.46f, 1.08f),
            2 => (1.08f, 1.90f, -1.90f, .18f),
            _ => (.18f, 1.50f, -1.47f, -1.06f)
        };
        float offset = ready + (drawn - ready) * Ease(t / WindupEnd)
            + (follow - drawn) * Ease((t - WindupEnd) / (CutEnd - WindupEnd))
            + (rest - follow) * Ease((t - CutEnd) / (1f - CutEnd));
        return aimAngle + offset * handedness;
    }

    private float ArmRotation(float worldAngle) => (worldAngle - Owner.fullRotation) * Owner.gravDir - MathHelper.PiOver2;

    private Vector2 HandAt(float angle)
    {
        Player player = Owner;
        Vector2 hand = player.GetFrontHandPosition(Player.CompositeArmStretchAmount.Full, ArmRotation(angle));
        if (player.gravDir < 0f)
            hand.Y = 2f * player.Center.Y - hand.Y;
        return player.RotatedRelativePoint(hand);
    }

    private void EmitSlash()
    {
        const int groups = 10;
        float start = Math.Clamp((previousProgress - WindupEnd) / (CutEnd - WindupEnd), 0f, 1f);
        float end = Math.Clamp((progress - WindupEnd) / (CutEnd - WindupEnd), 0f, 1f);
        for (int i = (int)(start * groups); i < (int)(end * groups); i++)
        {
            float t = WindupEnd + (CutEnd - WindupEnd) * (i + .5f) / groups;
            float angle = AngleAt(t);
            Vector2 blade = angle.ToRotationVector2();
            Vector2 tangent = new Vector2(-blade.Y, blade.X) * (Stroke == 1 ? handedness : -handedness);
            Vector2 position = HandAt(angle) + blade * (EdgeLength * Main.rand.NextFloat(.56f, 1f));
            SakuraParticles.Slash(position, tangent, Stroke == 2 ? 6 : 4);
        }
    }

    private void ReleaseCrescent()
    {
        Vector2 aim = aimAngle.ToRotationVector2();
        Vector2 tip = HandAt(aimAngle) + aim * EdgeLength;
        if (Projectile.owner == Main.myPlayer)
        {
            // A drawn blade may extend beyond a wall. Start the crescent on the owner's side in that case.
            Vector2 origin = Owner.MountedCenter;
            Vector2 halfBox = new(10f);
            if (Collision.CanHitLine(origin, 1, 1, tip - halfBox, 20, 20) &&
                !Collision.SolidCollision(tip - halfBox, 20, 20))
                origin = tip;
            Projectile.NewProjectile(Projectile.GetSource_FromThis(), origin, aim * 18f,
                ModContent.ProjectileType<SakuraCrescent>(), (int)MathF.Round(Projectile.damage * 1.5f),
                Projectile.knockBack, Projectile.owner);
        }
        if (!Main.dedServ)
        {
            SakuraParticles.Launch(tip, aim);
            SoundEngine.PlaySound(SoundID.Item9, tip);
        }
    }

    public override bool? Colliding(Rectangle projHitbox, Rectangle targetHitbox)
    {
        if (!Cutting)
            return false;
        float reach = EdgeLength + 5f;
        Vector2 nearest = Vector2.Clamp(Projectile.Center, targetHitbox.TopLeft(), targetHitbox.BottomRight());
        if (Vector2.DistanceSquared(nearest, Projectile.Center) > (reach + 32f) * (reach + 32f))
            return false;

        float start = AngleAt(Math.Max(WindupEnd, previousProgress));
        float end = AngleAt(Math.Min(CutEnd, progress));
        // Sweep the angular interval crossed this tick. Even a very fast prefix cannot skip a thin target.
        int steps = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(end - start) * reach / 7f));
        for (int i = 0; i <= steps; i++)
        {
            float angle = MathHelper.Lerp(start, end, i / (float)steps);
            Vector2 grip = HandAt(angle);
            Vector2 tip = grip + angle.ToRotationVector2() * reach;
            float distance = 0f;
            if (Collision.CheckAABBvLineCollision(targetHitbox.TopLeft(), targetHitbox.Size(), grip, tip,
                10f * Projectile.ai[2], ref distance))
                return true;
        }
        return false;
    }

    public override void ModifyHitNPC(NPC target, ref NPC.HitModifiers modifiers) =>
        modifiers.HitDirectionOverride = target.Center.X >= Owner.Center.X ? 1 : -1;

    public override void OnHitNPC(NPC target, NPC.HitInfo hit, int damageDone) => SakuraHits.OnHit(Projectile, target, hit);

    private float ChargeIntensity() =>
        Ease(progress / LaunchTime) * (1f - Ease((progress - LaunchTime) / .18f));

    private float TrailIntensity(float t, float lag)
    {
        float time = t - lag;
        float fadeEnd = Stroke == 2 ? 1f : .92f;
        return Ease((time - WindupEnd) / .23f) * (1f - Ease((time - .54f) / (fadeEnd - .54f)));
    }

    public override bool PreDraw(ref Color lightColor)
    {
        if (age == 0)
            return false;
        Vector2 blade = Projectile.rotation.ToRotationVector2();
        if (!Casting)
        {
            DrawArc(backArc.Value, new(48, 7), new(57, 107), 122f, .055f, 1.36f, .42f, new Color(173, 119, 213));
            DrawArc(midArc.Value, new(26, 4), new(36, 107), 120f, .028f, 1.18f, .66f, new Color(224, 165, 242));
            DrawArc(mainArc.Value, new(93, 3), new(8, 108), 137f, 0f, 1f, .92f, new Color(255, 240, 251, 226));
        }

        Texture2D texture = TextureAssets.Projectile[Type].Value;
        bool flip = handedness < 0f;
        Vector2 origin = SakuraBlade.Grip;
        if (flip)
            origin.Y = texture.Height - origin.Y;
        Color metal = Color.Lerp(lightColor, Color.White, .34f);
        Main.EntitySpriteDraw(texture, Projectile.Center - Main.screenPosition, null, metal,
            Projectile.rotation + (flip ? SakuraBlade.TipAngle : -SakuraBlade.TipAngle), origin, Projectile.scale,
            flip ? SpriteEffects.FlipVertically : SpriteEffects.None);

        if (Casting)
        {
            float pulse = ChargeIntensity();
            Texture2D spark = chargeSpark.Value;
            Main.EntitySpriteDraw(spark, Projectile.Center + blade * EdgeLength - Main.screenPosition, null,
                new Color(255, 225, 202, 75) * pulse, .5f * progress, spark.Size() * .5f,
                (5f + 23f * pulse) / spark.Width, SpriteEffects.None);
        }
        return false;
    }

    private void DrawArc(Texture2D texture, Vector2 top, Vector2 bottom, float sourceDepth, float lag, float radiusScale,
        float opacity, Color tint)
    {
        float t = Math.Clamp(progress - lag, WindupEnd, CutEnd);
        float leading = AngleAt(t);
        float swept = leading - AngleAt(WindupEnd);
        float span = Math.Min(MathF.Abs(swept), 2.55f);
        float strength = TrailIntensity(progress, lag) * opacity * (Stroke == 2 ? 1f : .88f);
        if (span < .10f || strength < .015f)
            return;

        // Fit both painted tips to the ends of the swept chord. Its depth grows with the swept angle.
        float half = span * .5f;
        float middle = leading - MathF.CopySign(half, swept);
        Vector2 leadingDirection = leading.ToRotationVector2();
        Vector2 pivot = HandAt(leading) - leadingDirection * 10f;
        float radius = (EdgeLength + 10f) * radiusScale;
        Vector2 topPosition = pivot + (middle - half).ToRotationVector2() * radius;
        float scaleX = radius * (1f - MathF.Cos(half)) / sourceDepth;
        float chord = 2f * radius * MathF.Sin(half);
        float dx = (bottom.X - top.X) * scaleX;
        float dy = MathF.Sqrt(Math.Max(0f, chord * chord - dx * dx));
        float rotation = middle + MathHelper.PiOver2 - MathF.Atan2(dy, dx);
        Main.EntitySpriteDraw(texture, topPosition - Main.screenPosition, null,
            tint * strength, rotation, top,
            new Vector2(scaleX, dy / (bottom.Y - top.Y)), SpriteEffects.None);
    }
}
