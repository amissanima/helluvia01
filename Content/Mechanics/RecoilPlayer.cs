using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

public class RecoilPlayer : ModPlayer
{
    public float RecoilAngle;

    public override void PostUpdate()
    {
        if (!Player.controlUseItem)
            RecoilAngle = Math.Max(0f, RecoilAngle - MathHelper.ToRadians(1f));
    }
}