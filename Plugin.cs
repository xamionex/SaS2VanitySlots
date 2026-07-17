using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.NET.Common;
using HarmonyLib;

namespace SaS2VanitySlots;

[BepInPlugin(PluginInfo.PluginGuid, PluginInfo.PluginName, PluginInfo.PluginVersion)]
// Depends on SaS2ModOptions for RegisterConfig/RegisterKeybind + the in-game options menu.
[BepInDependency("amione.SaS2ModOptions", BepInDependency.DependencyFlags.SoftDependency)]
// ReSharper disable once ClassNeverInstantiated.Global
public class SaS2VanitySlotsPlugin : BasePlugin
{
    internal static SaS2VanitySlotsPlugin Instance;

    private Harmony _harmony;

    // Config entries
    private static ConfigEntry<bool> _showAllItemsEntry;
    private static ConfigEntry<bool> _perCharacterEntry;
    private static ConfigEntry<string> _nakedKeybindEntry;
    internal static Keybind NakedKeybind;

    public override void Load()
    {
        Instance = this;

        // -- config + Mod Options registration --
        // Per Character: when true (default) each character/save gets its own vanity set, keyed
        // by the save index. When false, a single global set is shared across all characters.
        _perCharacterEntry = Config.Bind(
            "Vanity - General", "PerCharacter", true,
            new ConfigDescription(
                "Store vanity overrides per character (each save has its own set). " +
                "Turn off to use one shared set across all characters."));

        // Show All Items: when true the vanity picker lists every item of the matching type;
        // when false (default) only items the player has discovered (owns or can craft) appear.
        // Toggling it off restores discovered-only filtering without persisting any discovery.
        _showAllItemsEntry = Config.Bind(
            "Vanity - General", "ShowAllItems", false,
            new ConfigDescription(
                "Show all items in the vanity picker (not just discovered/craftable). " +
                "Does not save discovery state; turning it off returns to discovered-only."));

        // Naked toggle keybind: default N on keyboard, Y on gamepad.
        _nakedKeybindEntry = Config.Bind(
            "Vanity - Keybinds", "NakedToggleKeybind",
            $"{Keybind.KeyName(Common.Keys.None)}|N|0|{Keybind.PadY}|1",
            new ConfigDescription("Hold this combo while a slot is selected in the Vanity menu to toggle naked mode for that slot."));
        NakedKeybind = new Keybind(_nakedKeybindEntry);

        VanityStore.Init(Config);

        // Register with SaS2ModOptions so they appear in the in-game Mod Options menu.
        if (SaS2ModOptionsApi.IsAvailable)
        {
            SaS2ModOptionsApi.RegisterConfig(_perCharacterEntry, PluginInfo.PluginName, "Per Character", 0);
            SaS2ModOptionsApi.RegisterConfig(_showAllItemsEntry, PluginInfo.PluginName, "Show All Items", 1);
            SaS2ModOptionsApi.RegisterKeybind(_nakedKeybindEntry, PluginInfo.PluginName, "Naked Toggle", 2);
            Log.LogInfo("Registered with SaS2ModOptions.");
        }
        else
        {
            Log.LogWarning("SaS2ModOptions not present; settings are still editable in the BepInEx config file but not in-game.");
        }

        _harmony = new Harmony(PluginInfo.PluginGuid);
        _harmony.PatchAll();
        Log.LogInfo($"{PluginInfo.PluginName} v{PluginInfo.PluginVersion} loaded.");
    }

    public override bool Unload()
    {
        _harmony?.UnpatchSelf();
        return base.Unload();
    }

    /// Live-read of the ShowAllItems toggle (consumed by VanityStore when building picker lists).
    internal static bool ShowAllItems => _showAllItemsEntry != null && _showAllItemsEntry.Value;

    /// Live-read of the Per Character toggle. When true, vanity is keyed by save index.
    internal static bool PerCharacter => _perCharacterEntry == null || _perCharacterEntry.Value;
}

/// <summary>
/// Thin wrapper over SaS2ModOptions.SaS2ModOptions so this assembly does not hard-crash if that
/// plugin is absent. The methods are resolved by reflection and no-op if the assembly is missing.
/// SaS2ModOptions is a soft dependency: the mod works without it (config file only), and with it the
/// settings show up in the in-game Mod Options menu.
/// </summary>
internal static class SaS2ModOptionsApi
{
    private static readonly bool _available;
    private static readonly Action<ConfigEntryBase, string, string, int> _registerConfig;
    private static readonly Action<ConfigEntry<string>, string, string, int> _registerKeybind;

    static SaS2ModOptionsApi()
    {
        try
        {
            // SaS2ModOptions.SaS2ModOptions is the plugin type; its RegisterConfig/RegisterKeybind
            // are static. Resolve them by reflection so a missing assembly does not prevent load.
            var modOptionsType = Type.GetType("SaS2ModOptions.SaS2ModOptions, amione.SaS2ModOptions");
            if (modOptionsType == null) return;

            var rc = modOptionsType.GetMethod("RegisterConfig",
                new[] { typeof(ConfigEntryBase), typeof(string), typeof(string), typeof(int) });
            var rk = modOptionsType.GetMethod("RegisterKeybind",
                new[] { typeof(ConfigEntry<string>), typeof(string), typeof(string), typeof(int) });
            if (rc == null || rk == null) return;

            _registerConfig = (Action<ConfigEntryBase, string, string, int>)
                Delegate.CreateDelegate(typeof(Action<ConfigEntryBase, string, string, int>), rc);
            _registerKeybind = (Action<ConfigEntry<string>, string, string, int>)
                Delegate.CreateDelegate(typeof(Action<ConfigEntry<string>, string, string, int>), rk);
            _available = true;
        }
        catch
        {
            _available = false;
        }
    }

    public static bool IsAvailable => _available;

    public static void RegisterConfig(ConfigEntryBase entry, string modName, string displayName, int order)
    {
        if (_available) _registerConfig(entry, modName, displayName, order);
    }

    public static void RegisterKeybind(ConfigEntry<string> entry, string modName, string displayName, int order)
    {
        if (_available) _registerKeybind(entry, modName, displayName, order);
    }
}