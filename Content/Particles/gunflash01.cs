using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ModLoader;
using Terraria.GameContent;
using Terraria.DataStructures;

public class WhiteCrossProjectile : ModProjectile
{
    private const int Duration = 3;
    private const float DistanceFromPlayer = 68f;
    public override string Texture => "helluvia01/Content/Particles/gunflash";
    private const float Shake = 2f;


    public override void SetDefaults()
    {
        Projectile.width = 57;
        Projectile.height = 50;
        Projectile.friendly = true;
        Projectile.hostile = false;
        Projectile.penetrate = -1;
        Projectile.timeLeft = Duration;
        Projectile.tileCollide = false;
        Projectile.ignoreWater = true;
        Projectile.damage = 0; // Поставь нужный урон, если крест должен наносить урон
    }

public override void OnSpawn(IEntitySource source)
{
    // Projectile.spriteDirection = (int)Projectile.ai[0];
     Player player = Main.player[Projectile.owner];

        Vector2 direction = Main.MouseWorld - player.MountedCenter;
        if (direction.LengthSquared() < 0.001f)
            direction = Vector2.UnitX * player.direction;
        else
            direction.Normalize();
        Projectile.scale = 1.1f;

        

        Vector2 center = player.MountedCenter
        + direction * DistanceFromPlayer;;

        Projectile.Center = new Vector2(
            center.X,
            center.Y + Projectile.ai[1]
        );
        Projectile.rotation = direction.ToRotation();
        Projectile.spriteDirection = direction.X >= 0 ? 1 : -1;
}


// public override void OnSpawn(IEntitySource source)
// {
//     // Projectile.spriteDirection = (int)Projectile.ai[0];
//      Player player = Main.player[Projectile.owner];

//         Vector2 direction = Main.MouseWorld - player.MountedCenter;
//         if (direction.LengthSquared() < 0.001f)
//             direction = Vector2.UnitX * player.direction;
//         else
//             direction.Normalize();
//         Projectile.scale = 1.7f;

//         float yOffset = Main.rand.NextFloat(-Shake, Shake);

//         Projectile.Center = player.MountedCenter
//         + direction * DistanceFromPlayer
//         + new Vector2(0f, yOffset);
//         Projectile.rotation = direction.ToRotation();
//         Projectile.spriteDirection = direction.X >= 0 ? 1 : -1;
// }

public override bool PreDraw(ref Color lightColor)
    {
        Texture2D texture =
            ModContent.Request<Texture2D>(Texture).Value;

        Vector2 origin = texture.Size() / 2f;
        SpriteEffects effects = Projectile.spriteDirection == -1
            ? SpriteEffects.FlipVertically
            : SpriteEffects.None;

        Main.EntitySpriteDraw(
            texture,
            Projectile.Center - Main.screenPosition,
            null,
            Color.White,
            Projectile.rotation,
            origin,
            Projectile.scale,
            effects);


             Color glowColor = new Color(255, 235, 180, 0) * 0.18f;
        float glowScale = Projectile.scale * 1.72f;
        Vector2 position = Projectile.Center - Main.screenPosition;
        for (int i = 0; i < 3; i++)
        {
            float scale = 1.2f + i * 0.45f;       // размер ореола
            float opacity = 0.88f - i * 0.24f;    // мягкость/прозрачность

            Main.EntitySpriteDraw(
                texture,
                position,
                null,
                glowColor * opacity,
                Projectile.rotation,
                origin,
                scale,
                effects);
        }


        return false;
    }

    public override void DrawBehind(
    int index,
    System.Collections.Generic.List<int> behindNPCsAndTiles,
    System.Collections.Generic.List<int> behindNPCs,
    System.Collections.Generic.List<int> behindProjectiles,
    System.Collections.Generic.List<int> overPlayers,
    System.Collections.Generic.List<int> overWiresUI)
{
    overPlayers.Add(index);
}
// public override void AI()
//     {
//         Player player = Main.player[Projectile.owner];

//         if (!player.active || player.dead)
//         {
//             Projectile.Kill();
//             return;
//         }

//         // Направление от игрока к курсору
//         Vector2 direction = Main.MouseWorld - player.MountedCenter;
//         if (direction.LengthSquared() < 0.001f)
//             direction = Vector2.UnitX * player.direction;
//         else
//             direction.Normalize();

//         player.direction = direction.X >= 0 ? 1 : -1;
//         player.heldProj = Projectile.whoAmI;
//         player.itemTime = 2;
//         player.itemAnimation = 2;

//         Projectile.direction = player.direction;
//         Projectile.spriteDirection = player.direction;
//         Projectile.rotation = direction.ToRotation();

//         // Положение перед игроком
//         Projectile.Center = player.MountedCenter + direction * 35f;
//         Projectile.timeLeft = 2;
//     }
}