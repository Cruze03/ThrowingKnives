using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Core.Attributes;
using CSTimer = CounterStrikeSharp.API.Modules.Timers.Timer;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Utils;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Drawing;
using System.Collections.Concurrent;

using CS2_GameHUDAPI;

namespace ThrowingKnives;

public class PluginConfig : BasePluginConfig
{
    [JsonPropertyName("KnifeAmount")]
    public int KnifeAmount { get; set; } = -1;

    [JsonPropertyName("KnifeVelocity")]
    public float KnifeVelocity { get; set; } = 2250.0f;

    [JsonPropertyName("KnifeDamage")]
    public float KnifeDamage { get; set; } = 45.0f;

    [JsonPropertyName("KnifeElasticity")]
    public float KnifeElasticity { get; set; } = 0.2f;

    [JsonPropertyName("KnifeLifetime")]
    public float KnifeLifetime { get; set; } = 5.0f;

    [JsonPropertyName("KnifeTrailTime")]
    public float KnifeTrailTime { get; set; } = 3.0f;

    [JsonPropertyName("KnifeCooldown")]
    public float KnifeCooldown { get; set; } = 3.0f;

    [JsonPropertyName("ConfigVersion")]
    public override int Version { get; set; } = 2;
}

[MinimumApiVersion(342)]
public class Plugin : BasePlugin, IPluginConfig<PluginConfig>
{
    public override string ModuleName => "Throwing Knives";
    public override string ModuleDescription => "Throwing Knives plugin for CS2";
    public override string ModuleAuthor => "Cruze";
    public override string ModuleVersion => "1.0.2";

    public required PluginConfig Config { get; set; } = new();

    // Used for saving player knife cooldown, timers & hud
    private static TimeSpan _playerCooldownDuration = TimeSpan.FromSeconds(3);
    private readonly ConcurrentDictionary<int, DateTime> _playerCooldowns = new();
    private CSTimer?[] _playerCooldownTimers = new CSTimer[65];
    private const int HUD_CHANNEL = 1;
    public static IGameHUDAPI? _hudapi;


    // Used for tracking attacker active weapon when knife was thrown
    private Dictionary<string, uint?> _knivesThrown = new();

    // Used for tracking thrown knife amount
    private Dictionary<int, int> _knivesAvailable = new();

    // Used for trails
    private Dictionary<uint, Vector3> _knivesOldPos = new();

    // Used for thrown knife model
    private static Dictionary<ushort, string> KnifePaths { get; } = new()
    {
        { 42, "weapons/models/knife/knife_default_ct/weapon_knife_default_ct.vmdl" },
        { 59, "weapons/models/knife/knife_default_t/weapon_knife_default_t.vmdl" },
        { 500, "weapons/models/knife/knife_bayonet/weapon_knife_bayonet.vmdl" },
        { 503, "weapons/models/knife/knife_css/weapon_knife_css.vmdl" },
        { 505, "weapons/models/knife/knife_flip/weapon_knife_flip.vmdl" },
        { 506, "weapons/models/knife/knife_gut/weapon_knife_gut.vmdl" },
        { 507, "weapons/models/knife/knife_karambit/weapon_knife_karambit.vmdl" },
        { 508, "weapons/models/knife/knife_m9/weapon_knife_m9.vmdl" },
        { 509, "weapons/models/knife/knife_tactical/weapon_knife_tactical.vmdl" },
        { 512, "weapons/models/knife/knife_falchion/weapon_knife_falchion.vmdl" },
        { 514, "weapons/models/knife/knife_bowie/weapon_knife_bowie.vmdl" },
        { 515, "weapons/models/knife/knife_butterfly/weapon_knife_butterfly.vmdl" },
        { 516, "weapons/models/knife/knife_push/weapon_knife_push.vmdl" },
        { 517, "weapons/models/knife/knife_cord/weapon_knife_cord.vmdl" },
        { 518, "weapons/models/knife/knife_canis/weapon_knife_canis.vmdl" },
        { 519, "weapons/models/knife/knife_ursus/weapon_knife_ursus.vmdl" },
        { 520, "weapons/models/knife/knife_navaja/weapon_knife_navaja.vmdl" },
        { 521, "weapons/models/knife/knife_outdoor/weapon_knife_outdoor.vmdl" },
        { 522, "weapons/models/knife/knife_stiletto/weapon_knife_stiletto.vmdl" },
        { 523, "weapons/models/knife/knife_talon/weapon_knife_talon.vmdl" },
        { 525, "weapons/models/knife/knife_skeleton/weapon_knife_skeleton.vmdl" },
        { 526, "weapons/models/knife/knife_kukri/weapon_knife_kukri.vmdl" }
    };

    public void OnConfigParsed(PluginConfig config)
    {
        Config = config;
        if (config.Version != Config.Version)
        {
            Logger.LogWarning("Configuration version mismatch (Expected: {0} | Current: {1})", Config.Version, config.Version);
        }

        if (Config.KnifeTrailTime > 0)
        {
            RegisterListener<Listeners.OnTick>(OnTick);
        }
        else
        {
            RemoveListener<Listeners.OnTick>(OnTick);
        }

        _playerCooldownDuration = TimeSpan.FromSeconds(Config.KnifeCooldown);
    }

    public override void Load(bool hotReload)
    {
        base.Load(hotReload);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);

        VirtualFunctions.CBaseEntity_TakeDamageOldFunc.Hook(OnTakeDamage, HookMode.Pre);

        if (hotReload)
        {
            if (Config.KnifeAmount == -1) return;

            foreach (var player in Utilities.GetPlayers().Where(p => !p.IsBot && !p.IsHLTV))
            {
                _knivesAvailable[player.Slot] = Config.KnifeAmount;
            }
        }
    }

    public override void Unload(bool hotReload)
    {
        base.Unload(hotReload);
        RemoveListener<Listeners.OnMapStart>(OnMapStart);

        VirtualFunctions.CBaseEntity_TakeDamageOldFunc.Unhook(OnTakeDamage, HookMode.Pre);
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        try
        {
            PluginCapability<IGameHUDAPI> CapabilityCP = new("gamehud:api");
            _hudapi = IGameHUDAPI.Capability.Get();
        }
        catch (Exception ex)
        {
            _hudapi = null;
            Logger.LogWarning($"GameHUD API loading failed. Cooldown HUD will not work. {ex.Message}");
        }

    }

    private HookResult OnTakeDamage(DynamicHook hook)
    {
        var entity = hook.GetParam<CEntityInstance>(0);
        if (entity == null || !entity.IsValid || !entity.DesignerName.Equals("player"))
            return HookResult.Continue;

        var pawn = entity.As<CCSPlayerPawn>();
        if (pawn == null || !pawn.IsValid)
            return HookResult.Continue;

        var player = pawn.OriginalController.Get();
        if (player == null || !player.IsValid)
            return HookResult.Continue;

        var damageInfo = hook.GetParam<CTakeDamageInfo>(1);

        var thrownKnife = damageInfo.Inflictor.Value;

        if (thrownKnife == null || !thrownKnife.IsValid || thrownKnife.DesignerName.Equals("player"))
            return HookResult.Continue;

        if (thrownKnife.Entity == null || !thrownKnife.Entity.Name.StartsWith("tknife_") ||
            !_knivesThrown.TryGetValue(thrownKnife.Entity.Name, out var activeWeapon)
                || activeWeapon == null)
            return HookResult.Continue;

        var hActiveWeapon = (CHandle<CBasePlayerWeapon>?)Activator.CreateInstance(typeof(CHandle<CBasePlayerWeapon>), activeWeapon);

        if (hActiveWeapon == null || !hActiveWeapon.IsValid)
            return HookResult.Continue;

        var attacker = thrownKnife.OwnerEntity.Value?.As<CCSPlayerPawn>();

        if (attacker == null || !attacker.IsValid)
            return HookResult.Continue;

        damageInfo.Inflictor.Raw = attacker.EntityHandle;
        damageInfo.Attacker.Raw = attacker.EntityHandle;
        damageInfo.Ability.Raw = (uint)activeWeapon;
        damageInfo.BitsDamageType = DamageTypes_t.DMG_SLASH;
        damageInfo.Damage = Config.KnifeDamage;

        thrownKnife.AcceptInput("Kill");

        return HookResult.Continue;
    }

    public void OnMapStart(string map) { }

    public void OnTick()
    {
        var knives = Utilities.FindAllEntitiesByDesignerName<CPhysicsPropOverride>("prop_physics_override");

        foreach (var knife in knives)
        {
            if (knife == null || !knife.IsValid || knife.AbsOrigin == null || knife.Entity == null || !knife.Entity.Name.StartsWith("tknife_") || !_knivesOldPos.TryGetValue(knife.Index, out var oldpos))
                continue;

            var knifePos = (Vector3)knife.AbsOrigin;

            if (!ShouldUpdateTrail(knifePos, oldpos)) continue;

            var owner = knife.OwnerEntity.Value?.As<CCSPlayerPawn>();

            if (owner == null || !owner.IsValid)
                continue;

            CreateTrail(knifePos, oldpos, owner.TeamNum == 3 ? Color.Blue : Color.Red, lifetime: Config.KnifeTrailTime);
            _knivesOldPos[knife.Index] = knifePos;
        }
    }

    [ListenerHandler<Listeners.OnPlayerButtonsChanged>]
    public void OnPlayerButtonsChanged(CCSPlayerController player, PlayerButtons pressed, PlayerButtons released)
    {
        CBasePlayerWeapon? activeWeapon;
        if (pressed.HasFlag(PlayerButtons.Attack) &&
        (activeWeapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value) != null && activeWeapon.IsValid &&
            (activeWeapon.DesignerName.Contains("knife") || activeWeapon.DesignerName.Contains("bayonet")))
        {
            ThrowKnife(player, activeWeapon);
        }
    }

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo @info)
    {
        _knivesOldPos.Clear();
        _knivesThrown.Clear();

        if (Config.KnifeAmount == -1) return HookResult.Continue;

        for (int i = 0; i < 65; i++)
        {
            _playerCooldownTimers[i]?.Kill();

            var player = Utilities.GetPlayerFromSlot(i);
            if (player == null || !player.IsValid ||
                player.Connected != PlayerConnectedState.PlayerConnected ||
                player.IsBot || player.IsHLTV) continue;

            _hudapi?.Native_GameHUD_Remove(player, HUD_CHANNEL);

            _knivesAvailable[player.Slot] = Config.KnifeAmount;
        }
        return HookResult.Continue;
    }

    private void ThrowKnife(CCSPlayerController player, CBasePlayerWeapon? activeWeapon)
    {
        var pawn = player.PlayerPawn.Value;

        if (pawn == null) return;
        if (Config.KnifeAmount != -1)
        {
            if (!_knivesAvailable.ContainsKey(player.Slot) || _knivesAvailable[player.Slot] == 0)
            {
                return;
            }
        }

        if (_playerCooldowns.TryGetValue(player.Slot, out var lastTime))
        {
            if (DateTime.UtcNow - lastTime < _playerCooldownDuration)
                return;
        }

        ushort index;

        if (activeWeapon != null && activeWeapon.IsValid)
            index = activeWeapon.AttributeManager.Item.ItemDefinitionIndex;
        else
            index = (ushort)(player.TeamNum == 3 ? 42 : 59);

        if (!KnifePaths.TryGetValue(index, out var modelPath))
        {
            return;
        }

        var entity = Utilities.CreateEntityByName<CPhysicsPropOverride>("prop_physics_override")!;

        string entName = $"tknife_{Server.TickCount}";

        entity.Entity!.Name = entName;

        entity.CBodyComponent!.SceneNode!.Owner!.Entity!.Flags &= ~(uint)(1 << 2);

        entity.SetModel(modelPath);

        entity.DispatchSpawn();

        entity.Elasticity = Config.KnifeElasticity;
        entity.OwnerEntity.Raw = player.PlayerPawn.Raw;

        entity.Collision.CollisionGroup = (byte)CollisionGroup.COLLISION_GROUP_DEFAULT;

        float angleYaw = pawn.EyeAngles.Y * (float)Math.PI / 180f;
        float anglePitch = pawn.EyeAngles.X * (float)Math.PI / 180f;

        Vector3 rotation = (Vector3)pawn.AbsRotation!;

        Vector3 forward = new Vector3(
            (float)(Math.Cos(anglePitch) * Math.Cos(angleYaw)),
            (float)(Math.Cos(anglePitch) * Math.Sin(angleYaw)),
            (float)-Math.Sin(anglePitch)
        );

        float spawnDistance = 64.0f;
        Vector3 spawnPosition = new Vector3(
            pawn.AbsOrigin!.X + forward.X * spawnDistance,
            pawn.AbsOrigin.Y + forward.Y * spawnDistance + 5,
            pawn.AbsOrigin.Z + forward.Z * spawnDistance + 50.0f
        );

        float throwStrength = Config.KnifeVelocity;
        Vector3 velocity = new Vector3(
            forward.X * throwStrength,
            forward.Y * throwStrength,
            forward.Z * throwStrength + 300.0f
        );

        entity.Teleport(spawnPosition, rotation, velocity);

        _knivesOldPos[entity.Index] = spawnPosition;
        _knivesThrown[entName] = activeWeapon?.EntityHandle.Raw ?? null;

        entity.AddEntityIOEvent("Kill", entity, delay: Config.KnifeLifetime);

        int slot = player.Slot;

        _playerCooldowns[slot] = DateTime.UtcNow;
        _playerCooldowns.TryGetValue(slot, out lastTime);

        if (_hudapi != null)
        {
            _playerCooldownTimers[slot] = AddTimer(0.1f, () =>
            {
                if (player == null || !player.IsValid || player.Connected != PlayerConnectedState.PlayerConnected)
                {
                    _playerCooldownTimers[slot]?.Kill();
                    return;
                }
                float cdLeft = Config.KnifeCooldown - (float)(DateTime.UtcNow - lastTime).TotalSeconds;

                if (cdLeft <= 0)
                {
                    _hudapi.Native_GameHUD_Remove(player, HUD_CHANNEL);
                    _playerCooldownTimers[slot]?.Kill();
                    return;
                }

                const float fXEntity = -2.3f;
                const float fYEntity = -3.8f;
                const float fZEntity = 7.0f;
                const int iSize = 54;

                _hudapi.Native_GameHUD_SetParams(player, HUD_CHANNEL, fXEntity, fYEntity, fZEntity, Color.Cyan, iSize, "Verdana", iSize / 7000.0f);
                _hudapi.Native_GameHUD_Show(player, HUD_CHANNEL, $"Cooldown left: {cdLeft:F2}", 0.2f);
            }, TimerFlags.REPEAT);
        }

        if (Config.KnifeAmount == -1) return;

        _knivesAvailable[player.Slot] -= 1;
    }

    public void CreateTrail(Vector3 position, Vector3 endposition, Color color, float width = 1.0f, float lifetime = 3.0f)
    {
        var beam = Utilities.CreateEntityByName<CEnvBeam>("env_beam");
        if (beam == null)
            return;

        beam.Width = width;
        beam.Render = color;
        beam.Teleport(position);
        beam.DispatchSpawn();

        beam.EndPos.X = endposition.X;
        beam.EndPos.Y = endposition.Y;
        beam.EndPos.Z = endposition.Z;
        Utilities.SetStateChanged(beam, "CBeam", "m_vecEndPos");

        beam.AddEntityIOEvent("Kill", beam, delay: lifetime);
    }

    public bool ShouldUpdateTrail(Vector3 position, Vector3 endposition, float minDistance = 5.0f)
    {
        return Distance(position, endposition) > minDistance;
    }

    public float Distance(Vector3 vector1, Vector3 vector2)
    {
        float dx = vector2.X - vector1.X;
        float dy = vector2.Y - vector1.Y;
        float dz = vector2.Z - vector1.Z;

        return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}