using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Common;

namespace SaS2VanitySlots;

/// <summary>
/// A rebindable input combo. Holds an optional keyboard combo (modifier key + key) and an
/// optional gamepad combo (modifier button + button); either device may be left unbound.
/// Evaluation is stateless, the caller is responsible for edge detection / throttling.
///
/// Persisted to a <see cref="ConfigEntry{T}"/> of string as "KbMod|KbKey|PadMod|PadButton",
/// where the keyboard parts are <c>Common.Keys</c> names and the gamepad parts are the game's
/// negative button codes (0 / "None" = unset).
/// </summary>
public sealed class Keybind
{
    // Gamepad button codes (match ProjectMage InputProfile / InputMapping conventions).
    public const int PadA = -14, PadB = -15, PadX = -16, PadY = -17;
    public const int PadLb = -20, PadRb = -18, PadLt = -21, PadRt = -19;
    public const int PadLs = -28, PadRs = -29, PadStart = -22, PadBack = -23;
    public const int PadDLeft = -10, PadDRight = -11, PadDUp = -12, PadDDown = -13;

    private const Keys KeyNone = (Keys)0;

    // All capturable gamepad codes, in a stable order.
    public static readonly int[] PadCodes =
    [
        PadRs, PadLs, PadLb, PadRb, PadLt, PadRt, PadStart, PadBack,
        PadA, PadB, PadX, PadY, PadDLeft, PadDRight, PadDUp, PadDDown
    ];

    public Keys KbMod;     // modifier key (Ctrl/Shift/Alt) or KeyNone
    public Keys KbKey;     // main key or KeyNone
    public int PadMod;     // gamepad modifier code or 0
    public int PadButton;  // gamepad button code or 0
    public bool Enabled = true; // when false the bind never fires (toggled in the menu)

    private readonly ConfigEntry<string> _cfg;
    private string _lastRaw;

    /// Binds to a string config entry and loads the stored combo (falling back to the entry default).
    public Keybind(ConfigEntry<string> cfg)
    {
        _cfg = cfg;
        SyncFromConfig();
    }

    public ConfigEntry<string> Config => _cfg;

    /// Re-parse from the backing config when its raw string changed (covers menu rebinds in
    /// another Keybind instance bound to the same entry, and external file reloads).
    private void SyncFromConfig()
    {
        if (_cfg == null) return;
        var raw = _cfg.Value;
        if (raw == _lastRaw) return;
        Parse(raw);
        _lastRaw = raw;
    }

    // -- persistence ----------------------------------------------------------

    private void Parse(string s)
    {
        KbMod = KeyNone;
        KbKey = KeyNone;
        PadMod = 0;
        PadButton = 0;
        Enabled = true;
        if (string.IsNullOrEmpty(s)) return;

        var parts = s.Split('|');
        if (parts.Length >= 1) KbMod = ParseKey(parts[0]);
        if (parts.Length >= 2) KbKey = ParseKey(parts[1]);
        if (parts.Length >= 3) int.TryParse(parts[2], out PadMod);
        if (parts.Length >= 4) int.TryParse(parts[3], out PadButton);
        if (parts.Length >= 5) Enabled = parts[4] != "0";
    }

    private static Keys ParseKey(string s)
    {
        if (string.IsNullOrEmpty(s) || s == "None" || s == "0") return KeyNone;
        try { return (Keys)Enum.Parse(typeof(Keys), s); }
        catch { return KeyNone; }
    }

    private static string KeyToStr(Keys k) => k == KeyNone ? "None" : k.ToString();

    public string Serialize() => $"{KeyToStr(KbMod)}|{KeyToStr(KbKey)}|{PadMod}|{PadButton}|{(Enabled ? 1 : 0)}";

    /// Flip the enabled state and persist.
    public void ToggleEnabled()
    {
        SyncFromConfig();
        Enabled = !Enabled;
        Save();
    }

    public void Save()
    {
        if (_cfg == null) return;
        var s = Serialize();
        _cfg.Value = s;
        _lastRaw = s;
        _cfg.ConfigFile.Save();
    }

    /// Restore the combo to the config entry's registered default and persist it.
    public void ResetToDefault()
    {
        if (_cfg == null) return;
        Parse(_cfg.DefaultValue as string);
        Save();
    }

    // -- evaluation (stateless; caller does edge detection / throttling) -------

    public bool HeldKeyboard(KeyboardState ks)
    {
        SyncFromConfig();
        if (!Enabled || KbKey == KeyNone || !ks.IsKeyDown(KbKey)) return false;
        return KbMod == KeyNone || IsModDown(ks, KbMod);
    }

    public bool HeldGamepad(GamePadState gp)
    {
        SyncFromConfig();
        if (!Enabled || PadButton == 0 || !IsPadDown(gp, PadButton)) return false;
        return PadMod == 0 || IsPadDown(gp, PadMod);
    }

    public bool Held(KeyboardState ks, GamePadState gp) => HeldKeyboard(ks) || HeldGamepad(gp);

    private static bool IsModDown(KeyboardState ks, Keys mod)
    {
        switch (mod)
        {
            case Keys.LeftControl:
            case Keys.RightControl:
                return ks.IsKeyDown(Keys.LeftControl) || ks.IsKeyDown(Keys.RightControl);
            case Keys.LeftShift:
            case Keys.RightShift:
                return ks.IsKeyDown(Keys.LeftShift) || ks.IsKeyDown(Keys.RightShift);
            case Keys.LeftAlt:
            case Keys.RightAlt:
                return ks.IsKeyDown(Keys.LeftAlt) || ks.IsKeyDown(Keys.RightAlt);
            default:
                return ks.IsKeyDown(mod);
        }
    }

    public static bool IsPadDown(GamePadState gp, int code)
    {
        switch (code)
        {
            case PadA: return gp.Buttons.A == ButtonState.Pressed;
            case PadB: return gp.Buttons.B == ButtonState.Pressed;
            case PadX: return gp.Buttons.X == ButtonState.Pressed;
            case PadY: return gp.Buttons.Y == ButtonState.Pressed;
            case PadLb: return gp.Buttons.LeftShoulder == ButtonState.Pressed;
            case PadRb: return gp.Buttons.RightShoulder == ButtonState.Pressed;
            case PadLt: return gp.Triggers.Left > 0.5f;
            case PadRt: return gp.Triggers.Right > 0.5f;
            case PadLs: return gp.Buttons.LeftStick == ButtonState.Pressed;
            case PadRs: return gp.Buttons.RightStick == ButtonState.Pressed;
            case PadStart: return gp.Buttons.Start == ButtonState.Pressed;
            case PadBack: return gp.Buttons.Back == ButtonState.Pressed;
            case PadDLeft: return gp.DPad.Left == ButtonState.Pressed;
            case PadDRight: return gp.DPad.Right == ButtonState.Pressed;
            case PadDUp: return gp.DPad.Up == ButtonState.Pressed;
            case PadDDown: return gp.DPad.Down == ButtonState.Pressed;
            default: return false;
        }
    }

    // -- naming / display -----------------------------------------------------

    public string DisplayString()
    {
        SyncFromConfig();
        string kb = null;
        if (KbKey != KeyNone)
            kb = KbMod == KeyNone ? KeyName(KbKey) : $"{KeyName(KbMod)}+{KeyName(KbKey)}";

        string pad = null;
        if (PadButton != 0)
            pad = PadMod == 0 ? PadName(PadButton) : $"{PadName(PadMod)}+{PadName(PadButton)}";

        var combo = kb != null && pad != null ? $"{kb} / {pad}" : kb ?? pad ?? "Unbound";
        return Enabled ? combo : $"{combo} [off]";
    }

    public static string KeyName(Keys k)
    {
        switch (k)
        {
            case Keys.LeftControl:
            case Keys.RightControl: return "Ctrl";
            case Keys.LeftShift:
            case Keys.RightShift: return "Shift";
            case Keys.LeftAlt:
            case Keys.RightAlt: return "Alt";
            default: return k.ToString();
        }
    }

    public static string PadName(int code)
    {
        switch (code)
        {
            case PadA: return "A";
            case PadB: return "B";
            case PadX: return "X";
            case PadY: return "Y";
            case PadLb: return "LB";
            case PadRb: return "RB";
            case PadLt: return "LT";
            case PadRt: return "RT";
            case PadLs: return "LS";
            case PadRs: return "RS";
            case PadStart: return "Start";
            case PadBack: return "Back";
            case PadDLeft: return "DLeft";
            case PadDRight: return "DRight";
            case PadDUp: return "DUp";
            case PadDDown: return "DDown";
            default: return "?";
        }
    }

    // -- capture --------------------------------------------------------------

    /// <summary>
    /// Stateful capture of a key/button combo across frames. Inputs held when capture starts
    /// (e.g. the Accept key) are ignored until released. The combo is committed only once all
    /// capture inputs are released, so holding a key waits for the rest of the combo instead of
    /// binding immediately. First input pressed becomes the modifier, last becomes the key.
    /// One session sets whichever device the user used; the other device's binding is untouched.
    /// </summary>
    public sealed class Capture
    {
        private readonly HashSet<Keys> _ignoreKb;
        private readonly HashSet<int> _ignorePad;
        private readonly List<Keys> _seqKb = [];
        private readonly HashSet<Keys> _seenKb = [];
        private readonly List<int> _seqPad = [];
        private readonly HashSet<int> _seenPad = [];

        public Capture(KeyboardState ks, GamePadState gp)
        {
            _ignoreKb = new HashSet<Keys>(ks.GetPressedKeys());
            _ignorePad = SnapshotPad(gp);
        }

        /// Poll once per frame. Returns true when the combo is complete (all inputs released),
        /// writing the result into <paramref name="target"/>.
        public bool Poll(KeyboardState ks, GamePadState gp, Keybind target)
        {
            // Stop ignoring pre-held inputs once they have been released.
            _ignoreKb.RemoveWhere(k => !ks.IsKeyDown(k));
            _ignorePad.RemoveWhere(c => !IsPadDown(gp, c));

            var anyHeld = false;

            foreach (var k in ks.GetPressedKeys())
            {
                if (_ignoreKb.Contains(k)) continue;
                anyHeld = true;
                if (_seenKb.Add(k)) _seqKb.Add(k);
            }

            foreach (var c in PadCodes)
            {
                if (!IsPadDown(gp, c) || _ignorePad.Contains(c)) continue;
                anyHeld = true;
                if (_seenPad.Add(c)) _seqPad.Add(c);
            }

            // Wait until something was pressed AND everything has been released.
            if ((_seqKb.Count == 0 && _seqPad.Count == 0) || anyHeld) return false;

            if (_seqKb.Count > 0)
            {
                target.KbMod = KeyNone;
                target.KbKey = _seqKb[_seqKb.Count - 1];
                if (_seqKb.Count >= 2)
                {
                    // Prefer a real modifier (Ctrl/Shift/Alt) regardless of seen order, so a
                    // same-frame Ctrl+A is not inverted (GetPressedKeys is keycode-ordered).
                    var mod = KeyNone;
                    var key = KeyNone;
                    foreach (var k in _seqKb)
                        if (IsKbModifier(k)) mod = k;
                        else key = k;

                    if (mod != KeyNone && key != KeyNone)
                    {
                        target.KbMod = mod;
                        target.KbKey = key;
                    }
                    else
                    {
                        target.KbMod = _seqKb[0];
                        target.KbKey = _seqKb[_seqKb.Count - 1];
                    }
                }
            }

            if (_seqPad.Count > 0)
            {
                target.PadMod = _seqPad.Count >= 2 ? _seqPad[0] : 0;
                target.PadButton = _seqPad[_seqPad.Count - 1];
            }

            return true;
        }
    }

    /// Snapshot of gamepad codes currently down.
    private static HashSet<int> SnapshotPad(GamePadState gp)
    {
        var set = new HashSet<int>();
        foreach (var c in PadCodes)
            if (IsPadDown(gp, c)) set.Add(c);
        return set;
    }

    private static bool IsKbModifier(Keys k) =>
        k is Keys.LeftControl or Keys.RightControl or Keys.LeftShift or Keys.RightShift
            or Keys.LeftAlt or Keys.RightAlt;
}
