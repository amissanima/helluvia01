using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using helluvia01.Content.Mechanics;
using helluvia01.Content.Projectiles;

namespace helluvia01.Content.Items;

internal static class SakuraBlade
{
    public const string Path = "helluvia01/Content/Assets/Sakura/Blade";
    public static readonly Vector2 Grip = new(21f, 51f);
    public const float TipAngle = -.378f;
    public const float Length = 135.6f;
}

public sealed class SakuraKatana : ModItem
{
    public override string Texture => SakuraBlade.Path;
    public override void SetStaticDefaults() => ItemID.Sets.ItemsThatAllowRepeatedRightClick[Type] = true;

    public override void SetDefaults()
    {
        Item.width = 54;
        Item.height = 28;
        Item.damage = 150;
        Item.crit = 6; // The player's default 4% brings the unmodified total to 10%.
        Item.DamageType = DamageClass.Melee;
        Item.knockBack = 5f;
        Item.useStyle = ItemUseStyleID.Shoot;
        Item.useTime = Item.useAnimation = 24;
        Item.autoReuse = true;
        Item.noMelee = true;
        Item.noUseGraphic = true;
        Item.shoot = ModContent.ProjectileType<SakuraSwing>();
        Item.shootSpeed = 1f;
        Item.rare = ItemRarityID.Red;
        Item.value = Item.sellPrice(gold: 12);
    }

    public override bool AltFunctionUse(Player player) => true;
    public override bool MeleePrefix() => true;
    public override bool CanUseItem(Player player) =>
        player.ownedProjectileCounts[Item.shoot] == 0 && player.GetModPlayer<SakuraPlayer>().Ready;

    public override bool Shoot(Player player, EntitySource_ItemUse_WithAmmo source, Vector2 position,
        Vector2 velocity, int type, int damage, float knockback)
    {
        if (player.whoAmI == Main.myPlayer)
        {
            int ticks = System.Math.Max(1, player.itemAnimationMax);
            int stroke = player.GetModPlayer<SakuraPlayer>().Begin(player.altFunctionUse == 2, ticks);
            Vector2 aim = velocity.SafeNormalize(new Vector2(player.direction, 0f));
            Projectile.NewProjectile(source, player.MountedCenter, aim, type, damage, knockback,
                player.whoAmI, stroke, ticks, player.GetAdjustedItemScale(Item));
        }
        return false;
    }

    public override void AddRecipes() => CreateRecipe().AddIngredient(ItemID.Katana)
        .AddIngredient(ItemID.CrystalShard, 15).AddIngredient(ItemID.FragmentSolar, 12)
        .AddTile(TileID.LunarCraftingStation).Register();
}
