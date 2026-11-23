using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using helluvia01.Content.Mechanics;
using helluvia01.Content.Projectiles;

namespace helluvia01.Content.Items;

/// <summary>Однозарядная энергетическая винтовка. Параметры выстрела фиксируются при нажатии.</summary>
public sealed class EmberSniper : ModItem
{
    internal const int Windup = 24, Cooldown = 480;
    public override Vector2? HoldoutOffset() => new Vector2(-24f, 0f);

    public override void SetDefaults()
    {
        Item.width = 94;
        Item.height = 24;
        Item.damage = 175;
        Item.DamageType = DamageClass.Ranged;
        Item.knockBack = 8f;
        Item.useStyle = ItemUseStyleID.Shoot;
        Item.useTime = Item.useAnimation = Windup + 4;
        Item.noMelee = true;
        Item.shoot = ModContent.ProjectileType<EmberRound>();
        Item.shootSpeed = 18f;
        Item.rare = ItemRarityID.Red;
        Item.value = Item.sellPrice(gold: 12);
    }

    public override bool CanUseItem(Player player) => player.GetModPlayer<SniperCharge>().Ready;

    public override bool Shoot(Player player, EntitySource_ItemUse_WithAmmo source, Vector2 position,
        Vector2 velocity, int type, int damage, float knockback)
    {
        if (player.whoAmI == Main.myPlayer)
            player.GetModPlayer<SniperCharge>().Begin(Item, velocity, damage, knockback);
        return false; // Штатный мгновенный выстрел заменён одним выстрелом после подготовки.
    }

    public override void AddRecipes() => CreateRecipe().AddIngredient(ItemID.SniperRifle)
        .AddIngredient(ItemID.FragmentSolar, 12).AddTile(TileID.LunarCraftingStation).Register();
}

/// <summary>Хранит подготовку и срок перезарядки у стрелка, поэтому смена экземпляра винтовки не сбрасывает таймер.</summary>
public sealed class SniperCharge : ModPlayer
{
    private Item charging;
    private int slot, damage;
    private float knockback;
    private Vector2 velocity;
    private ulong fireAt, readyAt;
    internal bool Ready => charging == null && ShotSession.Tick >= readyAt;
    internal float FlareScale => charging != null
        ? .15f + .55f * (1f - (float)(fireAt - ShotSession.Tick) / EmberSniper.Windup)
        : readyAt > ShotSession.Tick + EmberSniper.Cooldown - 3 ? .95f : 0f;
    internal Vector2 Muzzle => Player.RotatedRelativePoint(Player.MountedCenter)
        + velocity.SafeNormalize(Vector2.UnitX * Player.direction) * 67f;

    public override void OnEnterWorld() { charging = null; readyAt = 0; }
    public override void UpdateDead() => charging = null;

    internal void Begin(Item rifle, Vector2 speed, int shotDamage, float recoil)
    {
        charging = rifle;
        slot = Player.selectedItem;
        velocity = speed;
        damage = shotDamage;
        knockback = recoil;
        fireAt = ShotSession.Tick + EmberSniper.Windup;
    }

    public override void PostUpdate()
    {
        if (charging == null)
            return;
        if (Player.dead || Player.CCed || Player.noItems || Player.selectedItem != slot || Player.HeldItem != charging)
        {
            charging = null;
            return;
        }

        Vector2 origin = Player.RotatedRelativePoint(Player.MountedCenter);
        Vector2 muzzle = Muzzle;
        if (ShotSession.Tick < fireAt)
        {
            var spark = Dust.NewDustPerfect(muzzle, DustID.Torch, Vector2.Zero, Scale: 1.2f);
            spark.noGravity = true;
            return;
        }

        // Владелец создаёт один обычный сетевой снаряд. В ai передаём мировые координаты стрелка.
        Vector2 spawn = Collision.CanHitLine(origin, 1, 1, muzzle, 1, 1) ? muzzle : origin;
        Projectile.NewProjectile(Player.GetSource_ItemUse(charging), spawn, velocity,
            ModContent.ProjectileType<EmberRound>(), damage, knockback, Player.whoAmI, origin.X, origin.Y);
        SoundEngine.PlaySound(SoundID.Item40, muzzle);
        readyAt = ShotSession.Tick + EmberSniper.Cooldown;
        charging = null;
    }
}

/// <summary>Рисует звезду у дула поверх оружия во время подготовки и первые три тика после выстрела.</summary>
public sealed class SniperFlareLayer : PlayerDrawLayer
{
    public override Position GetDefaultPosition() => new AfterParent(PlayerDrawLayers.HeldItem);
    protected override void Draw(ref PlayerDrawSet drawInfo)
    {
        Player player = drawInfo.drawPlayer;
        if (drawInfo.shadow != 0f || player.dead || player.HeldItem.ModItem is not EmberSniper) return;
        var state = player.GetModPlayer<SniperCharge>();
        if (state.FlareScale <= 0f) return;
        var star = ModContent.Request<Texture2D>("helluvia01/Content/Particles/SniperFlare").Value;
        drawInfo.DrawDataCache.Add(new DrawData(star, state.Muzzle + drawInfo.Position - player.position - Main.screenPosition,
            null, Color.White, 0f, star.Size() * .5f, state.FlareScale, SpriteEffects.None, 0));
    }
}
