using System;
using System.Reflection;
using Cartographer.TextureSheet;
using HarmonyLib;
using LootHero.loot;
using ProjectMage.character;
using ProjectMage.character.draw;
using ProjectMage.player;
using ProjectMage.texturesheet;
using Skellingtons.character.draw;

namespace SaS2VanitySlots;

/// <summary>
/// Visual-only equipment override. Patches <see cref="CharTexture.GetEquipmentXTexture"/> and
/// its playerSave-backed twin <see cref="CharTexture.GetPlayerSaveEquipmentXTexture"/> (the two
/// chokepoints that resolve an equipment slot to the body sprite) so the returned texture comes
/// from the vanity store instead of the real equipped item.
///
/// This mirrors the vanilla <c>hideHelm</c> precedent: the texture fetch is nullified or
/// replaced at this layer, while <c>PlayerEquipment.equippedItem[]</c>/<c>invItem[]</c> (and thus
/// all stats, weight, requirements, animations) are left untouched.
///
/// Local-only: the patch keys off <c>character.playerSaveIdx</c>, which is the local player's
/// save slot. Remote/ coop players are drawn from their own (real) equipment, so vanity is only
/// seen by the local player. Syncing vanity to other players would require network packets and
/// is intentionally out of scope for v1.
/// </summary>
[HarmonyPatch]
public static class VanityPatch
{
    private static FieldInfo _charTextureCharacterField;
    private static FieldInfo _charTextureCharDrawField;

    private static bool Prepare()
    {
        _charTextureCharacterField = AccessTools.Field(typeof(CharTexture), "character");
        _charTextureCharDrawField = AccessTools.Field(typeof(CharTexture), "charDraw");
        return _charTextureCharacterField != null;
    }

    /// <summary>
    /// Prefix on <see cref="CharTexture.GetEquipmentXTexture"/>. Returns false (skip original)
    /// only when we actually substitute the result; otherwise returns true to let vanilla run.
    /// </summary>
    [HarmonyPatch(typeof(CharTexture), nameof(CharTexture.GetEquipmentXTexture))]
    [HarmonyPrefix]
    // ReSharper disable once InconsistentNaming
    public static bool GetEquipmentXTexturePrefix(CharTexture __instance, int equipment, Player p,
        ref XTexture __result)
    {
        if (p == null) return true;

        // Replicate the slot remapping vanilla does at the top of GetEquipmentXTexture
        // (CharTexture.cs:178-192), because that remap runs INSIDE the original method body and
        // our prefix runs before it. Without this, calls like GetEquipmentXTexture(31, ...) and
        // (32, ...) for the held weapon / ranged would bypass vanity entirely.
        var resolved = ResolveSlotCode(__instance, equipment, p);
        if (resolved < 0) return true; // not a vanity-managed slot (e.g. cosmetic >= 100)

        return TrySubstitute(__instance, resolved, p, ref __result);
    }

    /// <summary>
    /// Prefix on the playerSave-backed variant. Same logic; only applies when the save being
    /// drawn is the local player's own active save, so preview/character-select screens show the
    /// real save equipment unless it is the active local character.
    /// </summary>
    [HarmonyPatch(typeof(CharTexture), "GetPlayerSaveEquipmentXTexture")]
    [HarmonyPrefix]
    // ReSharper disable once InconsistentNaming
    public static bool GetPlayerSaveEquipmentXTexturePrefix(CharTexture __instance, int equipment,
        Player p, ref XTexture __result)
    {
        if (p == null) return true;

        // GetPlayerSaveEquipmentXTexture is only ever called with an already-remapped slot code
        // (0..6, or >=100 cosmetic) because GetEquipmentXTexture remaps before delegating here.
        // Still guard against out-of-range just in case.
        if (equipment < 0 || equipment > 6) return true;

        // Only apply vanity to the local player's own active save. Other saves (previewed in the
        // character list) show their real equipment.
        if (!p.isLocal) return true;

        var character = GetCharacter(__instance);
        var saveIdx = character != null ? character.playerSaveIdx : -1;
        var activeIdx = p.playerSaveList != null ? p.playerSaveList.activeDataIdx : -1;
        if (saveIdx != activeIdx) return true;

        return TrySubstitute(__instance, equipment, p, ref __result);
    }

    /// <summary>
    /// Mirrors the remap in <see cref="CharTexture.GetEquipmentXTexture"/> lines 178-192:
    /// 31 -> 6 (ranged) if charDraw.rangedDraw, else 32 (weapon); 4 and 32 -> active weapon slot.
    /// Returns the canonical 0..6 body slot, or -1 for cosmetic codes (>=100) we don't manage.
    /// </summary>
    private static int ResolveSlotCode(CharTexture instance, int equipment, Player p)
    {
        if (equipment == 31)
        {
            var rangedDraw = false;
            if (_charTextureCharDrawField != null && _charTextureCharDrawField.GetValue(instance) is CharDraw cd)
                rangedDraw = cd.rangedDraw;
            equipment = rangedDraw ? 6 : 32;
        }
        if (equipment == 4 || equipment == 32)
        {
            // GetWeaponSlotIdx is public; returns 4 or 5 (the active weapon slot).
            return p != null && p.equipment != null ? p.equipment.GetWeaponSlotIdx() : -1;
        }
        if (equipment < 0 || equipment > 6) return -1; // >=100 cosmetic etc.
        return equipment;
    }

    private static Character GetCharacter(CharTexture instance)
    {
        return _charTextureCharacterField != null
            ? (Character)_charTextureCharacterField.GetValue(instance)
            : null;
    }

    /// <summary>
    /// Shared substitution: naked -> null, vanity lootIdx -> its texture, else fall through.
    /// Returns true to let vanilla run; false (with __result set) to skip the original.
    /// </summary>
    private static bool TrySubstitute(CharTexture instance, int equipment, Player p, ref XTexture __result)
    {
        var character = GetCharacter(instance);
        var saveIdx = character != null ? character.playerSaveIdx : -1;
        var perCharacter = SaS2VanitySlotsPlugin.PerCharacter;

        // Naked slot: hide the visual entirely (same as hideHelm returning null for the helm).
        if (VanityStore.IsNaked(equipment, saveIdx, perCharacter))
        {
            __result = null;
            return false;
        }

        var vanityLootIdx = VanityStore.GetVanityLootIdx(equipment, saveIdx, perCharacter);
        if (vanityLootIdx < 0 || vanityLootIdx >= LootCatalog.lootDef.Count) return true;

        var lDef = LootCatalog.lootDef[vanityLootIdx];
        if (lDef == null || string.IsNullOrEmpty(lDef.texture)) return true;

        // Lanterns (type 1 subType 12) are not drawn on the body in vanilla; respect that.
        if (lDef.type == 1 && lDef.subType == 12)
        {
            __result = null;
            return false;
        }

        // Resolve the vanity texture exactly like the vanilla path (CharTexture.cs:247-256):
        // cache texIdx/tex2Idx on the LootDef, pick the female variant when cosmetic[0]==1.
        if (lDef.texIdx == -1)
        {
            lDef.texIdx = Textures.GetTextureIdx(lDef.texture);
            lDef.tex2Idx = Textures.GetTextureIdxOrNegative(lDef.texture + "_f");
        }
        if (lDef.texIdx < 0 || lDef.texIdx >= Textures.tex.Length)
        {
            // Vanity item has no resolvable texture: fall back to the real equipped item.
            return true;
        }

        var tex = Textures.tex[lDef.texIdx];
        if (p.cosmetic != null && p.cosmetic.Length > 0 && p.cosmetic[0] == 1 && lDef.tex2Idx > -1)
            tex = Textures.tex[lDef.tex2Idx];

        __result = tex;
        return false;
    }
}