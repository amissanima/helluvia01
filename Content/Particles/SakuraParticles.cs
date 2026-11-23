using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.UI;
using Terraria.Utilities;

namespace helluvia01.Content.Particles;

[Autoload(Side = ModSide.Client)]
public sealed class SakuraParticles : ModSystem
{
    private enum Sprite { PetalSmall, PetalLarge, Flower, Spark, Crit }
    private enum Priority { Ambient, Trail, Burst, Flash }

    private struct Particle
    {
        public Vector2 Position, Velocity, Anchor;
        public float Rotation, Spin, Size, Phase, FlutterRate, Wave, Gravity, Opacity;
        public int Age, Life, Owner;
        public Color Tint;
        public Sprite Sprite;
        public Priority Priority;
        public float Swirl;
        public bool Distant;
    }

    private static SakuraParticles instance;
    private readonly UnifiedRandom random = new(Environment.TickCount);
    // The active prefix is packed. Expiration swaps in the last slot in constant time.
    private readonly Particle[] pool = new Particle[SakuraVisualConfig.MaximumParticles];
    private readonly int[] nextVisible = new int[SakuraVisualConfig.MaximumParticles];
    private readonly int[] firstVisible = new int[5];
    private readonly Asset<Texture2D>[] textures = new Asset<Texture2D>[5];
    private readonly int[] auraCounts = new int[Main.maxPlayers];
    private int count, spawned, skipped, lastSpawned, lastSkipped;
    private long lastFrame;
    private int sampleFrames;
    private double averageFrameMs = 1000d / 60d, updateMs, drawMs;
    private float performance = 1f;
    private LegacyGameInterfaceLayer diagnosticsLayer;
    private static SakuraVisualConfig Config => ModContent.GetInstance<SakuraVisualConfig>();
    private const string Path = "helluvia01/Content/Assets/Sakura/";

    public override void Load()
    {
        instance = this;
        foreach (Sprite sprite in Enum.GetValues<Sprite>())
            textures[(int)sprite] = ModContent.Request<Texture2D>(Path + sprite);
        diagnosticsLayer = new LegacyGameInterfaceLayer("helluvia01: Sakura particles", DrawDiagnostics, InterfaceScaleType.UI);
    }

    public override void OnWorldUnload()
    {
        count = spawned = skipped = lastSpawned = lastSkipped = sampleFrames = 0;
        lastFrame = 0;
        updateMs = drawMs = 0;
        averageFrameMs = 1000d / 60d;
        performance = 1f;
        Array.Clear(auraCounts);
    }

    public override void Unload()
    {
        instance = null;
        Array.Clear(textures);
        diagnosticsLayer = null;
    }

    internal static void Slash(Vector2 position, Vector2 tangent, int count) =>
        instance?.EmitDirectional(position, tangent, Math.Clamp(count, 0, 60), false);

    internal static void Launch(Vector2 position, Vector2 direction) =>
        instance?.EmitDirectional(position, direction, 14, true);

    internal static void Flight(Vector2 position, Vector2 velocity) => instance?.EmitFlight(position, velocity);

    internal static void Burst(Vector2 position, int count) =>
        instance?.EmitRadial(position, Math.Clamp(count, 0, 100), Priority.Burst);

    internal static void Hit(Vector2 position, bool critical) => instance?.EmitHit(position, critical);

    internal static void Cyclone(Vector2 center) => instance?.EmitCyclone(center);

    private void EmitCyclone(Vector2 center)
    {
        if (!Visible(center, 120f))
            return;
        int amount = Grant(48, Priority.Burst);
        float phase = random.NextFloat(MathHelper.TwoPi);
        float spin = random.NextBool() ? .24f : -.24f;
        for (int i = 0; i < amount; i++)
        {
            // Three spiral arms start inside the silhouette and expand as they rise.
            float angle = phase + (i % 3) * MathHelper.TwoPi / 3f + random.NextFloat(-.25f, .25f);
            Vector2 offset = angle.ToRotationVector2() * random.NextFloat(2f, 12f);
            Add(i % 9 == 0 ? Sprite.Crit : Sprite.Spark, Priority.Burst, center + offset,
                Vector2.Zero, random.Next(20, 34), i % 9 == 0 ? .43f : 1.2f);
            ref Particle p = ref pool[count - 1];
            p.Anchor = center;
            p.Swirl = spin * random.NextFloat(.85f, 1.15f);
        }
    }

    internal static void Aura(Player player) => instance?.EmitAura(player);

    // Padding is in world pixels. Camera.Center includes zoom and camera modifiers.
    private static bool Visible(Vector2 position, float padding)
    {
        Vector2 distance = position - Main.Camera.Center;
        Vector2 zoom = Main.GameViewMatrix.Zoom;
        return Math.Abs(distance.X) <= Main.screenWidth * .5f / zoom.X + padding
            && Math.Abs(distance.Y) <= Main.screenHeight * .5f / zoom.Y + padding;
    }

    private float Density => Config.Quality switch {
        SakuraQuality.Low => .32f, SakuraQuality.Medium => .59f, SakuraQuality.Ultra => 1f, _ => .85f
    };

    private float Lifetime => Config.Quality switch {
        SakuraQuality.Low => .58f, SakuraQuality.Medium => .80f, SakuraQuality.Ultra => 1.24f, _ => 1f
    };

    private int Grant(int wanted, Priority priority)
    {
        if (priority != Priority.Flash) {
            float density = Density * performance;
            if (priority == Priority.Burst)
                density = .20f + .80f * density;
            float scaled = wanted * density;
            wanted = (int)scaled + (random.NextFloat() < scaled % 1f ? 1 : 0);
        }

        // Reserve both resident slots and this frame's admissions for more significant events.
        float share = priority switch {
            Priority.Ambient => .52f, Priority.Trail => .82f, Priority.Burst => .98f, _ => 1f
        };
        int limit = Math.Clamp(Config.ParticleLimit, 100, pool.Length);
        int budget = Math.Clamp(Config.SpawnBudget, 12, 240);
        int room = Math.Min((int)(limit * share) - count, (int)(budget * share) - spawned);
        int granted = Math.Clamp(room, 0, wanted);
        skipped += wanted - granted;
        spawned += granted;
        return granted;
    }

    private Sprite ScatterSprite()
    {
        int roll = random.Next(100);
        return roll < 48 ? Sprite.PetalSmall : roll < 76 ? Sprite.PetalLarge : roll < 94 ? Sprite.Spark : Sprite.Flower;
    }

    private void Add(Sprite sprite, Priority priority, Vector2 position, Vector2 velocity, int life,
        float sizeFactor = 1f, int owner = -1)
    {
        bool glow = sprite >= Sprite.Spark;
        float size = sprite switch {
            Sprite.PetalSmall => random.NextFloat(4f, 7f),
            Sprite.PetalLarge => random.NextFloat(7f, 12f),
            Sprite.Flower => random.NextFloat(12f, 18f),
            Sprite.Spark => random.NextFloat(5f, 12f),
            _ => random.NextFloat(43f, 53f)
        };
        pool[count++] = new Particle {
            Sprite = sprite, Priority = priority, Owner = owner, Position = position, Velocity = velocity,
            Anchor = owner >= 0 ? Main.player[owner].Center : default,
            Rotation = random.NextFloat(MathHelper.TwoPi), Spin = random.NextFloat(-.065f, .065f) * (glow ? .36f : 1f),
            Size = size * sizeFactor, Phase = random.NextFloat(MathHelper.TwoPi), FlutterRate = random.NextFloat(.037f, .091f),
            Gravity = glow ? .018f : random.NextFloat(.018f, .049f),
            Opacity = glow ? 1f : random.NextFloat(.67f, .97f),
            Tint = glow ? new Color(255, 247, 222, 0) : Color.Lerp(Color.White, new Color(235, 186, 245), random.NextFloat()),
            Life = Math.Max(3, life)
        };
        if (owner >= 0)
            auraCounts[owner]++;
    }

    private int Life(Sprite sprite)
    {
        int ticks = sprite == Sprite.Spark ? random.Next(12, 29) : random.Next(88, 155);
        return (int)(ticks * Lifetime);
    }

    private void EmitDirectional(Vector2 position, Vector2 direction, int requested, bool launch)
    {
        if (!Visible(position, 120f))
            return;
        direction = direction.SafeNormalize(Vector2.UnitX);
        if (launch && Grant(1, Priority.Flash) != 0)
            Add(Sprite.Spark, Priority.Flash, position, direction * .7f, 13, 2.7f);
        Priority priority = launch ? Priority.Burst : Priority.Trail;
        int amount = Grant(requested, priority);
        for (int i = 0; i < amount; i++) {
            Sprite sprite = ScatterSprite();
            Vector2 scatter = random.NextVector2Circular(2.8f, 2.4f);
            Vector2 velocity = direction * random.NextFloat(.7f, 4.3f) + scatter;
            Add(sprite, priority, position + scatter * 2f, velocity, Life(sprite));
        }
    }

    private void EmitFlight(Vector2 position, Vector2 velocity)
    {
        if (!Visible(position, 80f) || Grant(1, Priority.Trail) == 0)
            return;
        Sprite sprite = ScatterSprite();
        Vector2 drift = random.NextVector2Circular(1.7f, 1.7f);
        // At most one admission per projectile tick and no more than 40 ticks of life.
        int life = Math.Min(40, (int)(random.Next(22, 41) * Lifetime));
        Add(sprite, Priority.Trail, position + drift * 6f, drift - velocity * .07f, life, .83f);
    }

    private void EmitRadial(Vector2 position, int requested, Priority priority)
    {
        if (!Visible(position, 160f))
            return;
        int amount = Grant(requested, priority);
        // Stratified angles keep a partially admitted burst round even under load.
        float phase = random.NextFloat(MathHelper.TwoPi);
        for (int i = 0; i < amount; i++) {
            float angle = phase + MathHelper.TwoPi * (i + random.NextFloat(.75f)) / amount;
            Vector2 ray = angle.ToRotationVector2();
            Sprite sprite = ScatterSprite();
            float speed = random.NextFloat(1.5f, sprite == Sprite.Spark ? 7.8f : 6.5f);
            Add(sprite, priority, position + ray * random.NextFloat(2f, 14f), ray * speed, Life(sprite));
        }
    }

    private void EmitHit(Vector2 position, bool critical)
    {
        if (!Visible(position, 90f))
            return;
        if (critical) {
            if (Grant(1, Priority.Flash) != 0)
                Add(Sprite.Crit, Priority.Flash, position, Vector2.Zero, 18);
            EmitRadial(position, 12, Priority.Burst);
        }
        int sparks = Grant(critical ? 5 : 3, critical ? Priority.Burst : Priority.Trail);
        for (int i = 0; i < sparks; i++)
            Add(Sprite.Spark, critical ? Priority.Burst : Priority.Trail, position, random.NextVector2Circular(3f, 3f), random.Next(8, 16));
    }

    private void EmitAura(Player player)
    {
        int owner = player.whoAmI;
        if ((uint)owner >= auraCounts.Length || !Visible(player.Center, 72f))
            return;
        int interval = Config.Quality switch {
            SakuraQuality.Low => 5, SakuraQuality.Medium => 4, SakuraQuality.Ultra => 2, _ => 3
        };
        if ((Main.GameUpdateCount + (uint)owner) % (uint)interval != 0)
            return;
        if (auraCounts[owner] >= Math.Clamp(Config.AuraLimit, 4, 48)) {
            skipped++;
            return;
        }
        if (Grant(1, Priority.Ambient) == 0)
            return;
        Vector2 orbit = random.NextFloat(MathHelper.TwoPi).ToRotationVector2();
        Vector2 offset = orbit * new Vector2(random.NextFloat(12f, 26f), random.NextFloat(13f, 25f));
        Vector2 velocity = new(-orbit.Y * .43f, orbit.X * .45f - .16f);
        Sprite sprite = random.Next(32) switch {
            0 => Sprite.Flower, 1 or 2 => Sprite.Spark, < 11 => Sprite.PetalLarge, _ => Sprite.PetalSmall
        };
        int life = (int)((sprite == Sprite.Spark ? random.Next(16, 29) : random.Next(71, 115)) * Lifetime);
        Add(sprite, Priority.Ambient, player.Center + offset, velocity, life, .78f, owner);
    }

    private void Remove(int index)
    {
        if (pool[index].Owner >= 0)
            auraCounts[pool[index].Owner]--;
        pool[index] = pool[--count];
    }

    public override void PostUpdateDusts()
    {
        bool measure = Config.Diagnostics;
        long start = measure ? Stopwatch.GetTimestamp() : 0;
        int limit = Math.Clamp(Config.ParticleLimit, 100, pool.Length);
        // Only needed if the user lowers the live limit while the pool is full.
        for (int tier = 0; count > limit; tier++) {
            for (int i = count - 1; i >= 0 && count > limit; i--)
                if ((int)pool[i].Priority <= tier)
                    Remove(i);
        }
        float wind = Main.windSpeedCurrent * .028f;
        Vector2 center = Main.Camera.Center;
        Vector2 extent = new Vector2(Main.screenWidth, Main.screenHeight) * .5f / Main.GameViewMatrix.Zoom
            + new Vector2(96f);
        for (int i = count - 1; i >= 0; i--) {
            ref Particle p = ref pool[i];
            if (++p.Age >= p.Life) {
                Remove(i);
                continue;
            }
            if (p.Owner >= 0) {
                Player player = Main.player[p.Owner];
                Vector2 motion = player.Center - p.Anchor;
                if (player.active && motion.LengthSquared() < 180f * 180f) {
                    p.Position += motion * .94f;
                    p.Anchor = player.Center;
                }
                else {
                    auraCounts[p.Owner]--;
                    p.Owner = -1;
                }
            }
            if (p.Swirl != 0f)
            {
                Vector2 radial = p.Position - p.Anchor;
                radial = radial.RotatedBy(p.Swirl) * (p.Age < 12 ? 1.15f : 1.035f);
                p.Anchor.Y -= 1.8f;
                p.Position = p.Anchor + radial;
                p.Rotation += p.Swirl * .5f;
                continue;
            }
            if (p.Sprite == Sprite.Crit)
                continue;
            if (p.Sprite == Sprite.Spark) {
                p.Velocity *= .94f;
                p.Velocity.Y += p.Gravity;
            }
            else {
                p.Phase += p.FlutterRate;
                // Recheck in staggered groups. Beyond the viewport a petal can coast
                // on its last flutter sample while its age and base motion continue.
                if (((p.Age + i) & 7) == 0)
                    p.Distant = Math.Abs(p.Position.X - center.X) > extent.X ||
                        Math.Abs(p.Position.Y - center.Y) > extent.Y;
                if (!p.Distant)
                    p.Wave = MathF.Sin(p.Phase);
                if (p.Owner >= 0) {
                    // A gentle pull towards the wearer closes the orbit as the tangent turns.
                    Vector2 offset = p.Position - p.Anchor;
                    p.Velocity = p.Velocity * .97f - offset * .0015f
                        + new Vector2(-offset.Y, offset.X) * .0009f;
                    p.Velocity.X += p.Wave * .017f;
                }
                else {
                    p.Velocity *= .967f;
                    p.Velocity.X += wind + p.Wave * .052f;
                    p.Velocity.Y += p.Gravity;
                }
            }
            p.Rotation += p.Spin;
            p.Position += p.Velocity;
        }
        if (measure)
            updateMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private void SampleFrame(long now)
    {
        if (lastFrame != 0) {
            double elapsed = Stopwatch.GetElapsedTime(lastFrame, now).TotalMilliseconds;
            // Focus changes, loading and game pauses are not particle load.
            if (elapsed >= 1 && elapsed <= 200 && !Main.gamePaused && Main.instance.IsActive) {
                averageFrameMs += (elapsed - averageFrameMs) * .045;
                if (++sampleFrames >= 24) {
                    sampleFrames = 0;
                    if (averageFrameMs > 21)
                        performance = Math.Max(.42f, performance - .065f);
                    else if (averageFrameMs < 17.8)
                        performance = Math.Min(1f, performance + .035f);
                }
            }
        }
        lastFrame = now;
    }

    public override void PostDrawTiles()
    {
        long start = Stopwatch.GetTimestamp();
        SampleFrame(start);
        lastSpawned = spawned;
        lastSkipped = skipped;
        spawned = skipped = 0;
        if (count == 0) {
            drawMs = 0;
            return;
        }

        Vector2 center = Main.Camera.Center;
        Vector2 extent = new Vector2(Main.screenWidth, Main.screenHeight) * .5f / Main.GameViewMatrix.Zoom;
        Array.Fill(firstVisible, -1);
        bool any = false;
        for (int i = count - 1; i >= 0; i--) {
            ref Particle p = ref pool[i];
            // Size is the longest dimension. This bounds a rotated, fluttering or swelling sprite.
            float radius = p.Size + 8f;
            if (Math.Abs(p.Position.X - center.X) > extent.X + radius || Math.Abs(p.Position.Y - center.Y) > extent.Y + radius)
                continue;
            int kind = (int)p.Sprite;
            nextVisible[i] = firstVisible[kind];
            firstVisible[kind] = i;
            any = true;
        }
        if (any)
            DrawVisible();
        if (Config.Diagnostics)
            drawMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private void DrawVisible()
    {
        SpriteBatch batch = Main.spriteBatch;
        using var saved = new WorldDrawBindings(Main.instance.GraphicsDevice, saveBuffers: true);
        batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None,
            RasterizerState.CullNone, null, Main.GameViewMatrix.TransformationMatrix);
        try {
            // Five texture groups in a single batch. In premultiplied alpha, a zero-alpha
            // vertex with nonzero RGB adds light. Draw this group after the solid petals.
            for (int kind = 0; kind < firstVisible.Length; kind++) {
                if (firstVisible[kind] < 0)
                    continue;
                Texture2D texture = textures[kind].Value;
                Vector2 origin = texture.Size() * .5f;
                float unit = 1f / Math.Max(texture.Width, texture.Height);
                for (int i = firstVisible[kind]; i >= 0; i = nextVisible[i]) {
                    ref Particle p = ref pool[i];
                    float t = (float)p.Age / p.Life;
                    float opacity = p.Opacity * Math.Min(1f, (p.Life - p.Age) / (p.Life * .38f));
                    Vector2 scale = new(p.Size * unit);
                    if (p.Sprite == Sprite.Crit) {
                        float swell = Math.Min(1f, (p.Age + 1) / 5f);
                        scale *= (.49f + .78f * swell) * (1f - .36f * t);
                        opacity *= .45f + .55f * swell;
                    }
                    else if (p.Sprite == Sprite.Spark)
                        scale *= 1.13f - .60f * t;
                    else {
                        scale.X *= .43f + .57f * Math.Abs(p.Wave);
                        scale.Y *= .98f + .07f * p.Wave;
                        opacity *= Math.Min(1f, (p.Age + 1) * .26f);
                    }
                    batch.Draw(texture, p.Position - Main.screenPosition, null, p.Tint * opacity, p.Rotation,
                        origin, scale, SpriteEffects.None, 0f);
                }
            }
        }
        finally {
            batch.End();
        }
    }

    public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
    {
        if (Config.Diagnostics)
            layers.Add(diagnosticsLayer);
    }

    private bool DrawDiagnostics()
    {
        string label = Language.GetTextValue("Mods.helluvia01.Sakura.ParticleDiagnostics", count, lastSpawned, lastSkipped,
            updateMs.ToString("0.00"), drawMs.ToString("0.00"));
        Utils.DrawBorderString(Main.spriteBatch, label, new Vector2(18f, 90f), Color.LightPink, .85f);
        return true;
    }
}
