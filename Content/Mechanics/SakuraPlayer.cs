using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
using helluvia01.Content.Items;
using helluvia01.Content.Particles;
using helluvia01.Content.Projectiles;

namespace helluvia01.Content.Mechanics;

public sealed class SakuraPlayer : ModPlayer
{
    internal bool InHotbar;
    private int nextStroke;
    private int activeSlot = -1;
    private int recovery, comboWindow;
    private int critDelay;

    internal bool Ready => recovery == 0;

    internal int Begin(bool alternate, int duration)
    {
        int stroke = alternate ? 3 : nextStroke;
        nextStroke = alternate ? 0 : (nextStroke + 1) % 3;
        activeSlot = Player.selectedItem;
        recovery = duration;
        comboWindow = duration + 24;
        return stroke;
    }

    internal bool TryShowCritical()
    {
        if (critDelay > 0)
            return false;
        critDelay = 6;
        return true;
    }

    private bool HasKatana()
    {
        for (int i = 0; i < 10; i++)
            if (Player.inventory[i].ModItem is SakuraKatana)
                return true;
        return false;
    }

    public override void PostUpdate()
    {
        if (recovery > 0) recovery--;
        if (comboWindow > 0) comboWindow--;
        if (critDelay > 0) critDelay--;
        if (comboWindow == 0 || activeSlot != Player.selectedItem || Player.HeldItem.ModItem is not SakuraKatana)
            nextStroke = 0;
        if (Player.whoAmI == Main.myPlayer)
            InHotbar = HasKatana();
        if (!Main.dedServ && InHotbar && !Player.dead && !Player.ghost && !Player.invis)
            SakuraParticles.Aura(Player);
    }

    public override void UpdateDead()
    {
        recovery = comboWindow = nextStroke = critDelay = 0;
        if (Player.whoAmI == Main.myPlayer)
            InHotbar = HasKatana();
    }

    public override void OnEnterWorld()
    {
        UpdateDead();
        if (Main.netMode == NetmodeID.MultiplayerClient && Player.whoAmI == Main.myPlayer)
            SyncPlayer(-1, -1, false);
    }

    public override void CopyClientState(ModPlayer targetCopy) =>
        ((SakuraPlayer)targetCopy).InHotbar = InHotbar;

    public override void SendClientChanges(ModPlayer clientPlayer)
    {
        if (InHotbar != ((SakuraPlayer)clientPlayer).InHotbar)
            SyncPlayer(-1, -1, false);
    }

    public override void SyncPlayer(int toWho, int fromWho, bool newPlayer) =>
        SakuraHits.SendHotbar(Player.whoAmI, InHotbar, toWho, fromWho);
}

public sealed class SakuraHipLayer : PlayerDrawLayer
{
    public override Position GetDefaultPosition() => new BeforeParent(PlayerDrawLayers.Torso);

    public override bool GetDefaultVisibility(PlayerDrawSet drawInfo)
    {
        Player player = drawInfo.drawPlayer;
        return drawInfo.shadow == 0f && !player.dead && !player.ghost && !player.invis &&
            player.GetModPlayer<SakuraPlayer>().InHotbar &&
            player.ownedProjectileCounts[ModContent.ProjectileType<SakuraSwing>()] == 0;
    }

    protected override void Draw(ref PlayerDrawSet drawInfo)
    {
        Player player = drawInfo.drawPlayer;
        int side = player.direction;
        float gravity = player.gravDir;
        float stride = Math.Min(1f, Math.Abs(player.velocity.X) * .15f);
        float sway = MathF.Sin(player.legFrame.Y / (float)player.legFrame.Height * .5f) * .065f * stride;
        float pitch = .62f + sway + MathHelper.Clamp(player.velocity.Y * gravity * .012f, -.09f, .09f);
        if (player.sitting.isSitting) pitch *= .65f;
        Vector2 direction = new(-side * MathF.Cos(pitch), gravity * MathF.Sin(pitch));
        bool flip = side * gravity > 0f;
        Texture2D blade = TextureAssets.Item[ModContent.ItemType<SakuraKatana>()].Value;
        Vector2 grip = SakuraBlade.Grip;
        if (flip) grip.Y = blade.Height - grip.Y;
        float angle = direction.ToRotation() - (flip ? -SakuraBlade.TipAngle : SakuraBlade.TipAngle);
        Vector2 hip = drawInfo.Position - Main.screenPosition + player.Size * .5f + player.bodyPosition
            + new Vector2(2f * side, 3.5f * gravity + drawInfo.seatYOffset);
        DrawData data = new(blade, hip, null, drawInfo.colorArmorBody, angle + player.bodyRotation,
            grip, .255f, flip ? SpriteEffects.FlipVertically : SpriteEffects.None, 0f);
        data.shader = drawInfo.cWaist;
        drawInfo.DrawDataCache.Add(data);
    }
}
