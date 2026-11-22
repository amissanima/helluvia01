using System;
using System.IO;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace helluvia01.Content.Mechanics
{
    public sealed class Fracture : GlobalNPC
    {
        public const byte MaxStacks = 30;
        public const ushort DurationTicks = 240;
        public const float LossPerStack = 0.01f;

        public override bool InstancePerEntity => true;

        private byte stacks;
        private ulong expiresAt;
        private bool needsSync;

        public static void Apply(NPC target)
        {
            if (Main.netMode == NetmodeID.MultiplayerClient || !target.active || target.life <= 0)
                return;

            Fracture fracture = target.GetGlobalNPC<Fracture>();
            if (ShotSession.Tick >= fracture.expiresAt)
                fracture.stacks = 0;

            fracture.stacks = (byte)Math.Min(MaxStacks, fracture.stacks + 1);
            fracture.expiresAt = ShotSession.Tick + DurationTicks;
            fracture.needsSync = Main.netMode == NetmodeID.Server;
        }

        public override void PostAI(NPC npc)
        {
            if (stacks > 0 && ShotSession.Tick >= expiresAt)
            {
                stacks = 0;
                needsSync = Main.netMode == NetmodeID.Server;
            }

            if (needsSync)
            {
                npc.netUpdate = true;
                needsSync = false;
            }
        }

        public override void ModifyIncomingHit(NPC npc, ref NPC.HitModifiers modifiers)
        {
            if (stacks == 0 || ShotSession.Tick >= expiresAt)
                return;

            float factor = 1f - LossPerStack * stacks;
            // Include flat defense adjustments already present, such as Ichor.
            modifiers.Defense *= factor;
            modifiers.Defense.Flat *= factor;
        }

        public override void DrawEffects(NPC npc, ref Color drawColor)
        {
            if (stacks > 0 && ShotSession.Tick < expiresAt)
            {
                float strength = 0.12f + 0.25f * stacks / MaxStacks;
                drawColor = Color.Lerp(drawColor, new Color(255, 148, 63, drawColor.A), strength);
            }
        }

        public override void SendExtraAI(NPC npc, BitWriter bitWriter, BinaryWriter writer)
        {
            bool active = stacks > 0 && ShotSession.Tick < expiresAt;
            bitWriter.WriteBit(active);
            if (active)
            {
                writer.Write(stacks);
                writer.Write((ushort)(expiresAt - ShotSession.Tick));
            }
        }

        public override void ReceiveExtraAI(NPC npc, BitReader bitReader, BinaryReader reader)
        {
            if (bitReader.ReadBit())
            {
                stacks = Math.Min(reader.ReadByte(), MaxStacks);
                expiresAt = ShotSession.Tick + Math.Min(reader.ReadUInt16(), DurationTicks);
            }
            else
            {
                stacks = 0;
                expiresAt = 0;
            }
        }
    }
}
