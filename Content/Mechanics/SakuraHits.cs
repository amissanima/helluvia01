using System.IO;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using helluvia01.Content.Particles;

namespace helluvia01.Content.Mechanics;

// Combat messages only reproduce visual events on peers.
internal static class SakuraHits
{
    internal const byte Hotbar = 32, Critical = 33, Defeat = 34;

    private static ModPacket Begin(byte kind, int player)
    {
        ModPacket packet = ModContent.GetInstance<global::helluvia01.helluvia01>().GetPacket();
        packet.Write(kind);
        packet.Write((byte)player);
        return packet;
    }

    internal static void SendHotbar(int player, bool value, int to, int except)
    {
        ModPacket packet = Begin(Hotbar, player);
        packet.Write(value);
        packet.Send(to, except);
    }

    private static void SendEffect(byte kind, int owner, Vector2 position, int except = -1)
    {
        ModPacket packet = Begin(kind, owner);
        packet.WriteVector2(position);
        packet.Send(-1, except);
    }

    internal static void OnHit(Projectile projectile, NPC target, NPC.HitInfo hit)
    {
        if (projectile.owner != Main.myPlayer || Main.dedServ)
            return;
        Vector2 point = Vector2.Clamp(projectile.Center, target.Hitbox.TopLeft(), target.Hitbox.BottomRight());
        bool crit = hit.Crit && Main.player[projectile.owner].GetModPlayer<SakuraPlayer>().TryShowCritical();
        SakuraParticles.Hit(point, crit);
        if (crit && Main.netMode == NetmodeID.MultiplayerClient)
            SendEffect(Critical, projectile.owner, point);
        NPC victim = target.realLife >= 0 ? Main.npc[target.realLife] : target;
        if (victim.life <= 0 && !victim.friendly && victim.lifeMax > 1)
        {
            SakuraParticles.Cyclone(victim.Center);
            if (Main.netMode == NetmodeID.MultiplayerClient)
                SendEffect(Defeat, projectile.owner, victim.Center);
        }
    }

    internal static void Receive(byte kind, BinaryReader reader, int from)
    {
        int index = reader.ReadByte();
        bool server = Main.netMode == NetmodeID.Server;
        if (index >= Main.maxPlayers || (server && index != from))
            return;
        SakuraPlayer player = Main.player[index].GetModPlayer<SakuraPlayer>();
        if (kind == Hotbar)
        {
            player.InHotbar = reader.ReadBoolean();
            if (server)
                SendHotbar(index, player.InHotbar, -1, from);
            return;
        }

        Vector2 point = reader.ReadVector2();
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) ||
            !WorldGen.InWorld((int)(point.X / 16f), (int)(point.Y / 16f)) || !Main.player[index].active)
            return;
        if (server)
        {
            if (kind == Defeat || player.TryShowCritical())
                SendEffect(kind, index, point, from);
        }
        else if (kind == Defeat)
            SakuraParticles.Cyclone(point);
        else
            SakuraParticles.Hit(point, true);
    }
}
