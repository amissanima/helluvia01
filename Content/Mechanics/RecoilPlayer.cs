using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework;
using System;
using Terraria.DataStructures;
using Terraria.ModLoader;
using Terraria;
using helluvia01.Content.Items;
using helluvia01.Content.Mechanics;
using helluvia01.Content.Particles;
using helluvia01.Content.Projectiles;

public class RecoilPlayer : ModPlayer
{
    public float RecoilAngle;

    public override void PostUpdate()
    {
        UpdateShotgunPose();
        if (!Player.controlUseItem)
            RecoilAngle = Math.Max(0f, RecoilAngle - MathHelper.ToRadians(1f));
    }

    private const int ReturnTicks = 10;
    private const float KickPixels = 4.2f;
    private const float LiftPixels = 1.6f;
    private const float KickAngle = .19f;
    private Vector2 aim;
    private uint firedAt;
    private bool active;
    private int slot;

    internal void Start(Vector2 direction)
    {
        active = !Player.dead && Player.HeldItem.ModItem is EmberShotgun;
        firedAt = Main.GameUpdateCount;
        slot = Player.selectedItem;
        aim = direction;
    }

    internal bool Visible => active && Main.GameUpdateCount - firedAt < ReturnTicks &&
        Player.active && !Player.dead && !Player.noItems && !Player.CCed &&
        Player.selectedItem == slot && Player.HeldItem.ModItem is EmberShotgun;

    private void UpdateShotgunPose()
    {
        if (!Visible) {
            active = false;
            return;
        }

        GetPose(out _, out float angle, out _);
        Player.SetCompositeArmFront(true, Player.CompositeArmStretchAmount.Quarter, angle - MathHelper.PiOver2);
        Player.SetCompositeArmBack(true, Player.CompositeArmStretchAmount.Full, angle - MathHelper.PiOver2);
    }

    public override void UpdateDead() => active = false;

    public override void HideDrawLayers(PlayerDrawSet drawInfo)
    {
        if (Visible)
            PlayerDrawLayers.HeldItem.Hide();
    }

    internal void GetPose(out Vector2 grip, out float angle, out bool flip)
    {
        float returnFraction = 1f - (Main.GameUpdateCount - firedAt) / (float)ReturnTicks;
        float kick = MathHelper.Clamp((Main.GameUpdateCount - firedAt) / 1.5f, 0f, 1f) * returnFraction * returnFraction;
        int side = aim.X == 0f ? Player.direction : aim.X < 0f ? -1 : 1;
        float orientation = side * Player.gravDir;
        angle = aim.ToRotation() - orientation * KickAngle * kick;
        grip = Player.RotatedRelativePoint(Player.MountedCenter) - aim * (KickPixels * kick)
            - Vector2.UnitY * (Player.gravDir * LiftPixels * kick);
        flip = orientation < 0f;
    }

}

public sealed class ShotgunDrawLayer : PlayerDrawLayer
{
    public override Position GetDefaultPosition() => new Between(PlayerDrawLayers.SolarShield, PlayerDrawLayers.ArmOverItem);

    public override bool GetDefaultVisibility(PlayerDrawSet drawInfo) =>
        drawInfo.shadow == 0f && !drawInfo.drawPlayer.invis && drawInfo.drawPlayer.GetModPlayer<RecoilPlayer>().Visible;

    protected override void Draw(ref PlayerDrawSet drawInfo)
    {
        Player player = drawInfo.drawPlayer;
        player.GetModPlayer<RecoilPlayer>().GetPose(out Vector2 grip, out float angle, out bool flip);
        Texture2D gun = ModContent.GetInstance<ShotVisualSystem>().Gun.Value;
        Vector2 origin = EmberShotgun.GripPixel;
        if (flip)
            origin.Y = gun.Height - origin.Y;
        Vector2 drawPosition = grip + drawInfo.Position - player.position - Main.screenPosition;
        Color light = Lighting.GetColor(grip.ToTileCoordinates());
        drawInfo.DrawDataCache.Add(new DrawData(gun, drawPosition, null, light, angle, origin,
            EmberShotgun.WeaponScale, flip ? SpriteEffects.FlipVertically : SpriteEffects.None, 0f));
    }
}
