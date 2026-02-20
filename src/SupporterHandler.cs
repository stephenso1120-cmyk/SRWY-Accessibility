using System;
using Il2CppCom.BBStudio.SRTeam.UIs;
using Il2CppTMPro;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace SRWYAccess
{
    /// <summary>
    /// Handles support unit selection during battle check.
    /// AttackSupporterListUIHandler and DefenceSupporterListUIHandler are
    /// MonoBehaviour-based (NOT UIHandlerBase), so GenericMenuReader cannot
    /// find them. This handler polls for them separately.
    ///
    /// Both handlers have supporterButtonList (List&lt;Button&gt;) and
    /// currentCursorIndex (int) for tracking selection changes.
    /// Button text is read from TMP children of each button.
    /// </summary>
    public class SupporterHandler
    {
        private AttackSupporterListUIHandler _attackHandler;
        private DefenceSupporterListUIHandler _defenceHandler;
        private IntPtr _attackPtr = IntPtr.Zero;
        private IntPtr _defencePtr = IntPtr.Zero;
        private int _lastAttackCursor = -1;
        private int _lastDefenceCursor = -1;
        private bool _attackAnnounced;
        private bool _defenceAnnounced;
        private int _faultCount;

        public void ReleaseHandler()
        {
            _attackHandler = null;
            _defenceHandler = null;
            _attackPtr = IntPtr.Zero;
            _defencePtr = IntPtr.Zero;
            _lastAttackCursor = -1;
            _lastDefenceCursor = -1;
            _attackAnnounced = false;
            _defenceAnnounced = false;
        }

        /// <summary>
        /// Poll for supporter list handlers.
        /// canSearch: if true, may call FindObjectsOfType this cycle.
        /// </summary>
        public void Update(bool canSearch)
        {
            if (_faultCount >= 5) return;

            try
            {
                UpdateInner(canSearch);
            }
            catch (Exception ex)
            {
                _faultCount++;
                ReleaseHandler();
                DebugHelper.Write($"SupporterHandler: FAULT #{_faultCount}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void UpdateInner(bool canSearch)
        {
            // Search for supporter handlers when allowed
            if (canSearch)
            {
                FindSupporterHandlers();
            }

            // Poll attack supporter
            if ((object)_attackHandler != null)
            {
                if (!SafeCall.ProbeObject(_attackPtr))
                {
                    _attackHandler = null;
                    _attackPtr = IntPtr.Zero;
                    _lastAttackCursor = -1;
                    _attackAnnounced = false;
                }
                else
                {
                    PollAttackSupporter();
                }
            }

            // Poll defence supporter
            if ((object)_defenceHandler != null)
            {
                if (!SafeCall.ProbeObject(_defencePtr))
                {
                    _defenceHandler = null;
                    _defencePtr = IntPtr.Zero;
                    _lastDefenceCursor = -1;
                    _defenceAnnounced = false;
                }
                else
                {
                    PollDefenceSupporter();
                }
            }
        }

        private void FindSupporterHandlers()
        {
            // Only search if we don't already have handlers
            if ((object)_attackHandler == null)
            {
                try
                {
                    var handlers = UnityEngine.Object.FindObjectsOfType<AttackSupporterListUIHandler>();
                    if (handlers != null && handlers.Count > 0)
                    {
                        var h = handlers[0];
                        if ((object)h != null && h.Pointer != IntPtr.Zero)
                        {
                            _attackHandler = h;
                            _attackPtr = h.Pointer;
                            _lastAttackCursor = -1;
                            _attackAnnounced = false;
                            DebugHelper.Write("SupporterHandler: Found AttackSupporterList");
                        }
                    }
                }
                catch { }
            }

            if ((object)_defenceHandler == null)
            {
                try
                {
                    var handlers = UnityEngine.Object.FindObjectsOfType<DefenceSupporterListUIHandler>();
                    if (handlers != null && handlers.Count > 0)
                    {
                        var h = handlers[0];
                        if ((object)h != null && h.Pointer != IntPtr.Zero)
                        {
                            _defenceHandler = h;
                            _defencePtr = h.Pointer;
                            _lastDefenceCursor = -1;
                            _defenceAnnounced = false;
                            DebugHelper.Write("SupporterHandler: Found DefenceSupporterList");
                        }
                    }
                }
                catch { }
            }
        }

        private void PollAttackSupporter()
        {
            try
            {
                // Check if the handler's GameObject is active
                try
                {
                    var go = _attackHandler.gameObject;
                    if ((object)go == null || !go.activeInHierarchy)
                    {
                        if (_attackAnnounced)
                        {
                            _attackAnnounced = false;
                            _lastAttackCursor = -1;
                        }
                        return;
                    }
                }
                catch { return; }

                int cursor = _attackHandler.currentCursorIndex;

                // Announce screen name when first appearing
                if (!_attackAnnounced)
                {
                    _attackAnnounced = true;
                    ScreenReaderOutput.Say(Loc.Get("support_attack_screen"));
                    DebugHelper.Write("SupporterHandler: Attack support screen opened");
                }

                if (cursor == _lastAttackCursor) return;
                _lastAttackCursor = cursor;

                // Read button text at cursor index
                string text = ReadSupporterButtonText(_attackHandler.supporterButtonList, cursor);

                // Fallback: enum name for known types
                if (string.IsNullOrWhiteSpace(text))
                {
                    switch (cursor)
                    {
                        case 0: text = Loc.Get("support_none"); break;
                        case 1: text = Loc.Get("support_double_attack"); break;
                        default: text = $"Support {cursor}"; break;
                    }
                }

                ScreenReaderOutput.Say(text);
                DebugHelper.Write($"SupporterHandler: Attack cursor={cursor} text={text}");
            }
            catch { }
        }

        private void PollDefenceSupporter()
        {
            try
            {
                // Check if the handler's GameObject is active
                try
                {
                    var go = _defenceHandler.gameObject;
                    if ((object)go == null || !go.activeInHierarchy)
                    {
                        if (_defenceAnnounced)
                        {
                            _defenceAnnounced = false;
                            _lastDefenceCursor = -1;
                        }
                        return;
                    }
                }
                catch { return; }

                int cursor = _defenceHandler.currentCursorIndex;

                // Announce screen name when first appearing
                if (!_defenceAnnounced)
                {
                    _defenceAnnounced = true;
                    ScreenReaderOutput.Say(Loc.Get("support_defence_screen"));
                    DebugHelper.Write("SupporterHandler: Defence support screen opened");
                }

                if (cursor == _lastDefenceCursor) return;
                _lastDefenceCursor = cursor;

                // Read button text at cursor index
                string text = ReadSupporterButtonText(_defenceHandler.supporterButtonList, cursor);

                // Fallback: enum name
                if (string.IsNullOrWhiteSpace(text))
                {
                    if (cursor == 0) text = Loc.Get("support_none");
                    else text = $"Support {cursor}";
                }

                ScreenReaderOutput.Say(text);
                DebugHelper.Write($"SupporterHandler: Defence cursor={cursor} text={text}");
            }
            catch { }
        }

        /// <summary>
        /// Read text from a supporter button at the given index.
        /// Reads TextMeshProUGUI from the button's GameObject children.
        /// </summary>
        private static string ReadSupporterButtonText(
            Il2CppSystem.Collections.Generic.List<UnityEngine.UI.Button> buttons, int index)
        {
            if (buttons == null || index < 0 || index >= buttons.Count) return null;

            try
            {
                var btn = buttons[index];
                if ((object)btn == null) return null;

                GameObject btnGo = null;
                try
                {
                    btnGo = btn.gameObject;
                    if ((object)btnGo == null || btnGo.Pointer == IntPtr.Zero) return null;
                }
                catch { return null; }

                // Read all TMP text from button children, pick longest non-numeric
                var tmps = btnGo.GetComponentsInChildren<TextMeshProUGUI>(false);
                if (tmps == null || tmps.Count == 0) return null;

                string bestText = null;
                foreach (var tmp in tmps)
                {
                    if ((object)tmp == null) continue;

                    string t = null;
                    if (SafeCall.TmpTextMethodAvailable)
                    {
                        IntPtr strPtr = SafeCall.ReadTmpTextSafe(tmp.Pointer);
                        if (strPtr != IntPtr.Zero)
                        {
                            try
                            {
                                t = IL2CPP.Il2CppStringToManaged(strPtr);
                                t = TextUtils.CleanRichText(t);
                            }
                            catch { }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(t))
                    {
                        if (bestText == null || t.Length > bestText.Length)
                            bestText = t;
                    }
                }

                return bestText;
            }
            catch { return null; }
        }
    }
}
