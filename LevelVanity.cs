using System;
using System.Collections.Generic;
using System.Text;
using Common;
using HarmonyLib;
using LootHero.loot;
using Menumancer.hud;
using Menumancer.UIFormat;
using ProjectMage;
using ProjectMage.character;
using ProjectMage.config;
using ProjectMage.gamestate;
using ProjectMage.player;
using ProjectMage.player.menu;
using ProjectMage.player.menu.levels;

namespace SaS2VanitySlots;

/// <summary>
/// The Vanity menu. A <see cref="LevelBase"/> subclass that draws its own panel (mirroring the
/// SaS2ModOptions menu style) listing the 7 visible equipment slots. Selecting a slot opens an
/// inline item picker for that slot's type; the naked-toggle keybind hides the slot visually.
///
/// The menu is added to the in-game pause menu and the main menu via <see cref="LevelPatch"/>.
/// It returns to the caller (screen 10 = LevelGameMenu, or 25 = LevelMainMenu) on Cancel.
/// </summary>
public class LevelVanity : LevelBase
{
    private enum Mode
    {
        SlotList,
        ItemPicker
    }

    private const float ItemHeight = 40f;
    private const float TopMargin = 40f;
    private const float BottomMargin = 40f; // space kept clear under the last item, above the control tips
    private const float ValueWidth = 240f;

    // The control tips keep the screen position they had before the box was made taller to cover tips
    private const float OldBoxHeightFactor = 0.7f;
    private const float TipsOffsetY = 25f;

    private readonly int _returnScreen;
    private Mode _mode = Mode.SlotList;

    private int _selectedSlotIndex; // index into VanityStore.VisibleSlots
    private int _selectedItemIndex; // index into _candidateList (ItemPicker mode)
    private float _scrollOffset;
    private float _currentListVisibleHeight;
    private float _currentScale = 1f;

    private List<int> _candidateList; // lootIdx candidates for the selected slot
    private bool _candidatesShowAll; // showAll snapshot at build time

    // UI geometry recorded during Draw for mouse hit-testing.
    private struct ItemHit
    {
        public int Index;
        public Rectangle Rect;
    }

    private readonly List<ItemHit> _itemHitRects = new List<ItemHit>();

    // 10 = LevelGameMenu, 25 = LevelMainMenu
    public LevelVanity(Player player, int returnToScreen = 10)
    {
        this.player = player;
        _returnScreen = returnToScreen;
        Init("Vanity", player);
    }

    public sealed override void Init(string strScreen, Player plr)
    {
        base.Init(strScreen, plr);
        if (!screen.uiFlag.Contains(9)) screen.uiFlag.Add(9);
    }

    public override void Activate()
    {
        _mode = Mode.SlotList;
        _selectedSlotIndex = 0;
        _selectedItemIndex = 0;
        _scrollOffset = 0f;
        _nakedHeldLastFrame = false; // clear edge-detection state from a prior menu session
        base.Activate();
    }

    // -- helpers --

    private int CurrentSlot => VanityStore.VisibleSlots[_selectedSlotIndex];

    // Owning player's ID (0 = P1, 1 = P2). Vanity is keyed per-player so P1 and P2 keep separate
    // sets even when their characters share the same save slot index.
    private int PlayerId => player.ID;

    private int SaveIdx
    {
        get
        {
            var c = (ProjectMage.character.Character)
                AccessTools.Method(typeof(Player), "GetCharacter")?.Invoke(player, null);
            return c != null ? c.playerSaveIdx : -1;
        }
    }

    private GamePadState GetPlayerGamePad()
    {
        var idx = player.inputProfile != null ? player.inputProfile.gamepadIdx : -1;
        if (idx < 0) idx = 0;
        var gps = GlobalInputMgr.gps;
        return gps != null && idx < gps.Length ? gps[idx] : default;
    }

    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));

    // Moves the selection one row up or down. At either end the move only happens when the matching
    // Wrap Up / Wrap Down setting is on; otherwise the selection stays put.
    private static bool StepSelection(ref int index, int count, bool up)
    {
        if (up)
        {
            if (index > 0) index--;
            else if (SaS2VanitySlotsPlugin.WrapUp) index = count - 1;
            else return false;
        }
        else
        {
            if (index < count - 1) index++;
            else if (SaS2VanitySlotsPlugin.WrapDown) index = 0;
            else return false;
        }

        return true;
    }

    public override void Update(Character character, float frameTime)
    {
        if (!CanInput()) return;

        if (HandleMouseInput()) return;

        if (_mode == Mode.ItemPicker)
        {
            UpdateItemPicker(character);
            return;
        }

        UpdateSlotList(character);
    }

    private void UpdateSlotList(Character character)
    {
        if (player.keys.keyUp || player.keys.keyDown)
        {
            if (!StepSelection(ref _selectedSlotIndex, VanityStore.VisibleSlots.Length, player.keys.keyUp)) return;
            PlaySelect();
            EnsureVisible();
            return;
        }

        var slot = CurrentSlot;
        var perChar = SaS2VanitySlotsPlugin.PerCharacter;

        // Open the item picker for this slot.
        if (player.keys.keyAccept)
        {
            BuildCandidates();
            _mode = Mode.ItemPicker;
            _selectedItemIndex = 0;
            _scrollOffset = 0f;
            // Pre-select the current vanity item if it is in the list.
            var cur = VanityStore.GetVanityLootIdx(slot, PlayerId, SaveIdx, perChar);
            if (cur >= 0 && _candidateList != null)
            {
                var idx = _candidateList.IndexOf(cur);
                if (idx >= 0) _selectedItemIndex = idx;
            }
            PlayAccept();
            return;
        }

        // Naked toggle keybind.
        if (SaS2VanitySlotsPlugin.NakedKeybind != null &&
            SaS2VanitySlotsPlugin.NakedKeybind.Held(GlobalInputMgr.ks, GetPlayerGamePad()))
        {
            // Edge-detect: only fire on the frame the keybind transitions to held.
            if (!_nakedHeldLastFrame)
            {
                VanityStore.ToggleNaked(slot, PlayerId, SaveIdx, perChar);
                PlaySelect();
            }
            _nakedHeldLastFrame = true;
            return;
        }
        _nakedHeldLastFrame = false;

        // Clear slot (reset to no override, not naked).
        if (player.keys.keyXOption)
        {
            VanityStore.ClearSlot(slot, PlayerId, SaveIdx, perChar);
            PlaySelect();
            return;
        }

        if (player.keys.keyCancel)
        {
            PlayCancel();
            Deactivate();
            player.menu.GetLevelByScreen(_returnScreen).Activate();
        }
    }

    private bool _nakedHeldLastFrame;

    private void UpdateItemPicker(Character character)
    {
        if (_candidateList == null || _candidateList.Count == 0)
        {
            if (player.keys.keyCancel || player.keys.keyAccept)
            {
                PlayCancel();
                ReturnToSlotList();
            }
            return;
        }

        if (player.keys.keyCancel)
        {
            PlayCancel();
            ReturnToSlotList();
            return;
        }

        if (player.keys.keyUp || player.keys.keyDown)
        {
            if (!StepSelection(ref _selectedItemIndex, _candidateList.Count, player.keys.keyUp)) return;
            PlaySelect();
            EnsureVisible();
            return;
        }

        if (player.keys.keyAccept)
        {
            var lootIdx = _candidateList[_selectedItemIndex];
            VanityStore.SetVanityLootIdx(CurrentSlot, PlayerId, SaveIdx, SaS2VanitySlotsPlugin.PerCharacter, lootIdx);
            PlayAccept();
            ReturnToSlotList();
            return;
        }

        // X-option in the picker clears the vanity override for this slot.
        if (player.keys.keyXOption)
        {
            VanityStore.ClearSlot(CurrentSlot, PlayerId, SaveIdx, SaS2VanitySlotsPlugin.PerCharacter);
            PlaySelect();
            ReturnToSlotList();
        }
    }

    private void BuildCandidates()
    {
        _candidatesShowAll = SaS2VanitySlotsPlugin.ShowAllItems;
        _candidateList = VanityStore.BuildCandidateList(player, CurrentSlot, _candidatesShowAll);
    }

    // Leaves the item picker and returns to the slot list. The picker list is long, so its scroll offset is reset:
    // keeping it would draw the seven slot rows far above the visible area, looking like an empty menu.
    private void ReturnToSlotList()
    {
        _mode = Mode.SlotList;
        _scrollOffset = 0f;
    }

    // -- mouse support (mirrors SaS2ModOptions.LevelModOptions) --

    // MouseMgr latches clicks and wheel notches until EndDraw, which runs once per rendered frame while this handler runs once per Update.
    // Consuming the latch as soon as an action is taken keeps one press or one notch to a single action, matching vanilla queueClick semantics.
    private bool HandleMouseInput()
    {
        if (player.ID != 0 || !MouseMgr.isActive) return false;

        var moved = MouseMgr.moveFrame > 0f;
        var clickActive = GameStateManager.activeFocus;
        var leftClick = clickActive && MouseMgr.isLeftClick;
        var rightClick = clickActive && MouseMgr.isRightClick;
        var mouse = MouseMgr.mLoc;

        var list = _mode == Mode.ItemPicker ? _candidateList : null;
        var count = _mode == Mode.ItemPicker
            ? (_candidateList != null ? _candidateList.Count : 0)
            : VanityStore.VisibleSlots.Length;

        if (count == 0) return false;

        if (MouseMgr.isScrollUp || MouseMgr.isScrollDown)
        {
            var up = MouseMgr.isScrollUp;
            // The latches are consumed before moving so one notch steps exactly one row.
            MouseMgr.isScrollUp = false;
            MouseMgr.isScrollDown = false;
            if (_mode == Mode.ItemPicker)
            {
                if (!StepSelection(ref _selectedItemIndex, count, up)) return true;
            }
            else
            {
                if (!StepSelection(ref _selectedSlotIndex, count, up)) return true;
            }

            PlaySelect();
            EnsureVisible();
            return true;
        }

        foreach (var hit in _itemHitRects)
        {
            if (!PointInRect(mouse, hit.Rect)) continue;
            if (!moved && !leftClick && !rightClick) return false;

            if (moved)
            {
                if (_mode == Mode.ItemPicker)
                {
                    if (hit.Index != _selectedItemIndex) { _selectedItemIndex = hit.Index; PlaySelect(); }
                }
                else
                {
                    if (hit.Index != _selectedSlotIndex) { _selectedSlotIndex = hit.Index; PlaySelect(); }
                }
            }

            if (!leftClick && !rightClick) return false;

            // Any click from here on is acted upon, so consume the latched press: one press is one action.
            ConsumeClick(leftClick, rightClick);

            if (_mode == Mode.ItemPicker)
            {
                if (hit.Index != _selectedItemIndex) _selectedItemIndex = hit.Index;
                if (leftClick)
                {
                    VanityStore.SetVanityLootIdx(CurrentSlot, PlayerId, SaveIdx, SaS2VanitySlotsPlugin.PerCharacter,
                        _candidateList[_selectedItemIndex]);
                    PlayAccept();
                    ReturnToSlotList();
                }
                else
                {
                    VanityStore.ClearSlot(CurrentSlot, PlayerId, SaveIdx, SaS2VanitySlotsPlugin.PerCharacter);
                    PlaySelect();
                    ReturnToSlotList();
                }
                return true;
            }

            // SlotList mode: left click opens picker, right click clears.
            if (hit.Index != _selectedSlotIndex) _selectedSlotIndex = hit.Index;
            if (leftClick)
            {
                BuildCandidates();
                _mode = Mode.ItemPicker;
                _selectedItemIndex = 0;
                _scrollOffset = 0f;
                var cur = VanityStore.GetVanityLootIdx(CurrentSlot, PlayerId, SaveIdx, SaS2VanitySlotsPlugin.PerCharacter);
                if (cur >= 0 && _candidateList != null)
                {
                    var idx = _candidateList.IndexOf(cur);
                    if (idx >= 0) _selectedItemIndex = idx;
                }
                PlayAccept();
            }
            else
            {
                VanityStore.ClearSlot(CurrentSlot, PlayerId, SaveIdx, SaS2VanitySlotsPlugin.PerCharacter);
                PlaySelect();
            }
            return true;
        }

        return false;
    }

    private static bool PointInRect(Vector2 p, Rectangle r) =>
        p.X > r.X && p.X < r.Right && p.Y > r.Y && p.Y < r.Bottom;

    // Consumes the latched click flags so a single physical press is acted on once, even when a frame runs several Updates.
    // MouseMgr only clears them in EndDraw, which happens once per rendered frame, while the mouse is handled here in Update.
    private static void ConsumeClick(bool left, bool right)
    {
        if (left) MouseMgr.isLeftClick = false;
        if (right) MouseMgr.isRightClick = false;
    }

    // -- draw --

    public override void Draw()
    {
        base.Draw();
        var vp = Game1.Instance.GraphicsDevice.Viewport;
        // Clamped here so a hand-edited config value cannot make the layout unusable.
        var scale = Math.Max(0.5f, Math.Min(2f, SaS2VanitySlotsPlugin.MenuScale));
        _currentScale = scale;
        var boxWidth = vp.Width * 0.5f * scale;
        var boxHeight = vp.Height * 0.9f * scale;

        var margin = boxWidth * 0.025f;
        var isMainPlayer = player.ID == GameSessionMgr.gameSession.mainPlayerIdx;
        var boxX = isMainPlayer ? 0f - margin : vp.Width * 0.5f + margin;
        const float boxYOffset = 30f; // To cover the tips
        var boxY = (vp.Height - boxHeight) / 2f;
        boxY += boxYOffset * 1.5f; // For full tips coverage

        UIRender.DrawRect(new Rectangle((int)boxX, (int)boxY, (int)boxWidth, (int)boxHeight),
            0.85f, 0, 1f, 1f, UIRender.interfaceTex);

        var title = _mode == Mode.ItemPicker ? "Vanity - Pick Item" : "Vanity";
        Text.DrawText(new StringBuilder(title),
            new Vector2(boxX + boxWidth / 2f, boxY + 30f * scale), Color.Yellow, 0.85f * scale, 1);

        // Where the tips start.
        var tipsY = vp.Height * (1f + OldBoxHeightFactor * scale) * 0.5f + TipsOffsetY * scale;

        var listX = boxX + 40f * scale;
        var listY = boxY + TopMargin * scale;
        var listWidth = boxWidth - 80f * scale;
        // A partly scrolled row can hang below the visible height by up to one item height, so that much is reserved on top of the bottom margin to keep every row clear of the tips.
        var listVisibleHeight = Math.Max(0f, tipsY - (ItemHeight + BottomMargin) * scale - listY);
        _currentListVisibleHeight = listVisibleHeight;

        _itemHitRects.Clear();

        var currentY = listY - _scrollOffset;

        if (_mode == Mode.ItemPicker)
        {
            DrawItemPicker(listX, currentY, listY, listWidth, listVisibleHeight, scale);
        }
        else
        {
            DrawSlotList(listX, currentY, listY, listWidth, listVisibleHeight, scale);
        }

        DrawHelpBar(boxX, boxWidth, tipsY, scale);
    }

    private void DrawSlotList(float x, float currentY, float listY, float listWidth, float listVisibleHeight,
        float scale)
    {
        var perChar = SaS2VanitySlotsPlugin.PerCharacter;
        var playerId = PlayerId;
        var saveIdx = SaveIdx;
        var itemHeight = ItemHeight * scale;

        for (var i = 0; i < VanityStore.VisibleSlots.Length; i++)
        {
            var slot = VanityStore.VisibleSlots[i];
            var selected = i == _selectedSlotIndex;

            if (currentY + itemHeight > listY && currentY < listY + listVisibleHeight)
            {
                _itemHitRects.Add(new ItemHit
                {
                    Index = i,
                    Rect = new Rectangle((int)x, (int)currentY, (int)listWidth, (int)itemHeight)
                });

                if (selected)
                    UIRender.DrawRect(
                        new Rectangle((int)x, (int)currentY, (int)listWidth, (int)itemHeight),
                        0.2f, 3, 1f, 1f, UIRender.interfaceTex);

                var textColor = selected ? Color.Yellow : Color.White;
                var textY = currentY + itemHeight * 0.75f;

                Text.DrawText(new StringBuilder(VanityStore.SlotNames[i]),
                    new Vector2(x + 10f * scale, textY), textColor, 0.7f * scale, 0);

                var valStr = FormatSlotValue(slot, playerId, saveIdx, perChar);
                Text.DrawText(new StringBuilder(valStr),
                    new Vector2(x + listWidth - ValueWidth * scale, textY), textColor, 0.7f * scale, 0);
            }

            currentY += itemHeight;
        }
    }

    private void DrawItemPicker(float x, float currentY, float listY, float listWidth, float listVisibleHeight,
        float scale)
    {
        if (_candidateList == null || _candidateList.Count == 0)
        {
            Text.DrawText(new StringBuilder("No items discovered for this slot."),
                new Vector2(x, currentY + 10f * scale), new Color(0.7f, 0.7f, 0.7f, 1f), 0.7f * scale, 0);
            return;
        }

        var itemHeight = ItemHeight * scale;

        for (var i = 0; i < _candidateList.Count; i++)
        {
            var lootIdx = _candidateList[i];
            var selected = i == _selectedItemIndex;

            if (currentY + itemHeight > listY && currentY < listY + listVisibleHeight)
            {
                _itemHitRects.Add(new ItemHit
                {
                    Index = i,
                    Rect = new Rectangle((int)x, (int)currentY, (int)listWidth, (int)itemHeight)
                });

                if (selected)
                    UIRender.DrawRect(
                        new Rectangle((int)x, (int)currentY, (int)listWidth, (int)itemHeight),
                        0.2f, 3, 1f, 1f, UIRender.interfaceTex);

                var textColor = selected ? Color.Yellow : Color.White;
                var textY = currentY + itemHeight * 0.75f;
                var lDef = LootCatalog.lootDef[lootIdx];
                var name = lDef.title.Length > 0 ? lDef.title[Game1.language] : lDef.name;

                Text.DrawText(new StringBuilder(name),
                    new Vector2(x + 10f * scale, textY), textColor, 0.7f * scale, 0);

                // Show the lootIdx on the right so the user can cross-reference with config.
                Text.DrawText(new StringBuilder("#" + lootIdx),
                    new Vector2(x + listWidth - 80f * scale, textY),
                    new Color(0.6f, 0.6f, 0.6f, 1f), 0.6f * scale, 0);
            }

            currentY += itemHeight;
        }
    }

    private string FormatSlotValue(int slot, int playerId, int saveIdx, bool perChar)
    {
        if (VanityStore.IsNaked(slot, playerId, saveIdx, perChar)) return "[Naked]";
        var li = VanityStore.GetVanityLootIdx(slot, playerId, saveIdx, perChar);
        if (li < 0 || li >= LootCatalog.lootDef.Count) return "Default";
        var lDef = LootCatalog.lootDef[li];
        var name = lDef.title.Length > 0 ? lDef.title[Game1.language] : lDef.name;
        return name;
    }

    private void DrawHelpBar(float boxX, float boxWidth, float tipsY, float scale)
    {
        var useKeyboard = player.inputProfile.keyMouseEnable;
        var action = useKeyboard ? "[Space]" : "[a]";
        var centerX = boxX + boxWidth / 2f;

        var top = new StringBuilder();
        if (_mode == Mode.ItemPicker)
        {
            top.Append($"\u02ef{action}\u02f0 Pick  |  \u02ef[ll]/[lr]\u02f0 Change  |  ");
            top.Append(useKeyboard ? "\u02efBksp\u02f0 Clear" : "\u02ef[x]\u02f0 Clear");
        }
        else
        {
            top.Append($"\u02ef{action}\u02f0 Pick Item  |  \u02ef[ll]/[lr]\u02f0 Change  |  ");
            var nakedKb = SaS2VanitySlotsPlugin.NakedKeybind != null
                ? SaS2VanitySlotsPlugin.NakedKeybind.DisplayString()
                : "N";
            top.Append($"\u02ef{nakedKb}\u02f0 Naked  |  ");
            top.Append(useKeyboard ? "\u02efBksp\u02f0 Clear" : "\u02ef[x]\u02f0 Clear");
        }
        Text.DrawText(top, new Vector2(centerX, tipsY), Color.White, 0.6f * scale, 1, player, 1);

        var bottom = new StringBuilder();
        bottom.Append("\u02ef[b]\u02f0 Back");
        Text.DrawText(bottom, new Vector2(centerX, tipsY + 30f * scale), Color.White, 0.6f * scale, 1, player, 1);
    }

    // -- scrolling --

    private void EnsureVisible()
    {
        var count = _mode == Mode.ItemPicker
            ? (_candidateList != null ? _candidateList.Count : 0)
            : VanityStore.VisibleSlots.Length;
        if (count == 0) return;

        var sel = _mode == Mode.ItemPicker ? _selectedItemIndex : _selectedSlotIndex;
        var itemHeight = ItemHeight * _currentScale;
        var itemTop = sel * itemHeight;
        var itemBottom = itemTop + itemHeight;

        var visibleTop = _scrollOffset;
        var visibleBottom = _scrollOffset + _currentListVisibleHeight;

        if (itemTop < visibleTop) _scrollOffset = itemTop;
        else if (itemBottom > visibleBottom) _scrollOffset = itemBottom - _currentListVisibleHeight;

        _scrollOffset = Math.Max(0f, _scrollOffset);
    }

    // LevelBase wraps these as non-virtual; mirror the SaS2ModOptions approach via reflection so
    // we do not depend on protected members the decompiler could not surface.
    private new void PlaySelect() => AccessTools.Method(typeof(LevelBase), "PlaySelect")?.Invoke(this, null);
    private new void PlayAccept() => AccessTools.Method(typeof(LevelBase), "PlayAccept")?.Invoke(this, null);
    private new void PlayCancel() => AccessTools.Method(typeof(LevelBase), "PlayCancel")?.Invoke(this, null);
    private new bool CanInput() => (bool)AccessTools.Method(typeof(LevelBase), "CanInput")?.Invoke(this, null)!;
}