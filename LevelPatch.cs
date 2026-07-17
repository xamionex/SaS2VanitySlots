using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using ProjectMage;
using ProjectMage.character;
using ProjectMage.player.menu.levels;

namespace SaS2VanitySlots;

/// <summary>
/// Injects a "Vanity" entry into the in-game pause menu (LevelGameMenu) and the main menu
/// (LevelMainMenu), beside Equipment. Mirrors SaS2ModOptions.LevelPatch: a transpiler inserts the
/// icon+label into the menu's dictionary, and a prefix on Update dispatches keyAccept to the
/// <see cref="LevelVanity"/> screen.
/// </summary>
[HarmonyPatch]
public static class LevelPatch
{
    // Icon ID for the Vanity menu entry. ICON_AMULET_RING_DAGGER (20) is a real, rendered sprite
    // (the interface icon grid has no cell beyond 111; using an undefined cell renders a stray
    // sliver). 20 is thematically appropriate for an appearance/cosmetics menu and is not used by
    // any vanilla LevelGameMenu/LevelMainMenu dictionary entry, so it won't collide.
    private const int IconId = 20;

    [HarmonyPatch(typeof(LevelMainMenu), "Update")]
    [HarmonyPrefix]
    // ReSharper disable once InconsistentNaming
    public static bool MainMenu_Update_Prefix(LevelMainMenu __instance, Character character, float frameTime)
    {
        if (!__instance.CanInput() || !__instance.player.keys.keyAccept)
            return true;

        if (!__instance.player.playerSaveList.readComplete ||
            !__instance.player.playerSaveList.readSuccess)
            return true;

        var icons = __instance.icons;
        if (icons == null) return true;

        var selY = __instance.selY;
        if (selY < 0 || selY >= icons.Count) return true;

        var icon = icons[selY];

        switch (icon)
        {
            case IconId:
            {
                __instance.PlayAccept();
                __instance.Deactivate();
                var screen = new LevelVanity(__instance.player, returnToScreen: 25);
                VanityMenuHelper.AddAndActivateScreen(__instance.player, screen);
                return false;
            }
            case 23:
                __instance.PlayAccept();
                Game1.shouldExit = true;
                return false;
            default:
                return true;
        }
    }

    [HarmonyPatch(typeof(LevelMainMenu), "Init")]
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> MainMenu_Init_Transpiler(
        IEnumerable<CodeInstruction> instructions)
    {
        return InsertBeforeKey(instructions.ToList(), 23, IconId, "Vanity");
    }

    [HarmonyPatch(typeof(LevelGameMenu), "Update")]
    [HarmonyPrefix]
    // ReSharper disable once InconsistentNaming
    public static bool GameMenu_Update_Prefix(LevelGameMenu __instance, Character character, float frameTime)
    {
        if (!__instance.CanInput() || !__instance.player.keys.keyAccept)
            return true;

        var icons = __instance.icons;
        var selY = __instance.selY;
        if (icons == null || selY < 0 || selY >= icons.Count)
            return true;

        var icon = icons[selY];

        switch (icon)
        {
            case IconId:
            {
                __instance.PlayAccept();
                __instance.Deactivate();
                var screen = new LevelVanity(__instance.player, returnToScreen: 10);
                VanityMenuHelper.AddAndActivateScreen(__instance.player, screen);
                return false;
            }
            case 11:
                __instance.PlayAccept();
                __instance.Deactivate();
                ((LevelSettings)__instance.player.menu.GetLevelByScreen(54)).Activate(10);
                return false;
            default:
                return true;
        }
    }

    [HarmonyPatch(typeof(LevelGameMenu), "Init")]
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> GameMenu_Init_Transpiler(
        IEnumerable<CodeInstruction> instructions)
    {
        return InsertBeforeKey(instructions.ToList(), 11, IconId, "Vanity");
    }

    private static IEnumerable<CodeInstruction> InsertBeforeKey(
        List<CodeInstruction> codes, int key, int iconId, string label)
    {
        var insertIndex = -1;
        for (var i = 0; i < codes.Count - 1; i++)
        {
            if (codes[i].opcode != OpCodes.Ldloc_0 ||
                codes[i + 1].opcode != OpCodes.Ldc_I4_S || (int)(sbyte)codes[i + 1].operand != key) continue;
            insertIndex = i;
            break;
        }

        if (insertIndex == -1)
        {
            SaS2VanitySlotsPlugin.Instance.Log.LogWarning("Key " + key + " not found, " + label + " not inserted.");
            return codes;
        }

        var newCodes = new List<CodeInstruction>();
        for (var i = 0; i < codes.Count; i++)
        {
            if (i == insertIndex)
            {
                newCodes.Add(new CodeInstruction(OpCodes.Ldloc_0));
                newCodes.Add(new CodeInstruction(OpCodes.Ldc_I4, iconId));
                newCodes.Add(new CodeInstruction(OpCodes.Ldstr, label));
                newCodes.Add(new CodeInstruction(OpCodes.Callvirt,
                    AccessTools.Method(typeof(Dictionary<int, string>), "Add",
                        new[] { typeof(int), typeof(string) })));
            }

            newCodes.Add(codes[i]);
        }

        return newCodes;
    }
}