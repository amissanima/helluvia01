using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework;
using ReLogic.Content;
using System.Collections.Generic;
using System;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.Graphics.CameraModifiers;
using Terraria.ModLoader;
using Terraria.Utilities;
using Terraria;
using helluvia01.Content.Items;
using helluvia01.Content.Mechanics;
using helluvia01.Content.Projectiles;

namespace helluvia01.Content.Particles
{
    public static class ShotEffects
    {
        private static readonly SoundStyle Fire = new("helluvia01/Content/Sounds/ShotgunFire") { MaxInstances = 8 };
        private static readonly SoundStyle Ready = new("helluvia01/Content/Sounds/ShotgunReady") { MaxInstances = 1 };

        public static void OnShot(Player player, Vector2 muzzle, Vector2 direction)
        {
            if (Main.dedServ)
                return;

            SoundEngine.PlaySound(Fire, muzzle);
            player.GetModPlayer<RecoilPlayer>().Start(direction);
            ModContent.GetInstance<ShotVisualSystem>().EmitShot(player, muzzle, direction);
            if (player.whoAmI == Main.myPlayer)
                Main.instance.CameraModifiers.Add(new PunchCameraModifier(muzzle, -direction, 1.7f,
                   1f, 15, uniqueIdentity: "helluvia01.Shotgun"));
        }

        public static void OnReady(Player player)
        {
            if (!Main.dedServ && player.whoAmI == Main.myPlayer)
                SoundEngine.PlaySound(Ready);
        }
    }

    [Autoload(Side = ModSide.Client)]
    public sealed class ShotVisualSystem : ModSystem
    {
        private enum Kind { Flash, Shell, Smoke, Spark }

        private struct Particle
        {
            public Kind Kind;
            public Vector2 Center, Velocity;
            public float Angle, Spin, Gravity;
            public int Age, Life;
        }

        private const int ParticleLimit = 320;
        private const int FlashTicks = 5;
        private readonly List<Particle> particles = new(ParticleLimit);
        private readonly UnifiedRandom random = new(Environment.TickCount);
        private readonly TrailPass trails = new();
        internal Asset<Texture2D> Core, Glow, Gun;
        private Asset<Texture2D> flash, shell, smoke, band;

        public override void Load()
        {
            Core = ModContent.Request<Texture2D>("helluvia01/Content/Projectiles/EnergyPellet");
            Gun = ModContent.Request<Texture2D>("helluvia01/Content/Items/EmberShotgun");
            Glow = ModContent.Request<Texture2D>("helluvia01/Content/Particles/ShotgunGlow");
            flash = ModContent.Request<Texture2D>("helluvia01/Content/Particles/ShotgunFlash");
            shell = ModContent.Request<Texture2D>("helluvia01/Content/Particles/ShotgunShell");
            smoke = ModContent.Request<Texture2D>("helluvia01/Content/Particles/ShotgunSmoke");
            band = ModContent.Request<Texture2D>("helluvia01/Content/Particles/ShotgunTrailGradient");
        }


        public override void OnWorldUnload() => particles.Clear();

        public override void Unload() => Main.QueueMainThreadAction(trails.Dispose);

        internal void EmitShot(Player player, Vector2 muzzle, Vector2 aim)
        {
            
            Add(Kind.Flash, muzzle, Vector2.Zero, aim.ToRotation(), FlashTicks);
            float side = aim.X < 0 ? -1f : 1f;
            Vector2 eject = new(-side * random.NextFloat(1.5f, 3f), -player.gravDir * random.NextFloat(2.8f, 4.2f));
            Add(Kind.Shell, EmberShotgun.WorldPoint(player, aim, EmberShotgun.EjectionFromGrip), eject + player.velocity * .25f,
                aim.ToRotation(), 80, side * .25f, player.gravDir);
            for (int i = 0; i < 4; i++) {
                Vector2 drift = aim.RotatedBy(random.NextFloat(-.30f, .30f)) * random.NextFloat(.5f, 2.6f);
                Add(Kind.Smoke, muzzle + drift * i, drift, random.NextFloat(MathHelper.TwoPi),
                    random.Next(17, 28), random.NextFloat(-.07f, .07f), player.gravDir);
            }
        }

        internal void EmitImpact(Projectile pellet)
        {
            Vector2 backward = -pellet.oldVelocity.SafeNormalize(Vector2.UnitY);
            for (int i = 0; i < 3; i++) {
                Vector2 velocity = backward.RotatedBy(random.NextFloat(-1.45f, 1.45f)) * random.NextFloat(1f, 4.2f);
                Add(Kind.Spark, pellet.Center, velocity, velocity.ToRotation(), random.Next(6, 12));
            }
        }

        private void Add(Kind kind, Vector2 position, Vector2 speed, float angle, int life, float spin = 0f, float gravity = 1f)
        {
            if (particles.Count < ParticleLimit)
                particles.Add(new Particle { Kind = kind, Center = position, Velocity = speed, Angle = angle,
                    Spin = spin, Gravity = gravity, Life = life });
        }

        public override void PostUpdateDusts()
        {
            for (int i = particles.Count - 1; i >= 0; i--) {
                Particle p = particles[i];
                if (++p.Age >= p.Life) {
                    particles.RemoveAt(i);
                    continue;
                }

                switch (p.Kind) {
                    case Kind.Flash:
                        Lighting.AddLight(p.Center, new Vector3(.92f, .46f, .12f) * (1f - (float)p.Age / p.Life));
                        break;
                    case Kind.Shell:
                        p.Velocity.Y += .26f * p.Gravity;
                        Vector2 step = Collision.TileCollision(p.Center - new Vector2(2f), p.Velocity, 4, 4,
                            gravDir: (int)p.Gravity);
                        p.Center += step;
                        if (step.X != p.Velocity.X)
                            p.Velocity.X *= -.4f;
                        if (step.Y != p.Velocity.Y) {
                            p.Velocity.Y = Math.Abs(p.Velocity.Y) > .9f ? -.35f * p.Velocity.Y : 0f;
                            p.Velocity.X *= .70f;
                            p.Spin *= .64f;
                        }
                        break;
                    case Kind.Smoke:
                        p.Velocity *= .92f;
                        p.Velocity.Y -= .048f * p.Gravity;
                        p.Center += p.Velocity;
                        break;
                    case Kind.Spark:
                        p.Velocity *= .90f;
                        p.Velocity.Y += .12f;
                        p.Center += p.Velocity;
                        p.Angle = p.Velocity.ToRotation();
                        break;
                }

                p.Angle += p.Spin;
                particles[i] = p;
            }
        }

        public override void PostDrawTiles()
        {
            trails.Draw(band);
            if (particles.Count == 0)
                return;

            using var bindings = new WorldDrawBindings(Main.instance.GraphicsDevice, saveBuffers: true);
            SpriteBatch batch = Main.spriteBatch;
            batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None,
                RasterizerState.CullNone, null, Main.GameViewMatrix.TransformationMatrix);
            try {
                foreach (Particle p in particles)
                {
                    Vector2 pos = p.Center - Main.screenPosition;
                    float t = (float)p.Age / p.Life;
                    float fade = 1f - t;
                    switch (p.Kind) {
                        case Kind.Flash:
                            Texture2D rays = flash.Value;
                            float swell = 1.67f + .55f * Math.Min(1f, t * 4f);
                            batch.Draw(rays, pos, null, Color.White * fade, p.Angle + .065f * t,
                                new Vector2(0f, rays.Height * .5f), swell * (1f - .18f * t), SpriteEffects.None, 0f);
                            break;
                        case Kind.Shell:
                            Color light = Lighting.GetColor(p.Center.ToTileCoordinates());
                            batch.Draw(shell.Value, pos, null, light * Math.Min(1f, fade * 5f), p.Angle,
                                shell.Value.Size() * .5f, .85f, SpriteEffects.None, 0f);
                            break;
                        case Kind.Smoke:
                            batch.Draw(smoke.Value, pos, null, new Color(160, 147, 148) * (.49f * fade),
                                p.Angle, smoke.Value.Size() * .5f, .22f + .42f * t, SpriteEffects.None, 0f);
                            break;
                        case Kind.Spark:
                            batch.Draw(TextureAssets.MagicPixel.Value, pos, new Rectangle(0, 0, 1, 1),
                                new Color(255, 165, 54, 0) * fade, p.Angle, new Vector2(1f, .5f),
                                new Vector2(2f + p.Velocity.Length(), 1.45f * fade), SpriteEffects.None, 0f);
                            break;
                    }

                }
            }
            finally {
                batch.End();
            }
        }


    }

    // PostDrawTiles is a public world-space draw hook entered with no SpriteBatch in progress.
    internal sealed class TrailPass : IDisposable
    {
        private const float MaxLength = 78f;
        private const float HeadRadius = 3.6f;
        private const int TriangleVertices = 3;
        private const int QuadVertices = 6;
        private const int BatchQuads = 512;
        private readonly VertexPositionColorTexture[] vertices = new VertexPositionColorTexture[BatchQuads * QuadVertices];
        private BasicEffect effect;

        public void Dispose() => effect?.Dispose();

        public void Draw(Asset<Texture2D> gradient)
        {
            GraphicsDevice device = Main.instance.GraphicsDevice;
            int count = 0;
            bool began = false;
            WorldDrawBindings saved = default;

            try {
                foreach (Projectile pellet in Main.ActiveProjectiles) {
                    if (pellet.ModProjectile is not EnergyPellet)
                        continue;
                    // The whole strip fits within MaxLength of its head.
                    Vector2 offset = pellet.Center - Main.Camera.Center;
                    if (Math.Abs(offset.X) > Main.screenWidth + MaxLength || Math.Abs(offset.Y) > Main.screenHeight + MaxLength)
                        continue;

                    if (!began) {
                        saved = new WorldDrawBindings(device, saveBuffers: false);
                        began = true;
                        effect ??= new BasicEffect(device) { TextureEnabled = true, VertexColorEnabled = true };
                        Viewport view = device.Viewport;
                        effect.Texture = gradient.Value;
                        effect.World = Main.GameViewMatrix.TransformationMatrix;
                        effect.Projection = Matrix.CreateOrthographicOffCenter(0, view.Width, view.Height, 0, 0, 1);
                        device.BlendState = BlendState.Additive;
                        device.DepthStencilState = DepthStencilState.None;
                        device.RasterizerState = RasterizerState.CullNone;
                        effect.CurrentTechnique.Passes[0].Apply();
                        device.SamplerStates[0] = SamplerState.LinearClamp;
                    }

                    AppendRibbon(device, pellet, ref count);
                }
                Flush(device, ref count);
            }
            finally {
                if (began)
                    saved.Dispose();
            }
        }

        private static void GetPath(Projectile pellet, Span<Vector2> points, Span<float> distances, out int size)
        {
            points[0] = pellet.Center - Main.screenPosition;
            distances[0] = 0f;
            size = 1;
            Vector2 centerOffset = pellet.Size * .5f - Main.screenPosition;
            foreach (Vector2 history in pellet.oldPos) {
                if (history == Vector2.Zero)
                    break;

                int last = size - 1;
                Vector2 delta = history + centerOffset - points[last];
                float length = delta.Length();
                if (length < .015f)
                    continue;
                if (length > MaxLength)
                    break; // Discontinuous network correction.
                float step = Math.Min(length, MaxLength - distances[last]);
                points[size] = points[last] + delta * (step / length);
                distances[size++] = distances[last] + step;
                if (step < length || distances[size - 1] >= MaxLength)
                    break;
            }
        }

        private void AppendRibbon(GraphicsDevice device, Projectile pellet, ref int count)
        {
            Span<Vector2> points = stackalloc Vector2[pellet.oldPos.Length + 1];
            Span<float> distances = stackalloc float[points.Length];
            GetPath(pellet, points, distances, out int size);
            if (size < 2)
                return;

            VertexPositionColorTexture prevL = default, prevR = default;
            for (int i = 0; i < size; i++) {
                // Shared cross-sections keep both triangles connected around bends.
                Vector2 tangent = points[Math.Min(i + 1, size - 1)] - points[Math.Max(0, i - 1)];
                tangent = tangent.SafeNormalize(Vector2.UnitX);
                float remaining = 1f - distances[i] / distances[size - 1];
                Vector2 normal = new Vector2(-tangent.Y, tangent.X) * (HeadRadius * remaining);
                Color tint = Color.Lerp(new Color(243, 68, 16), new Color(255, 217, 124), remaining) * remaining;
                VertexPositionColorTexture nextL = new(new Vector3(points[i] + normal, 0f), tint, Vector2.Zero);
                VertexPositionColorTexture nextR = new(new Vector3(points[i] - normal, 0f), tint, Vector2.UnitY);

                if (i > 0) {
                    if (count == vertices.Length)
                        Flush(device, ref count);
                    vertices[count++] = prevL;
                    vertices[count++] = prevR;
                    vertices[count++] = nextL;
                    vertices[count++] = nextL;
                    vertices[count++] = prevR;
                    vertices[count++] = nextR;
                }
                prevL = nextL;
                prevR = nextR;
            }
        }

        private void Flush(GraphicsDevice device, ref int count)
        {
            if (count != 0)
                device.DrawUserPrimitives(PrimitiveType.TriangleList, vertices, 0, count / TriangleVertices);
            count = 0;
        }
    }

    internal readonly struct WorldDrawBindings : IDisposable
    {
        private readonly GraphicsDevice device;
        private readonly BlendState blend;
        private readonly DepthStencilState depth;
        private readonly RasterizerState rasterizer;
        private readonly SamplerState sampler;
        private readonly Texture texture;
        private readonly VertexBufferBinding[] vertices;
        private readonly IndexBuffer indices;

        internal WorldDrawBindings(GraphicsDevice device, bool saveBuffers)
        {
            this.device = device;
            blend = device.BlendState;
            depth = device.DepthStencilState;
            rasterizer = device.RasterizerState;
            sampler = device.SamplerStates[0];
            texture = device.Textures[0];
            // DrawUserPrimitives preserves the public vertex/index bindings in FNA.
            // SpriteBatch can bind its own vertex and index buffers.
            vertices = saveBuffers ? device.GetVertexBuffers() : null;
            indices = device.Indices;
        }

        public void Dispose()
        {
            device.BlendState = blend;
            device.DepthStencilState = depth;
            device.RasterizerState = rasterizer;
            device.SamplerStates[0] = sampler;
            device.Textures[0] = texture;
            if (vertices != null) {
                device.SetVertexBuffers(vertices);
                device.Indices = indices;
            }
        }
    }
}
