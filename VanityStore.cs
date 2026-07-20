using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using LootHero.loot;
using ProjectMage.player;

namespace SaS2VanitySlots;

/// <summary>
/// Per-player, per-character (or per-player global) vanity overrides for the 7 visible equipment
/// slots.
///
/// Each of the 7 visible slots can have:
///   - a vanity lootIdx (the item whose texture is shown instead of the real equipped item), or -1
///   - a naked flag (when true, the slot's texture is hidden, same as the vanilla hideHelm path)
///
/// Keying (mirrors SaS2DevTools' per-player PlayerCheats pattern):
///   - Always namespaced by the owning player's ID (Player.ID, 0 or 1), because playerSaveIdx is
///     only unique within one Player's PlayerSaveList. P1 and P2 each have their own save slot 0,
///     so keying by saveIdx alone collides between local coop players.
///   - By default (PerCharacter mode) the key is "p{playerId}:{saveIdx}", so each player's each
///     save/character has its own vanity set.
///   - When the user toggles "Per Character" off in Mod Options, the key is "p{playerId}:_global",
///     so each player has one shared set across their own saves, but P1 and P2 stay separate.
///
/// Persistence:
///   Stored in the BepInEx config file as one string entry per slot. Each entry is a
///   semicolon-separated list of "key=lootIdx|naked" pairs. This is a mod config, not a save:
///   nothing is written to the player save, so the "Show All Items" toggle in Mod Options can be
///   turned off to go back to discovered-only without persisting any discovery state.
///   Backward compat: keys written before the per-player change have no "p{ID}:" prefix; those
///   are treated as belonging to player 0 (P1), so existing configs keep working unchanged.
///
/// Visual-only: the real <see cref="PlayerEquipment.equippedItem"/>[] and invItem[] are never
/// touched; only <see cref="CharTexture.GetEquipmentXTexture"/> is intercepted.
/// </summary>
internal static class VanityStore
{
    public const int SlotHelm = 0;
    public const int SlotArmor = 1;
    public const int SlotGloves = 2;
    public const int SlotBoots = 3;
    public const int SlotWeapon1 = 4;
    public const int SlotWeapon2 = 5;
    public const int SlotRanged = 6;

    public static readonly int[] VisibleSlots = { 0, 1, 2, 3, 4, 5, 6 };

    public static readonly string[] SlotNames =
    {
        "Helm", "Armor", "Gloves", "Boots", "Weapon 1", "Weapon 2", "Ranged"
    };

    // LootDef.type/subType filter per slot, mirroring LevelInventoryPicker.SetTypeFromLoadoutType.
    public static readonly int[] SlotType = { 0, 0, 0, 0, 1, 1, 2 };
    public static readonly int[] SlotSubType = { 0, 1, 3, 2, -1, -1, -1 };

    private const string GlobalKey = "_global";

    // Player ID prefix used in every per-player key, e.g. "p0:2" = player 1, save slot 2.
    private const string PlayerPrefix = "p";

    // One config entry per slot. Value is a serialized map: "key=li|n;key2=li2|n2".
    private static readonly ConfigEntry<string>[] _slotEntries = new ConfigEntry<string>[7];

    // In-memory cache: slot -> (charKey -> state). Re-parsed when the config string changes.
    private static readonly Dictionary<int, Dictionary<string, VanitySlotState>> _cache =
        new Dictionary<int, Dictionary<string, VanitySlotState>>();

    // Last raw string seen per slot, for change detection.
    private static readonly string[] _lastRaw = new string[7];

    internal struct VanitySlotState
    {
        public int LootIdx; // -1 = no override
        public bool Naked;
    }

    internal static void Init(ConfigFile config)
    {
        for (var i = 0; i < 7; i++)
        {
            var slot = VisibleSlots[i];
            _slotEntries[i] = config.Bind(
                "Vanity - Slots", "Slot_" + slot,
                "",
                new ConfigDescription(
                    "Vanity override for " + SlotNames[i] +
                    " slot. Serialized as p<playerId>=<saveIdx|_global>=lootIdx|naked pairs separated by ;. " +
                    "Managed by the in-game Vanity menu; edit manually at your own risk."));
        }
    }

    /// <summary>
    /// Returns the active character key for a given player's saveIdx under the current mode.
    /// Always namespaced by the player ID so P1 and P2 never collide (each Player has its own
    /// PlayerSaveList with the same 0..5 save indices).
    /// In PerCharacter mode the suffix is the saveIdx; in global mode it is "_global".
    /// </summary>
    public static string CharKey(int playerId, int saveIdx, bool perCharacter)
    {
        var suffix = perCharacter ? saveIdx.ToString() : GlobalKey;
        return PlayerPrefix + playerId + ":" + suffix;
    }

    public static int GetVanityLootIdx(int slot, int playerId, int saveIdx, bool perCharacter)
    {
        var s = GetState(slot, playerId, saveIdx, perCharacter);
        return s.LootIdx;
    }

    public static bool IsNaked(int slot, int playerId, int saveIdx, bool perCharacter)
    {
        var s = GetState(slot, playerId, saveIdx, perCharacter);
        return s.Naked;
    }

    public static void SetVanityLootIdx(int slot, int playerId, int saveIdx, bool perCharacter, int lootIdx)
    {
        var map = GetOrInitSlot(slot);
        var key = CharKey(playerId, saveIdx, perCharacter);
        map[key] = new VanitySlotState { LootIdx = lootIdx, Naked = map.ContainsKey(key) && map[key].Naked };
        SaveSlot(slot);
    }

    public static void ToggleNaked(int slot, int playerId, int saveIdx, bool perCharacter)
    {
        var map = GetOrInitSlot(slot);
        var key = CharKey(playerId, saveIdx, perCharacter);
        VanitySlotState cur;
        if (!map.TryGetValue(key, out cur)) cur = new VanitySlotState { LootIdx = -1 };
        map[key] = new VanitySlotState { LootIdx = cur.LootIdx, Naked = !cur.Naked };
        SaveSlot(slot);
    }

    public static void ClearSlot(int slot, int playerId, int saveIdx, bool perCharacter)
    {
        var map = GetOrInitSlot(slot);
        var key = CharKey(playerId, saveIdx, perCharacter);
        map[key] = new VanitySlotState { LootIdx = -1, Naked = false };
        SaveSlot(slot);
    }

    private static VanitySlotState GetState(int slot, int playerId, int saveIdx, bool perCharacter)
    {
        var map = GetOrInitSlot(slot);
        var key = CharKey(playerId, saveIdx, perCharacter);
        VanitySlotState s;
        return map.TryGetValue(key, out s) ? s : new VanitySlotState { LootIdx = -1 };
    }

    private static Dictionary<string, VanitySlotState> GetOrInitSlot(int slot)
    {
        var i = IndexOf(slot);
        if (i < 0) return new Dictionary<string, VanitySlotState>();
        EnsureParsed(i);
        Dictionary<string, VanitySlotState> map;
        if (!_cache.TryGetValue(slot, out map))
        {
            map = new Dictionary<string, VanitySlotState>();
            _cache[slot] = map;
        }
        return map;
    }

    private static void EnsureParsed(int i)
    {
        var raw = _slotEntries[i] != null ? _slotEntries[i].Value : "";
        if (raw == _lastRaw[i]) return;
        _lastRaw[i] = raw;
        var slot = VisibleSlots[i];
        var map = new Dictionary<string, VanitySlotState>();
        if (!string.IsNullOrEmpty(raw))
        {
            foreach (var pair in raw.Split(';'))
            {
                if (string.IsNullOrEmpty(pair)) continue;
                var eq = pair.IndexOf('=');
                if (eq < 0) continue;
                var key = pair.Substring(0, eq);
                var val = pair.Substring(eq + 1);
                var vp = val.Split('|');
                int li;
                bool n;
                int.TryParse(vp.Length > 0 ? vp[0] : "-1", out li);
                bool.TryParse(vp.Length > 1 ? vp[1] : "0", out n);
                // Backward compat: pre-per-player keys had no "p{ID}:" prefix (a bare saveIdx or
                // "_global"). Treat those as belonging to player 0 (P1) so existing configs keep
                // working without losing the user's saved vanity.
                if (key.IndexOf(':') < 0) key = PlayerPrefix + 0 + ":" + key;
                map[key] = new VanitySlotState { LootIdx = li, Naked = n };
            }
        }
        _cache[slot] = map;
    }

    private static void SaveSlot(int slot)
    {
        var i = IndexOf(slot);
        if (i < 0 || _slotEntries[i] == null) return;
        EnsureParsed(i);
        var map = _cache.ContainsKey(slot) ? _cache[slot] : new Dictionary<string, VanitySlotState>();
        var sb = new StringBuilder();
        var first = true;
        foreach (var kv in map)
        {
            if (kv.Value.LootIdx == -1 && !kv.Value.Naked) continue; // drop empty entries
            if (!first) sb.Append(';');
            first = false;
            sb.Append(kv.Key).Append('=').Append(kv.Value.LootIdx).Append('|')
                .Append(kv.Value.Naked ? 1 : 0);
        }
        var raw = sb.ToString();
        _slotEntries[i].Value = raw;
        _lastRaw[i] = raw;
        _slotEntries[i].ConfigFile.Save();
    }

    private static int IndexOf(int slot)
    {
        for (var i = 0; i < 7; i++) if (VisibleSlots[i] == slot) return i;
        return -1;
    }

    // -- discovery / "known to player" filter --

    /// Material-name lootField indices per LootDef.type, replicating LevelCrafting.GetCraftingMats.
    private static readonly Dictionary<int, int[]> MatFieldByType = new Dictionary<int, int[]>
    {
        { 0, new[] { 7, 9, 11 } },   // armor
        { 1, new[] { 2, 4, 6 } },    // weapon
        { 2, new[] { 2, 4, 6 } },    // ranged
        { 6, new[] { 0, 2, 4 } },    // charm
    };

    // GetItemCount(string, bool) is internal on PlayerEquipment; use AccessTools (Harmony) which
    // considers non-public members, since plain Type.GetMethod(string, Type[]) is public-only and
    // would return null here, silently breaking the "discovered via crafting materials" filter.
    private static readonly MethodInfo _getItemCountMethod =
        AccessTools.Method(typeof(PlayerEquipment), "GetItemCount",
            new[] { typeof(string), typeof(bool) });

    private static int GetItemCount(Player p, string name)
    {
        if (p == null || p.equipment == null || _getItemCountMethod == null || string.IsNullOrEmpty(name))
            return 0;
        try
        {
            return (int)_getItemCountMethod.Invoke(p.equipment, new object[] { name, false });
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Returns true if the player has discovered lootIdx: currently owns it, or owns at least one
    /// of its crafting materials (the same predicate the vanilla crafting menu uses). When
    /// <paramref name="showAll"/> is true, returns true unconditionally.
    /// </summary>
    public static bool IsKnownToPlayer(Player p, int lootIdx, bool showAll)
    {
        if (showAll) return true;
        if (lootIdx < 0 || lootIdx >= LootCatalog.lootDef.Count) return false;
        var lDef = LootCatalog.lootDef[lootIdx];

        for (var i = 0; i < p.equipment.invItem.Count; i++)
        {
            var pi = p.equipment.invItem[i];
            if (!pi.stockPiled && pi.count > 0 && pi.lootIdx == lootIdx) return true;
        }

        int[] fields;
        if (MatFieldByType.TryGetValue(lDef.type, out fields))
        {
            foreach (var fi in fields)
            {
                if (fi >= lDef.lootField.Count) continue;
                var matName = lDef.lootField[fi].strData;
                if (!string.IsNullOrEmpty(matName) && GetItemCount(p, matName) > 0)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds the candidate lootIdx list for a slot, filtered by the slot's type/subType and by
    /// the discovery filter. Sorted by display name for a stable list.
    /// </summary>
    public static List<int> BuildCandidateList(Player p, int slot, bool showAll)
    {
        var i = IndexOf(slot);
        if (i < 0) return new List<int>();
        var type = SlotType[i];
        var subType = SlotSubType[i];

        var result = new List<int>();
        for (var li = 0; li < LootCatalog.lootDef.Count; li++)
        {
            var lDef = LootCatalog.lootDef[li];
            if (lDef.type != type) continue;
            if (subType != -1 && lDef.subType != subType) continue;
            if (!IsKnownToPlayer(p, li, showAll)) continue;
            // Lanterns: type 1 subType 12 are not drawn on the body (vanilla returns null in
            // GetEquipmentXTexture), so exclude them from the vanity picker.
            if (type == 1 && lDef.subType == 12) continue;
            result.Add(li);
        }

        result.Sort((a, b) =>
        {
            var na = LootCatalog.lootDef[a].title.Length > 0
                ? LootCatalog.lootDef[a].title[0]
                : LootCatalog.lootDef[a].name;
            var nb = LootCatalog.lootDef[b].title.Length > 0
                ? LootCatalog.lootDef[b].title[0]
                : LootCatalog.lootDef[b].name;
            return string.Compare(na, nb, StringComparison.Ordinal);
        });
        return result;
    }
}