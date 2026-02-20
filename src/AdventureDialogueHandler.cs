using System;
using UnityEngine;
using Il2CppAdvSDemoDlgEv;
using Il2CppCom.BBStudio.SRTeam.Common;
using Il2CppCom.BBStudio.SRTeam.UIs;
using Il2CppInterop.Runtime;

namespace SRWYAccess
{
    /// <summary>
    /// Reads story dialogue and subtitles, announces via screen reader.
    ///
    /// SAFETY: All IL2CPP object null checks use (object)x != null to bypass
    /// Unity's overloaded == operator, which accesses native pointers and can
    /// crash on destroyed objects.
    /// </summary>
    public class AdventureDialogueHandler
    {
        private SubtitleUIHandler _subtitleHandler;
        private string _lastSubtitleText = "";
        private string _lastChapterText = "";

        private SDemoDialogueMessage _dialogueMessage;
        private SDemoSpeakerName _speakerName;
        private SDemoDialogueTextDrawer _textDrawer;
        private AdventureSceneObjects _sceneObjects;

        private string _lastDialogueText = "";
        private string _lastSpeakerName = "";
        // Track the IL2CPP string pointer (not just content) to detect
        // same-text dialogue entries. When a character says the same line
        // consecutively, the game allocates a new IL2CPP string object
        // with the same content but a different pointer.
        private IntPtr _lastDialoguePtr = IntPtr.Zero;
        // Cooldown after dialogue text becomes empty (scene likely ending).
        // Prevents FindObjectOfType during scene destruction and stale reference AV.
        private int _searchCooldown = 0;

        // Stale dialogue detection: when the same text persists for many polls,
        // reduce IL2CPP access frequency. The game can destroy SDemoDialogueMessage
        // BEFORE text becomes empty (race condition), so the "empty text" safety
        // mechanism doesn't always fire. By reducing access frequency when text
        // is unchanged, we shrink the crash window dramatically.
        private int _staleTextCount;

        /// <summary>
        /// Set to true when dialogue refs are released (text went empty).
        /// The main loop checks this to preemptively pause ALL IL2CPP access
        /// during the ~400ms window before InputMode changes from ADVENTURE.
        /// Without this, GST.Update()'s GetCurrentInputBehaviour() call can
        /// hit freed native objects and cause an uncatchable AV.
        /// </summary>
        public bool RefsJustReleased { get; set; }

        public void ReleaseHandler()
        {
            _subtitleHandler = null;
            _lastSubtitleText = "";
            _lastChapterText = "";
            _dialogueMessage = null;
            _speakerName = null;
            _textDrawer = null;
            _sceneObjects = null;
            _lastDialogueText = "";
            _lastSpeakerName = "";
            _lastDialoguePtr = IntPtr.Zero;
            _searchCooldown = 0;
            _staleTextCount = 0;
            RefsJustReleased = false;
        }

        public void Update(bool canSearch)
        {
            if (_searchCooldown > 0)
                _searchCooldown--;

            // Stale text detection: when dialogue text hasn't changed for many
            // polls, reduce IL2CPP access frequency. The game can destroy
            // SDemoDialogueMessage objects during scene transitions before text
            // becomes empty, causing uncatchable AccessViolationException.
            // By probing less frequently when idle, we shrink the crash window.
            if (_staleTextCount >= ModConfig.AdvStaleTextLimit && HasDialogueRefs())
            {
                if (_staleTextCount < ModConfig.AdvMaxStaleCount) _staleTextCount++;
                if (_staleTextCount % ModConfig.AdvStaleProbeInterval != 0)
                    return; // Skip this cycle - reduce IL2CPP access
            }

            ReadSubtitles();

            string currentText = null;
            string currentSpeaker = null;
            IntPtr currentDialoguePtr = IntPtr.Zero;

            if (TryReadSDemoSystem(out currentText, out currentSpeaker, out currentDialoguePtr))
            {
            }
            else if (TryReadTextDrawer(out currentText, out currentSpeaker))
            {
            }
            else if (TryReadSceneObjects(out currentText, out currentSpeaker))
            {
            }
            else
            {
                if (canSearch && _searchCooldown <= 0)
                    FindDialogueObjects();
                return;
            }

            currentText = TextUtils.CleanRichText(currentText);
            currentSpeaker = TextUtils.CleanRichText(currentSpeaker);

            if (string.IsNullOrWhiteSpace(currentText)) return;

            // Detect new dialogue entry by text content change OR IL2CPP string
            // pointer change. When a character says the same line consecutively,
            // text content is identical but the game allocates a new string object
            // with a different pointer. Speaker-only changes for the same text
            // are rapid corrections by the game (e.g., 特工→艾蒂卡 in 124ms).
            bool textChanged = currentText != _lastDialogueText;
            bool ptrChanged = currentDialoguePtr != IntPtr.Zero
                && currentDialoguePtr != _lastDialoguePtr;

            if (textChanged || ptrChanged)
            {
                if (ptrChanged && !textChanged)
                    DebugHelper.Write($"ADH: Same text, new string ptr (0x{_lastDialoguePtr:X}→0x{currentDialoguePtr:X})");
                _lastDialogueText = currentText;
                _lastSpeakerName = currentSpeaker;
                _lastDialoguePtr = currentDialoguePtr;
                _staleTextCount = 0; // Reset: new text arrived
                AnnounceDialogue(currentSpeaker, currentText);
            }
            else
            {
                _staleTextCount++;
            }
        }

        private bool HasDialogueRefs()
        {
            return (object)_dialogueMessage != null
                || (object)_textDrawer != null
                || (object)_sceneObjects != null;
        }

        private void ReadSubtitles()
        {
            if ((object)_subtitleHandler == null) return;

            try
            {
                // Fresh ref via FindObjectOfType - cached refs crash on destroyed
                // objects (Pointer stays non-zero, uncatchable AV in .NET 6).
                var freshSub = UnityEngine.Object.FindObjectOfType<SubtitleUIHandler>();
                if ((object)freshSub == null)
                {
                    _subtitleHandler = null;
                    return;
                }

                // SAFETY: ProbeObject before accessing properties to prevent AV
                // on partially-destroyed objects returned by FindObjectOfType
                if (!SafeCall.ProbeObject(freshSub.Pointer))
                {
                    _subtitleHandler = null;
                    return;
                }

                string chapter = "";
                string subtitle = "";

                var chapterTmp = freshSub.chapterText;
                if ((object)chapterTmp != null && SafeCall.TmpTextMethodAvailable)
                {
                    IntPtr strPtr = SafeCall.ReadTmpTextSafe(chapterTmp.Pointer);
                    if (strPtr != IntPtr.Zero)
                    {
                        try { chapter = IL2CPP.Il2CppStringToManaged(strPtr) ?? ""; } catch { }
                    }
                }

                var subtitleTmp = freshSub.subtitleText;
                if ((object)subtitleTmp != null && SafeCall.TmpTextMethodAvailable)
                {
                    IntPtr strPtr = SafeCall.ReadTmpTextSafe(subtitleTmp.Pointer);
                    if (strPtr != IntPtr.Zero)
                    {
                        try { subtitle = IL2CPP.Il2CppStringToManaged(strPtr) ?? ""; } catch { }
                    }
                }

                chapter = TextUtils.CleanRichText(chapter);
                subtitle = TextUtils.CleanRichText(subtitle);

                if (!string.IsNullOrWhiteSpace(chapter) && chapter != _lastChapterText)
                {
                    _lastChapterText = chapter;
                    ScreenReaderOutput.Say(chapter);
                }

                if (!string.IsNullOrWhiteSpace(subtitle) && subtitle != _lastSubtitleText)
                {
                    _lastSubtitleText = subtitle;
                    ScreenReaderOutput.Say(subtitle);
                }
            }
            catch
            {
                _subtitleHandler = null;
            }
        }

        private bool TryReadSDemoSystem(out string text, out string speaker, out IntPtr dialoguePtr)
        {
            text = null;
            speaker = null;
            dialoguePtr = IntPtr.Zero;

            if ((object)_dialogueMessage == null) return false;

            // SAFETY: Use FindObjectOfType for fresh ref every poll.
            // Cached IL2CPP refs stay non-zero (Pointer) after Unity destroys
            // the native object, causing uncatchable AccessViolationException
            // in .NET 6. FindObjectOfType returns null for destroyed objects
            // (removed from Unity's list before memory freed), giving a
            // microsecond TOCTOU window vs seconds with cached ref.
            SDemoDialogueMessage freshMsg;
            try
            {
                freshMsg = UnityEngine.Object.FindObjectOfType<SDemoDialogueMessage>();
            }
            catch
            {
                _dialogueMessage = null;
                ReleaseDialogueRefs();
                return false;
            }

            if ((object)freshMsg == null)
            {
                // Object destroyed - adventure scene ending
                ReleaseDialogueRefs();
                return false;
            }

            // SAFETY: ProbeObject before accessing properties to prevent AV
            if (!SafeCall.ProbeObject(freshMsg.Pointer))
            {
                _dialogueMessage = null;
                ReleaseDialogueRefs();
                return false;
            }

            try
            {
                // SAFETY: Use SafeCall to read string field. IL2CPP string field access
                // internally calls Il2CppStringToManaged which reads native string memory.
                // If the string has been freed during scene transition, this causes an
                // uncatchable AV in .NET 6. SafeCall reads the string pointer via VEH
                // and probes the string object before Il2CppStringToManaged.
                if (SafeCall.AdventureFieldsAvailable)
                {
                    text = SafeCall.ReadIl2CppStringFieldSafe(freshMsg.Pointer, SafeCall.OffsetSDemoCurrentShowingMessage);
                    // Read raw IL2CPP string pointer for same-text detection.
                    // Even when text content is identical, different dialogue entries
                    // produce different string objects with different pointers.
                    dialoguePtr = SafeCall.ReadFieldPtrSafe(freshMsg.Pointer, SafeCall.OffsetSDemoCurrentShowingMessage);
                }
                else
                    text = freshMsg._currentShowingMessage;

                if (string.IsNullOrWhiteSpace(text))
                {
                    ReleaseDialogueRefs();
                    text = null;
                    return false;
                }
            }
            catch
            {
                _dialogueMessage = null;
                return false;
            }

            speaker = ReadSpeakerName();
            return true;
        }

        private bool TryReadTextDrawer(out string text, out string speaker)
        {
            text = null;
            speaker = null;

            if ((object)_textDrawer == null) return false;

            SDemoDialogueTextDrawer freshDrawer;
            try
            {
                freshDrawer = UnityEngine.Object.FindObjectOfType<SDemoDialogueTextDrawer>();
            }
            catch
            {
                _textDrawer = null;
                ReleaseDialogueRefs();
                return false;
            }

            if ((object)freshDrawer == null)
            {
                ReleaseDialogueRefs();
                return false;
            }

            // SAFETY: ProbeObject before accessing properties to prevent AV
            if (!SafeCall.ProbeObject(freshDrawer.Pointer))
            {
                _textDrawer = null;
                ReleaseDialogueRefs();
                return false;
            }

            try
            {
                var textComp = freshDrawer._text;
                if ((object)textComp == null) return false;

                // CRITICAL: Use SafeCall to read text
                if (SafeCall.TmpTextMethodAvailable)
                {
                    IntPtr strPtr = SafeCall.ReadTmpTextSafe(textComp.Pointer);
                    if (strPtr != IntPtr.Zero)
                    {
                        try { text = IL2CPP.Il2CppStringToManaged(strPtr); } catch { }
                    }
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    ReleaseDialogueRefs();
                    text = null;
                    return false;
                }
            }
            catch
            {
                _textDrawer = null;
                return false;
            }

            speaker = ReadSpeakerName();
            return true;
        }

        private string ReadSpeakerName()
        {
            if ((object)_speakerName == null) return null;
            try
            {
                var freshSpeaker = UnityEngine.Object.FindObjectOfType<SDemoSpeakerName>();
                if ((object)freshSpeaker != null && SafeCall.ProbeObject(freshSpeaker.Pointer))
                {
                    // SAFETY: Use SafeCall to read string field (prevents AV on freed string)
                    if (SafeCall.AdventureFieldsAvailable && SafeCall.OffsetSDemoSpeakerName > 0)
                        return SafeCall.ReadIl2CppStringFieldSafe(freshSpeaker.Pointer, SafeCall.OffsetSDemoSpeakerName);
                    return freshSpeaker._speakerName;
                }
                _speakerName = null;
            }
            catch
            {
                _speakerName = null;
            }
            return null;
        }

        /// <summary>
        /// Release all dialogue IL2CPP refs and set search cooldown.
        /// Called when dialogue text goes empty (scene likely ending).
        /// Prevents accessing freed native memory on the next poll cycle.
        /// </summary>
        private void ReleaseDialogueRefs()
        {
            _dialogueMessage = null;
            _speakerName = null;
            _textDrawer = null;
            _sceneObjects = null;
            // Also release subtitle handler - its native object gets destroyed
            // during scene transitions. Pointer stays non-zero on destroyed objects,
            // so ReadSubtitles() would pass the Pointer check and AV on .chapterText.
            _subtitleHandler = null;
            _lastSubtitleText = "";
            _lastChapterText = "";
            // Clear dialogue tracking so same text reappearing after transition
            // is announced (e.g., scene end → new scene with same first line).
            _lastDialogueText = "";
            _lastSpeakerName = "";
            _lastDialoguePtr = IntPtr.Zero;
            _searchCooldown = ModConfig.AdvSearchCooldownEffective;
            _staleTextCount = 0;
            RefsJustReleased = true;
            DebugHelper.Write($"ADH: Dialogue text empty, released refs (cooldown {ModConfig.AdvSearchCooldownEffective} cycles)");
        }

        private bool TryReadSceneObjects(out string text, out string speaker)
        {
            text = null;
            speaker = null;

            if ((object)_sceneObjects == null) return false;

            AdventureSceneObjects freshScene;
            try
            {
                freshScene = UnityEngine.Object.FindObjectOfType<AdventureSceneObjects>();
            }
            catch
            {
                _sceneObjects = null;
                ReleaseDialogueRefs();
                return false;
            }

            if ((object)freshScene == null)
            {
                ReleaseDialogueRefs();
                return false;
            }

            // SAFETY: ProbeObject before accessing properties to prevent AV
            if (!SafeCall.ProbeObject(freshScene.Pointer))
            {
                _sceneObjects = null;
                ReleaseDialogueRefs();
                return false;
            }

            try
            {
                var dialogTmp = freshScene.dialogText;
                if ((object)dialogTmp != null && SafeCall.TmpTextMethodAvailable)
                {
                    IntPtr strPtr = SafeCall.ReadTmpTextSafe(dialogTmp.Pointer);
                    if (strPtr != IntPtr.Zero)
                    {
                        try { text = IL2CPP.Il2CppStringToManaged(strPtr); } catch { }
                    }
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    ReleaseDialogueRefs();
                    text = null;
                    return false;
                }

                var speakerTmp = freshScene.speakerNameText;
                if ((object)speakerTmp != null && SafeCall.TmpTextMethodAvailable)
                {
                    IntPtr strPtr = SafeCall.ReadTmpTextSafe(speakerTmp.Pointer);
                    if (strPtr != IntPtr.Zero)
                    {
                        try { speaker = IL2CPP.Il2CppStringToManaged(strPtr); } catch { }
                    }
                }
            }
            catch
            {
                _sceneObjects = null;
                return false;
            }

            return true;
        }

        private void FindDialogueObjects()
        {
            // Batch search: find ALL missing object types in one call.
            // Each FindObjectOfType for these small MonoBehaviour types is
            // lightweight (~0.1ms), and batching reduces initial discovery
            // from ~500ms (5 types × 3 poll slots) to ~33ms (1 poll cycle).
            try
            {
                if ((object)_dialogueMessage == null)
                {
                    _dialogueMessage = UnityEngine.Object.FindObjectOfType<SDemoDialogueMessage>();
                    if ((object)_dialogueMessage != null)
                    {
                        DebugHelper.Write("ADH: Found SDemoDialogueMessage");
                        // SDemoSpeakerName is always paired with SDemoDialogueMessage.
                        // Find it now to avoid announcing the first line without speaker name.
                        if ((object)_speakerName == null)
                        {
                            _speakerName = UnityEngine.Object.FindObjectOfType<SDemoSpeakerName>();
                            if ((object)_speakerName != null)
                                DebugHelper.Write("ADH: Found SDemoSpeakerName (paired)");
                        }
                    }
                }

                if ((object)_speakerName == null)
                {
                    _speakerName = UnityEngine.Object.FindObjectOfType<SDemoSpeakerName>();
                    if ((object)_speakerName != null)
                        DebugHelper.Write("ADH: Found SDemoSpeakerName");
                }

                if ((object)_textDrawer == null)
                {
                    _textDrawer = UnityEngine.Object.FindObjectOfType<SDemoDialogueTextDrawer>();
                    if ((object)_textDrawer != null)
                        DebugHelper.Write("ADH: Found SDemoDialogueTextDrawer");
                }

                if ((object)_subtitleHandler == null)
                {
                    _subtitleHandler = UnityEngine.Object.FindObjectOfType<SubtitleUIHandler>();
                    if ((object)_subtitleHandler != null)
                        DebugHelper.Write("ADH: Found SubtitleUIHandler");
                }

                if ((object)_sceneObjects == null)
                {
                    _sceneObjects = UnityEngine.Object.FindObjectOfType<AdventureSceneObjects>();
                    if ((object)_sceneObjects != null)
                        DebugHelper.Write("ADH: Found AdventureSceneObjects");
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"ADH: FindObjectOfType error: {ex.Message}");
            }
        }

        /// <summary>
        /// Collect current adventure dialogue info for screen review.
        /// </summary>
        public void CollectReviewItems(System.Collections.Generic.List<string> items)
        {
            // Current dialogue
            if (!string.IsNullOrWhiteSpace(_lastDialogueText))
            {
                if (!string.IsNullOrWhiteSpace(_lastSpeakerName))
                    items.Add(Loc.Get("dialogue_line", _lastSpeakerName, _lastDialogueText));
                else
                    items.Add(_lastDialogueText);
            }

            // Chapter/subtitle
            if (!string.IsNullOrWhiteSpace(_lastChapterText))
                items.Add(_lastChapterText);
            if (!string.IsNullOrWhiteSpace(_lastSubtitleText))
                items.Add(_lastSubtitleText);
        }

        private void AnnounceDialogue(string speaker, string text)
        {
            string announcement;
            if (!string.IsNullOrWhiteSpace(speaker))
                announcement = Loc.Get("dialogue_line", speaker, text);
            else
                announcement = text;

            ScreenReaderOutput.Say(announcement);
            DebugHelper.Write($"Dialogue: [{speaker}] {text}");
        }

    }
}
