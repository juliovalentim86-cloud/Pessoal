using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Reactor;
using Reactor.Networking;
using Reactor.Networking.Attributes;
using UnityEngine;

namespace FamilyRoles;

public enum Role : byte { None, Sheriff, Jester, Hitman, Glutton, Trapper }

[BepInPlugin("family.roles", "FamilyRoles", "1.0.0")]
[BepInDependency(ReactorPlugin.Id)]
[ReactorModFlags(ModFlags.RequireOnAllClients)]
public class Plugin : BasePlugin
{
    public static Dictionary<byte, Role> Roles = new();
    public static List<Vector2> Traps = new();
    public static List<GameObject> TrapVisuals = new();
    public static byte HitmanTarget = 255;
    public static float FrozenUntil;
    public static bool Frozen;
    public static int TrapsLeft = 3;
    public static float LastSheriffKill;

    public override void Load() => new Harmony("family.roles").PatchAll();

    public static Role MyRole =>
        PlayerControl.LocalPlayer != null && Roles.TryGetValue(PlayerControl.LocalPlayer.PlayerId, out var r)
            ? r : Role.None;

    public static void ClearAllState()
    {
        Roles.Clear();
        Traps.Clear();
        foreach (var obj in TrapVisuals)
            if (obj != null) UnityEngine.Object.Destroy(obj);
        TrapVisuals.Clear();

        TrapsLeft = 3;
        HitmanTarget = 255;
        Frozen = false;
        LastSheriffKill = 0f;
    }

    public static void CreateTrapVisual(Vector2 pos)
    {
        var go = new GameObject("FamilyTrapVisual");
        go.transform.position = new Vector3(pos.x, pos.y, 0.5f);
        var sr = go.AddComponent<SpriteRenderer>();
        
        var tex = new Texture2D(32, 32);
        for (int x = 0; x < 32; x++)
        {
            for (int y = 0; y < 32; y++)
            {
                float dist = Vector2.Distance(new Vector2(x, y), new Vector2(16, 16));
                tex.SetPixel(x, y, dist < 14f ? new Color(1f, 0.1f, 0.1f, 0.55f) : Color.clear);
            }
        }
        tex.Apply();
        sr.sprite = Sprite.Create(tex, new Rect(0, 0, 32, 32), new Vector2(0.5f, 0.5f), 40f);
        TrapVisuals.Add(go);
    }
}

public static class Rpcs
{
    [MethodRpc(1)]
    public static void RpcSetRole(this PlayerControl pc, byte id, byte role)
    {
        if (role == 0 && id == 255)
        {
            Plugin.ClearAllState();
            return;
        }
        Plugin.Roles[id] = (Role)role;
    }

    [MethodRpc(2)]
    public static void RpcSetTarget(this PlayerControl pc, byte id) => Plugin.HitmanTarget = id;

    [MethodRpc(3)]
    public static void RpcEat(this PlayerControl pc, byte bodyOwnerId)
    {
        var bodies = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<DeadBody>());
        foreach (var obj in bodies)
        {
            var body = obj.TryCast<DeadBody>();
            if (body != null && body.ParentId == bodyOwnerId)
                UnityEngine.Object.Destroy(body.gameObject);
        }
    }

    [MethodRpc(4)]
    public static void RpcTrap(this PlayerControl pc, float x, float y)
    {
        var pos = new Vector2(x, y);
        Plugin.Traps.Add(pos);
        if (Plugin.MyRole == Role.Trapper)
            Plugin.CreateTrapVisual(pos);
    }

    [MethodRpc(5)]
    public static void RpcRemoveTrap(this PlayerControl pc, int index)
    {
        if (index >= 0 && index < Plugin.Traps.Count)
            Plugin.Traps.RemoveAt(index);

        if (index >= 0 && index < Plugin.TrapVisuals.Count)
        {
            if (Plugin.TrapVisuals[index] != null)
                UnityEngine.Object.Destroy(Plugin.TrapVisuals[index]);
            Plugin.TrapVisuals.RemoveAt(index);
        }
    }
}

[HarmonyPatch(typeof(RoleManager), nameof(RoleManager.SelectRoles))]
public static class AssignRoles
{
    public static void Postfix()
    {
        if (!AmongUsClient.Instance.AmHost) return;
        var me = PlayerControl.LocalPlayer;
        me.RpcSetRole(255, 0);

        var all = PlayerControl.AllPlayerControls.ToArray()
            .Where(p => p != null && p.Data != null && !p.Data.Disconnected).ToList();

        var imps = all.Where(p => p.Data.Role != null && p.Data.Role.IsImpostor)
            .OrderBy(_ => UnityEngine.Random.value).ToList();

        var crew = all.Where(p => p.Data.Role != null && !p.Data.Role.IsImpostor)
            .OrderBy(_ => UnityEngine.Random.value).ToList();

        var impRoles = new[] { Role.Hitman, Role.Glutton, Role.Trapper }
            .OrderBy(_ => UnityEngine.Random.value).ToArray();

        for (int i = 0; i < imps.Count; i++)
            me.RpcSetRole(imps[i].PlayerId, (byte)impRoles[i % impRoles.Length]);

        if (crew.Count > 0) me.RpcSetRole(crew.PlayerId, (byte)Role.Sheriff);
        if (crew.Count > 1) me.RpcSetRole(crew.PlayerId, (byte)Role.Jester);

        NewHitmanTarget(crew);
    }

    public static void NewHitmanTarget(List<PlayerControl> crew = null)
    {
        crew ??= PlayerControl.AllPlayerControls.ToArray()
            .Where(p => p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected && p.Data.Role != null && !p.Data.Role.IsImpostor)
            .ToList();

        if (crew.Count == 0)
        {
            PlayerControl.LocalPlayer.RpcSetTarget(255);
            return;
        }

        var chosen = crew[UnityEngine.Random.Range(0, crew.Count)];
        PlayerControl.LocalPlayer.RpcSetTarget(chosen.PlayerId);
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
public static class HitmanKill
{
    public static void Postfix(PlayerControl __instance, PlayerControl target)
    {
        if (__instance == null || target == null || !__instance.AmOwner) return;
        if (Plugin.Roles.GetValueOrDefault(__instance.PlayerId) != Role.Hitman) return;

        if (target.PlayerId == Plugin.HitmanTarget)
        {
            __instance.SetKillTimer(5f);
            AssignRoles.NewHitmanTarget();
        }
        else
        {
            __instance.SetKillTimer(45f);
        }
    }
}

[HarmonyPatch(typeof(ExileController), nameof(ExileController.WrapUp))]
public static class JesterWin
{
    public static void Postfix(ExileController __instance)
    {
        var exiled = __instance.initData?.networkedPlayer;
        if (exiled == null || !AmongUsClient.Instance.AmHost) return;

        if (Plugin.Roles.GetValueOrDefault(exiled.PlayerId) == Role.Jester)
            GameManager.Instance.RpcEndGame(GameOverReason.ImpostorByVote, false);
    }
}

[HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
public static class Abilities
{
    static PlayerControl Nearest(float range)
    {
        var me = PlayerControl.LocalPlayer;
        return PlayerControl.AllPlayerControls.ToArray()
            .Where(p => p != null && p != me && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected &&
                        Vector2.Distance(p.GetTruePosition(), me.GetTruePosition()) < range)
            .OrderBy(p => Vector2.Distance(p.GetTruePosition(), me.GetTruePosition()))
            .FirstOrDefault();
    }

    static string GetRoleDisplay(Role role)
    {
        switch (role)
        {
            case Role.Sheriff:
                float cd = Mathf.Max(0f, 15f - (Time.time - Plugin.LastSheriffKill));
                string status = cd > 0f ? $" ({Mathf.CeilToInt(cd)}s)" : " [Q: Atirar]";
                return $"<color=#FFD700>Sheriff{status}</color>";

            case Role.Jester:
                return "<color=#FF69B4>Jester (Seja votado!)</color>";

            case Role.Hitman:
                string targetName = "Nenhum";
                if (Plugin.HitmanTarget != 255 && GameData.Instance != null)
                {
                    var info = GameData.Instance.GetPlayerById(Plugin.HitmanTarget);
                    if (info != null) targetName = info.PlayerName;
                }
                return $"<color=#FF3333>Hitman (Alvo: {targetName})</color>";

            case Role.Glutton:
                return "<color=#FF3333>Glutao [Q: Comer Corpo]</color>";

            case Role.Trapper:
                return $"<color=#FF3333>Armadilheiro [Q: Armadilha ({Plugin.TrapsLeft})]</color>";

            default:
                return "";
        }
    }

    public static void Postfix()
    {
        var me = PlayerControl.LocalPlayer;
        if (me == null || me.Data == null || ShipStatus.Instance == null) return;

        var role = Plugin.MyRole;
        if (me.cosmetics != null && me.cosmetics.nameText != null)
        {
            string roleText = GetRoleDisplay(role);
            string frozenText = Plugin.Frozen ? "\n<color=#00FFFF><b>CONGELADO!</b></color>" : "";
            if (!string.IsNullOrEmpty(roleText))
                me.cosmetics.nameText.text = $"{me.Data.PlayerName}\n<size=65%>{roleText}</size>{frozenText}";
        }

        if (!me.Data.IsDead && role != Role.Trapper)
        {
            for (int i = Plugin.Traps.Count - 1; i >= 0; i--)
            {
                if (Vector2.Distance(me.GetTruePosition(), Plugin.Traps[i]) < 0.65f)
                {
                    me.RpcRemoveTrap(i);
                    Plugin.FrozenUntil = Time.time + 3f;
                    Plugin.Frozen = true;
                    break;
                }
            }
        }

        if (Plugin.Frozen)
        {
            if (Time.time < Plugin.FrozenUntil)
            {
                me.moveable = false;
            }
            else
            {
                me.moveable = true;
                Plugin.Frozen = false;
            }
        }

        if (me.Data.IsDead || Plugin.Frozen || !Input.GetKeyDown(KeyCode.Q)) return;

        switch (role)
        {
            case Role.Sheriff:
                if (Time.time - Plugin.LastSheriffKill < 15f) break;
                var target = Nearest(1.8f);
                if (target == null) break;

                Plugin.LastSheriffKill = Time.time;
            bool isEvil = (target.Data.Role != null && target.Data.Role.IsImpostor) ||
Plugin.Roles.GetValueOrDefault(target.PlayerId) == Role.Jester;
if (isEvil)
me.RpcMurderPlayer(target, true);
else
me.RpcMurderPlayer(me, true);
break;
case Role.Glutton:
var bodies = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of());
foreach (var obj in bodies)
{
var body = obj.TryCast();
if (body != null && Vector2.Distance(body.TruePosition, me.GetTruePosition()) < 1.5f)
{
me.RpcEat(body.ParentId);
break;
}
}
break;
case Role.Trapper:
if (Plugin.TrapsLeft > 0)
{
Plugin.TrapsLeft--;
var pos = me.GetTruePosition();
me.RpcTrap(pos.x, pos.y);
}
break;
}
}
}
