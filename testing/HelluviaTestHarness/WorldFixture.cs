using System;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace HelluviaTestHarness;

public sealed class WorldFixture
{
    internal static WorldFixture Current { get; private set; }
    internal Player Player { get; }
    public NPC Target { get; private set; }
    private int hits, damage, deaths, respawns;
    private Vector2 targetCenter;

    public WorldFixture(Player player) => Player = player;

    public void Prepare(JsonObject initial)
    {
        if (!Entry.InTestWorld || Player != Main.LocalPlayer || Current != null || Player.dead)
            throw new InvalidOperationException("World setup needs a living local test player and a fresh fixture.");
        Current = this;
        initial ??= new JsonObject();
        if (initial["position"] != null && initial["spawnOffset"] != null)
            throw new ArgumentException("Specify only one player position.");
        Vector2? center = null;
        if (initial["position"] is JsonNode position)
            center = Coordinates(position);
        if (initial["spawnOffset"] is JsonNode spawnOffset)
            center = new Vector2(Main.spawnTileX * 16 + 8, Main.spawnTileY * 16 - Player.height / 2f) + Coordinates(spawnOffset);
        if (center.HasValue)
        {
            Vector2 topLeft = CheckedTopLeft(center.Value, Player.width, Player.height);
            if (Collision.SolidCollision(topLeft, Player.width, Player.height))
                throw new ArgumentException("The requested player position intersects solid tiles.");
            Player.Teleport(topLeft);
            Player.velocity = Vector2.Zero;
            Player.fallStart = (int)(Player.position.Y / 16f);
        }
        if (initial["health"] != null)
            Player.statLife = Within((int)initial["health"], 1, Player.statLifeMax2, "health");
        if (initial["mana"] != null)
            Player.statMana = Within((int)initial["mana"], 0, Player.statManaMax2, "mana");
        if (initial["target"] is JsonObject spec)
            SpawnTarget(spec);
        Entry.Emit("initial", new { x = Player.Center.X, y = Player.Center.Y, life = Player.statLife, mana = Player.statMana });
    }

    private static int Within(int value, int min, int max, string name) =>
        value >= min && value <= max ? value : throw new ArgumentOutOfRangeException(name, value, $"Expected {min}..{max}.");

    private static Vector2 Coordinates(JsonNode value)
    {
        if (value is not JsonArray { Count: 2 } pair)
            throw new ArgumentException("Expected [x,y] coordinates in pixels.");
        var result = new Vector2((float)pair[0], (float)pair[1]);
        if (!float.IsFinite(result.X) || !float.IsFinite(result.Y))
            throw new ArgumentException("Coordinates must be finite.");
        return result;
    }

    private static Vector2 CheckedTopLeft(Vector2 center, int width, int height)
    {
        Vector2 topLeft = center - new Vector2(width, height) / 2f;
        if (topLeft.X < 160 || topLeft.Y < 160 || topLeft.X + width > (Main.maxTilesX - 10) * 16 ||
            topLeft.Y + height > (Main.maxTilesY - 10) * 16)
            throw new ArgumentOutOfRangeException(nameof(center), "Fixture is outside the playable world.");
        return topLeft;
    }

    private void SpawnTarget(JsonObject spec)
    {
        string name = (string)spec["npc"] ?? "Terraria/BlueSlime";
        int type = name.StartsWith("Terraria/", StringComparison.Ordinal) ? NPCID.Search.GetId(name[9..])
            : ModContent.TryFind<ModNPC>(name, out var modNpc) ? modNpc.Type : -1;
        if (type <= 0 || !ContentSamples.NpcsByNetId.TryGetValue(type, out var sample))
            throw new ArgumentException($"Unknown target NPC {name}.");
        targetCenter = Player.Center + Coordinates(spec["offset"]);
        CheckedTopLeft(targetCenter, sample.width, sample.height);
        int life = Within((int?)spec["health"] ?? sample.lifeMax, 1, int.MaxValue, "target.health");
        int index = NPC.NewNPC(new EntitySource_Misc("HelluviaTestTarget"), (int)targetCenter.X, (int)targetCenter.Y, type);
        if (index >= Main.maxNPCs)
            throw new InvalidOperationException("No free NPC slot for the test target.");
        Target = Main.npc[index];
        Target.Center = targetCenter;
        Target.lifeMax = Target.life = life;
        Target.noGravity = Target.noTileCollide = true;
        Target.knockBackResist = 0;
        Target.damage = Target.defDamage = 0;
        Entry.Emit("target", new { name, type, index, life, x = targetCenter.X, y = targetCenter.Y });
    }

    internal bool RunNpcAI(NPC npc)
    {
        if (Target != npc)
            return true;
        npc.Center = targetCenter;
        npc.velocity = Vector2.Zero;
        return false;
    }

    internal void Hit(NPC npc, string source, int sourceType, int damageDone)
    {
        if (npc != Target)
            return;
        hits++;
        damage += damageDone;
        Entry.Emit("hit", new { npc = npc.whoAmI, npc.life, source, sourceType, damageDone, totalHits = hits, totalDamage = damage });
    }

    public void KillPlayer()
    {
        if (!Entry.InTestWorld || Current != this || Player.dead || Player.difficulty == PlayerDifficultyID.Hardcore)
            throw new InvalidOperationException("Controlled death requires a living, respawnable test player.");
        Player.KillMe(PlayerDeathReason.ByCustomReason(NetworkText.FromLiteral(Player.name + " died in the test.")),
            Player.statLife, 0);
    }

    public void Respawn()
    {
        if (!Entry.InTestWorld || Current != this || !Player.dead)
            throw new InvalidOperationException("Respawn requires a dead test player.");
        // Player.UpdateDead will call Player.Spawn(ReviveFromDeath) on its next normal update.
        Player.respawnTimer = 1;
    }

    internal void RecordDeath()
    {
        deaths++;
        Entry.Emit("death", new { deaths });
    }

    internal void RecordRespawn()
    {
        respawns++;
        Entry.Emit("respawn", new { respawns });
    }

    public double GetMetric(string metric) => TryMetric(metric, out double value) ? value
        : throw new ArgumentException($"Unknown or unavailable fixture metric: {metric}.");

    public bool TryMetric(string metric, out double value)
    {
        value = metric switch
        {
            "life" => Player.statLife,
            "mana" => Player.statMana,
            "dead" => Player.dead ? 1 : 0,
            "deathCount" => deaths,
            "respawnCount" => respawns,
            "player.moveSpeed" => Player.moveSpeed,
            "player.statDefense" => (int)Player.statDefense,
            "player.lifeMax" => Player.statLifeMax2,
            "player.manaMax" => Player.statManaMax2,
            "player.lifeRegen" => Player.lifeRegen,
            "player.x" => Player.Center.X,
            "player.y" => Player.Center.Y,
            "target.life" when Target != null => Target.active ? Math.Max(0, Target.life) : 0,
            "target.hits" when Target != null => hits,
            "target.damage" when Target != null => damage,
            _ => double.NaN
        };
        return !double.IsNaN(value);
    }

    public void Cleanup()
    {
        if (Current != this)
            return;
        if (Target != null && ReferenceEquals(Main.npc[Target.whoAmI], Target))
            Target.active = false;
        Current = null;
    }
}

public sealed class FixtureNpc : GlobalNPC
{
    public override bool PreAI(NPC npc) => WorldFixture.Current?.RunNpcAI(npc) ?? true;
    public override bool CheckActive(NPC npc) => WorldFixture.Current?.Target != npc;
}

public sealed class FixturePlayer : ModPlayer
{
    private WorldFixture Fixture => WorldFixture.Current?.Player == Player ? WorldFixture.Current : null;

    public override void Kill(double damage, int hitDirection, bool pvp, PlayerDeathReason damageSource) =>
        Fixture?.RecordDeath();
    public override void OnRespawn() => Fixture?.RecordRespawn();
    public override void OnHitNPCWithItem(Item item, NPC target, NPC.HitInfo hit, int damageDone) =>
        Fixture?.Hit(target, "item", item.type, damageDone);
    public override void OnHitNPCWithProj(Projectile proj, NPC target, NPC.HitInfo hit, int damageDone) =>
        Fixture?.Hit(target, "projectile", proj.type, damageDone);
}
