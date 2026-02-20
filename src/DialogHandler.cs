using System;
using MelonLoader;
using UnityEngine;
using Il2CppCom.BBStudio.SRTeam.UIs;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTMPro;

namespace SRWYAccess
{
    /// <summary>
    /// Handles dialog/confirmation box announcements.
    /// Detects when dialogs open, reads text content, and tracks
    /// cursor movement in Yes/No and selection dialogs.
    ///
    /// SAFETY: All IL2CPP object null checks use (object)x != null to bypass
    /// Unity's overloaded == operator, which accesses native pointers and can
    /// crash on destroyed objects.
    /// </summary>
    public class DialogHandler
    {
        private DialogSystem _dialog;
        private DialogSystem.DialogStatus _lastStatus = DialogSystem.DialogStatus.None;
        private int _lastSelectNo = -1;
        private string _lastDialogText = "";

        // Re-read mechanism: dialog text may not be populated on first frame
        private bool _pendingReread;
        private int _rereadCount;

        // Known dialog title labels to filter out from TMP text
        private static readonly System.Collections.Generic.HashSet<string> _dialogTitleLabels =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "QUESTION", "CAUTION", "INFORMATION", "WARNING", "ERROR", "NOTICE",
                "SELECT", "CONFIRM", "ALERT"
            };

        public void ReleaseHandler()
        {
            _dialog = null;
            _lastStatus = DialogSystem.DialogStatus.None;
            _lastSelectNo = -1;
            _lastDialogText = "";
            _pendingReread = false;
            _rereadCount = 0;
        }

        public void Update()
        {
            DialogSystem.DialogStatus currentStatus;
            try
            {
                currentStatus = DialogSystem._status;
            }
            catch
            {
                return;
            }

            // Dialog just opened
            if (currentStatus == DialogSystem.DialogStatus.Busy && _lastStatus != DialogSystem.DialogStatus.Busy)
            {
                _lastStatus = currentStatus;
                OnDialogOpened();
                return;
            }

            // Dialog just closed
            if (currentStatus != DialogSystem.DialogStatus.Busy && _lastStatus == DialogSystem.DialogStatus.Busy)
            {
                _lastStatus = currentStatus;
                OnDialogClosed();
                return;
            }

            _lastStatus = currentStatus;

            // While dialog is active, track selection changes
            if (currentStatus == DialogSystem.DialogStatus.Busy)
            {
                TrackSelectionChange();

                // Re-read dialog text if initial read got a title label
                if (_pendingReread)
                {
                    _rereadCount++;
                    if (_rereadCount % ModConfig.DialogRereadInterval == 0 && _rereadCount <= ModConfig.DialogRereadMaxAttempts)
                    {
                        string text = ReadDialogUIText();
                        if (!string.IsNullOrWhiteSpace(text) && text != _lastDialogText)
                        {
                            _pendingReread = false;
                            _lastDialogText = text;
                            var dialogType = DialogSystem.dialogType;
                            string announcement = BuildAnnouncement(text, dialogType);
                            ScreenReaderOutput.Say(announcement);
                            DebugHelper.Write($"DialogHandler: re-read success: {text}");
                        }
                    }
                    else if (_rereadCount > ModConfig.DialogRereadMaxAttempts)
                    {
                        // Retry limit reached. Announce whatever text we have
                        // (even title labels) rather than staying silent forever.
                        _pendingReread = false;
                        string text = ReadDialogUIText() ?? ReadDialogText();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            _lastDialogText = text;
                            var dialogType = DialogSystem.dialogType;
                            string announcement = BuildAnnouncement(text, dialogType);
                            ScreenReaderOutput.Say(announcement);
                            DebugHelper.Write($"DialogHandler: re-read timeout, announcing as-is: {text}");
                        }
                        else
                        {
                            DebugHelper.Write("DialogHandler: re-read timeout, no text available");
                        }
                    }
                }
            }
        }

        private void OnDialogOpened()
        {
            _lastSelectNo = -1;

            // Try to reuse cached DialogSystem ref if still valid
            if ((object)_dialog != null)
            {
                try
                {
                    if (_dialog.Pointer == IntPtr.Zero)
                        _dialog = null;
                }
                catch { _dialog = null; }
            }

            if ((object)_dialog == null)
            {
                try
                {
                    _dialog = UnityEngine.Object.FindObjectOfType<DialogSystem>();
                }
                catch (Exception ex)
                {
                    DebugHelper.Write($"DialogHandler: FindObjectOfType failed: {ex.Message}");
                }
            }

            if ((object)_dialog == null)
            {
                DebugHelper.Write("DialogHandler: DialogSystem instance not found");
                return;
            }

            // Read dialog text: try UI components first (actual rendered text),
            // fall back to string arrays (which may contain localization keys)
            string text = ReadDialogUIText() ?? ReadDialogText();
            if (string.IsNullOrEmpty(text))
            {
                // Text not available yet - schedule re-read
                _pendingReread = true;
                _rereadCount = 0;
                DebugHelper.Write("DialogHandler: No dialog text found, scheduling re-read");
                return;
            }

            // Determine dialog type and announce accordingly
            var dialogType = DialogSystem.dialogType;
            string announcement = BuildAnnouncement(text, dialogType);

            ScreenReaderOutput.Say(announcement);
            _pendingReread = false;
            MelonLogger.Msg($"[SRWYAccess] Dialog opened: type={dialogType}, text={text}");
        }

        private void OnDialogClosed()
        {
            _lastSelectNo = -1;
            _lastDialogText = "";
            _pendingReread = false;
            _rereadCount = 0;
        }

        private void TrackSelectionChange()
        {
            if ((object)_dialog == null) return;

            try
            {
                if (_dialog.Pointer == IntPtr.Zero)
                {
                    _dialog = null;
                    return;
                }
            }
            catch { _dialog = null; return; }

            var dialogType = DialogSystem.dialogType;
            if (!IsSelectionDialog(dialogType)) return;

            try
            {
                int currentSelect = _dialog.SelectNo;
                if (currentSelect != _lastSelectNo)
                {
                    // Announce on every change (including initial selection)
                    AnnounceSelection(currentSelect, dialogType);
                }
                _lastSelectNo = currentSelect;
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"DialogHandler: SelectNo read error: {ex.Message}");
            }
        }

        private string ReadDialogUIText()
        {
            if ((object)_dialog == null) return null;

            try
            {
                if (_dialog.Pointer == IntPtr.Zero)
                {
                    _dialog = null;
                    return null;
                }

                // SAFETY: ProbeObject before accessing .dialogPrefabObject to prevent AV
                // on partially-destroyed DialogSystem during scene transitions.
                if (!SafeCall.ProbeObject(_dialog.Pointer))
                {
                    DebugHelper.Write("DialogHandler: ProbeObject failed for DialogSystem");
                    _dialog = null;
                    return null;
                }

                var prefab = _dialog.dialogPrefabObject;
                if ((object)prefab == null || prefab.Pointer == IntPtr.Zero) return null;

                // CRITICAL: Inner try-catch for GetComponentsInChildren
                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<TextMeshProUGUI> tmps = null;
                try
                {
                    tmps = prefab.GetComponentsInChildren<TextMeshProUGUI>(false);
                }
                catch (Exception ex)
                {
                    DebugHelper.Write($"DialogHandler: GetComponentsInChildren error: {ex.GetType().Name}");
                    return null;
                }
                if (tmps == null || tmps.Count == 0) return null;

                string bestText = null;
                foreach (var tmp in tmps)
                {
                    if ((object)tmp == null) continue;
                    try
                    {
                        // CRITICAL: Use SafeCall to read tmp.text - direct access causes
                        // uncatchable AccessViolationException when TMP destroyed
                        string t = null;
                        if (SafeCall.TmpTextMethodAvailable)
                        {
                            IntPtr il2cppStrPtr = SafeCall.ReadTmpTextSafe(tmp.Pointer);
                            if (il2cppStrPtr != IntPtr.Zero)
                            {
                                try
                                {
                                    t = IL2CPP.Il2CppStringToManaged(il2cppStrPtr);
                                    t = TextUtils.CleanRichText(t);
                                }
                                catch { }
                            }
                        }
                        if (!string.IsNullOrWhiteSpace(t) && !_dialogTitleLabels.Contains(t.Trim()))
                        {
                            if (bestText == null || t.Length > bestText.Length)
                                bestText = t;
                        }
                    }
                    catch { }
                }

                if (!string.IsNullOrEmpty(bestText))
                {
                    _lastDialogText = bestText;
                    DebugHelper.Write($"DialogHandler: UI text: {bestText}");
                }
                return bestText;
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"DialogHandler: ReadDialogUIText error: {ex.Message}");
                return null;
            }
        }

        private string ReadDialogText()
        {
            if ((object)_dialog == null) return null;

            string result = ReadTextArray(_dialog.dialogText)
                         ?? ReadTextArray(_dialog.dialogTextLocalization);

            if (!string.IsNullOrEmpty(result))
                _lastDialogText = result;

            return result;
        }

        private static string ReadTextArray(Il2CppStringArray arr)
        {
            try
            {
                if ((object)arr == null || arr.Length == 0) return null;

                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < arr.Length; i++)
                {
                    string line = arr[i];
                    if (!string.IsNullOrEmpty(line))
                    {
                        if (sb.Length > 0) sb.Append(" ");
                        sb.Append(TextUtils.CleanRichText(line));
                    }
                }
                string result = sb.ToString();
                return string.IsNullOrEmpty(result) ? null : result;
            }
            catch { return null; }
        }

        private string BuildAnnouncement(string text, DialogSystem.DialogType type)
        {
            if (IsYesNoDialog(type))
            {
                GetYesNoLabels(out string yes, out string no);
                return Loc.Get("dialog_yesno", text, yes, no);
            }

            if (IsSelectDialog(type))
                return Loc.Get("dialog_select", text);

            return Loc.Get("dialog_message", text);
        }

        private void AnnounceSelection(int selectNo, DialogSystem.DialogType type)
        {
            if (IsYesNoDialog(type))
            {
                GetYesNoLabels(out string yes, out string no);
                ScreenReaderOutput.Say(selectNo == 0 ? yes : no);
            }
            else if (IsSelectDialog(type))
            {
                string buttonText = GetButtonText(selectNo);
                int totalOptions = GetSelectOptionCount(type);

                if (!string.IsNullOrEmpty(buttonText))
                    ScreenReaderOutput.Say(Loc.Get("dialog_option_named", buttonText, selectNo + 1, totalOptions));
                else
                    ScreenReaderOutput.Say(Loc.Get("dialog_option", selectNo + 1, totalOptions));
            }
        }

        /// <summary>
        /// Get the actual number of options in a select dialog.
        /// Tries to read from buttons array first, falls back to type inference.
        /// </summary>
        private int GetSelectOptionCount(DialogSystem.DialogType type)
        {
            try
            {
                var btns = _dialog?.buttons;
                if ((object)btns != null && btns.Length > 0)
                    return btns.Length;
            }
            catch { }
            return (type == DialogSystem.DialogType.Select3) ? 3 : 2;
        }

        private void GetYesNoLabels(out string yesText, out string noText)
        {
            yesText = null;
            noText = null;
            try
            {
                yesText = _dialog?.replaceTextYes;
                noText = _dialog?.replaceTextNo;
            }
            catch { }
            if (string.IsNullOrEmpty(yesText)) yesText = Loc.Get("dialog_yes");
            if (string.IsNullOrEmpty(noText)) noText = Loc.Get("dialog_no");
        }

        /// <summary>
        /// Read the text from a dialog button by index using TextMeshProUGUI.
        /// </summary>
        private string GetButtonText(int index)
        {
            if ((object)_dialog == null) return null;

            try
            {
                var buttons = _dialog.buttons;
                if ((object)buttons == null || index < 0 || index >= buttons.Length)
                    return null;

                var button = buttons[index];
                if ((object)button == null) return null;

                var tmpText = button.GetComponentInChildren<TextMeshProUGUI>();
                if ((object)tmpText != null)
                {
                    // CRITICAL: Use SafeCall to read tmp.text - direct access causes
                    // uncatchable AccessViolationException when TMP destroyed
                    string text = null;
                    if (SafeCall.TmpTextMethodAvailable)
                    {
                        IntPtr il2cppStrPtr = SafeCall.ReadTmpTextSafe(tmpText.Pointer);
                        if (il2cppStrPtr != IntPtr.Zero)
                        {
                            try
                            {
                                text = IL2CPP.Il2CppStringToManaged(il2cppStrPtr);
                                text = TextUtils.CleanRichText(text);
                            }
                            catch { }
                        }
                    }
                    if (!string.IsNullOrEmpty(text))
                        return text;
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"DialogHandler: GetButtonText({index}) error: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Collect current dialog info for screen review.
        /// </summary>
        public void CollectReviewItems(System.Collections.Generic.List<string> items)
        {
            if (_lastStatus != DialogSystem.DialogStatus.Busy) return;
            if (string.IsNullOrWhiteSpace(_lastDialogText)) return;

            items.Add(_lastDialogText);

            // Add selection options if this is a selection dialog
            if ((object)_dialog == null) return;

            try
            {
                var dialogType = DialogSystem.dialogType;

                if (IsYesNoDialog(dialogType))
                {
                    GetYesNoLabels(out string yes, out string no);
                    int selectNo = -1;
                    try { selectNo = _dialog.SelectNo; } catch { }
                    items.Add((selectNo == 0 ? "(*) " : "") + yes);
                    items.Add((selectNo == 1 ? "(*) " : "") + no);
                }
                else if (IsSelectDialog(dialogType))
                {
                    int totalOptions = GetSelectOptionCount(dialogType);
                    int selectNo = -1;
                    try { selectNo = _dialog.SelectNo; } catch { }

                    for (int i = 0; i < totalOptions; i++)
                    {
                        string btnText = GetButtonText(i);
                        string prefix = (i == selectNo) ? "(*) " : "";
                        if (!string.IsNullOrEmpty(btnText))
                            items.Add(prefix + btnText);
                        else
                            items.Add(prefix + Loc.Get("dialog_option", i + 1, totalOptions));
                    }
                }
            }
            catch { }
        }

        private static bool IsYesNoDialog(DialogSystem.DialogType type)
        {
            return type == DialogSystem.DialogType.YesNoBlue
                || type == DialogSystem.DialogType.YesNoRed
                || type == DialogSystem.DialogType.YesNoYellow
                || type == DialogSystem.DialogType.YesNoLong;
        }

        private static bool IsSelectDialog(DialogSystem.DialogType type)
        {
            return type == DialogSystem.DialogType.Select2
                || type == DialogSystem.DialogType.Select3;
        }

        private static bool IsSelectionDialog(DialogSystem.DialogType type)
        {
            return IsYesNoDialog(type) || IsSelectDialog(type);
        }

    }
}
