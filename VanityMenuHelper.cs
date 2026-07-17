using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ProjectMage.player;
using ProjectMage.player.menu;

namespace SaS2VanitySlots;

/// <summary>
/// Helper to inject the Vanity screen into PlayerMenu's level list. Mirrors
/// SaS2ModOptions.ModOptionsMenuHelper: reflects the private <c>PlayerMenu.level</c> list,
/// removes any stale inactive vanity instance, and adds + activates the new one.
/// </summary>
public static class VanityMenuHelper
{
    private static FieldInfo _levelField;

    public static void AddAndActivateScreen(Player player, LevelVanity screen)
    {
        if (_levelField == null)
            _levelField = AccessTools.Field(typeof(PlayerMenu), "level");
        if (_levelField == null)
        {
            SaS2VanitySlotsPlugin.Instance.Log.LogError("PlayerMenu.level field not found.");
            return;
        }

        if (_levelField.GetValue(player.menu) is not List<LevelBase> levelList) return;

        // Remove any existing vanity instance (active or not). A stale instance may carry a
        // different returnScreen (e.g. opened from main menu vs pause menu); reusing it would
        // send Cancel back to the wrong screen, so always replace with the fresh one.
        levelList.RemoveAll(l => l is LevelVanity);

        levelList.Add(screen);
        screen.Activate();
    }
}