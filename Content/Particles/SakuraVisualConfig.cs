using System.ComponentModel;
using Terraria.ModLoader.Config;

namespace helluvia01.Content.Particles;

public enum SakuraQuality { Low, Medium, High, Ultra }

public sealed class SakuraVisualConfig : ModConfig
{
    internal const int MaximumParticles = 1000;

    public override ConfigScope Mode => ConfigScope.ClientSide;

    [DefaultValue(SakuraQuality.High)]
    public SakuraQuality Quality = SakuraQuality.High;

    [Range(100, MaximumParticles), Increment(20), DefaultValue(640)]
    public int ParticleLimit = 640;

    [Range(12, 240), Increment(4), DefaultValue(128)]
    public int SpawnBudget = 128;

    [Range(4, 48), DefaultValue(36)]
    public int AuraLimit = 36;

    [DefaultValue(false)]
    public bool Diagnostics;
}
