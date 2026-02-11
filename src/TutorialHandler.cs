using System;
using MelonLoader;
using UnityEngine;
using Il2CppCom.BBStudio.SRTeam.Tutorial;
using Il2CppCom.BBStudio.SRTeam.UIs;

namespace SRWYAccess
{
    /// <summary>
    /// Handles tutorial window announcements.
    /// Detects when tutorials open, reads title and body text,
    /// tracks page changes, and announces navigation buttons.
    ///
    /// SAFETY: All IL2CPP object null checks use (object)x != null to bypass
    /// Unity's overloaded == operator, which accesses native pointers and can
    /// crash on destroyed objects.
    /// </summary>
    public class TutorialHandler
    {
        private bool _wasTutorialShowing;
        private string _lastTitle = "";
        private string _lastInfoText = "";
        private string _lastPageNum = "";
        private int _lastCursorIndex = -1;
        private TutorialManager _cachedTutorialManager;

        // Button names for the 3 tutorial navigation buttons
        private static readonly string[] _buttonLocKeys = new string[]
        {
            "tutorial_prev_page",
            "tutorial_skip",
            "tutorial_next_page"
        };

        public void ReleaseHandler()
        {
            _cachedTutorialManager = null;
            _wasTutorialShowing = false;
            _lastTitle = "";
            _lastInfoText = "";
            _lastPageNum = "";
            _lastCursorIndex = -1;
        }

        /// <summary>
        /// canSearch: if true, may call FindObjectOfType this cycle (round-robin controlled).
        /// </summary>
        public void Update(bool canSearch)
        {
            bool isShowing = false;
            TutorialManager tutMgr = null;

            try
            {
                tutMgr = GetTutorialManager(canSearch);
                if ((object)tutMgr != null)
                {
                    isShowing = tutMgr.isTutorialShow;
                }
            }
            catch
            {
                return;
            }

            // Tutorial just opened
            if (isShowing && !_wasTutorialShowing)
            {
                _wasTutorialShowing = true;
                ResetTrackingState();
                OnTutorialOpened(tutMgr);
                return;
            }

            // Tutorial just closed
            if (!isShowing && _wasTutorialShowing)
            {
                _wasTutorialShowing = false;
                ResetTrackingState();
                return;
            }

            // While tutorial is active, track changes
            if (isShowing)
            {
                TrackPageChange(tutMgr);
                TrackButtonNavigation(tutMgr);
            }
        }

        private void ResetTrackingState()
        {
            _lastTitle = "";
            _lastInfoText = "";
            _lastPageNum = "";
            _lastCursorIndex = -1;
        }

        private void OnTutorialOpened(TutorialManager tutMgr)
        {
            MelonLogger.Msg("[SRWYAccess] Tutorial opened");

            var handler = GetUIHandler(tutMgr);
            if ((object)handler == null) return;

            string title = ReadTitle(handler);
            string body = ReadInfoText(handler);
            string pageInfo = ReadPageInfo(handler);

            // Build announcement: "Tutorial: [title]. [body]. Page X of Y"
            var sb = new System.Text.StringBuilder();
            sb.Append(Loc.Get("tutorial_opened"));

            if (!string.IsNullOrEmpty(title))
            {
                sb.Append(" ");
                sb.Append(title);
                sb.Append(".");
            }

            if (!string.IsNullOrEmpty(body))
            {
                sb.Append(" ");
                sb.Append(body);
            }

            if (!string.IsNullOrEmpty(pageInfo))
            {
                sb.Append(" ");
                sb.Append(pageInfo);
            }

            ScreenReaderOutput.Say(sb.ToString());
        }

        private void TrackPageChange(TutorialManager tutMgr)
        {
            var handler = GetUIHandler(tutMgr);
            if ((object)handler == null) return;

            string currentPage = "";
            try
            {
                var windows = handler.windows;
                if ((object)windows != null)
                {
                    for (int i = 0; i < windows.Count; i++)
                    {
                        var win = windows[i];
                        if ((object)win == null) continue;
                        var pageTextComp = win.pageText;
                        if ((object)pageTextComp != null)
                        {
                            currentPage = pageTextComp.text ?? "";
                            break;
                        }
                    }
                }
            }
            catch { }

            if (!string.IsNullOrEmpty(currentPage) && currentPage != _lastPageNum)
            {
                _lastPageNum = currentPage;

                // Page changed - re-read content
                string body = ReadInfoText(handler);
                string pageInfo = ReadPageInfo(handler);

                var sb = new System.Text.StringBuilder();
                if (!string.IsNullOrEmpty(body))
                {
                    sb.Append(body);
                }
                if (!string.IsNullOrEmpty(pageInfo))
                {
                    if (sb.Length > 0) sb.Append(" ");
                    sb.Append(pageInfo);
                }

                if (sb.Length > 0)
                {
                    ScreenReaderOutput.Say(sb.ToString());
                }
            }
        }

        private void TrackButtonNavigation(TutorialManager tutMgr)
        {
            var handler = GetUIHandler(tutMgr);
            if ((object)handler == null) return;

            try
            {
                int cursorIndex = handler.currentCursorIndex;
                if (cursorIndex != _lastCursorIndex && _lastCursorIndex >= 0)
                {
                    if (cursorIndex >= 0 && cursorIndex < _buttonLocKeys.Length)
                    {
                        string buttonName = Loc.Get(_buttonLocKeys[cursorIndex]);
                        ScreenReaderOutput.Say(buttonName);
                    }
                }
                _lastCursorIndex = cursorIndex;
            }
            catch { }
        }

        private TutorialUIHandler GetUIHandler(TutorialManager tutMgr)
        {
            if ((object)tutMgr == null) return null;
            try
            {
                if (tutMgr.Pointer == System.IntPtr.Zero)
                {
                    _cachedTutorialManager = null;
                    return null;
                }
                var handler = tutMgr.tutorialUIHandler;
                if ((object)handler == null) return null;
                if (handler.Pointer == System.IntPtr.Zero) return null;
                return handler;
            }
            catch
            {
                _cachedTutorialManager = null;
                return null;
            }
        }

        private string ReadTitle(TutorialUIHandler handler)
        {
            try
            {
                var windows = handler.windows;
                if ((object)windows != null)
                {
                    for (int i = 0; i < windows.Count; i++)
                    {
                        var win = windows[i];
                        if ((object)win == null) continue;
                        var titleComp = win.titleText;
                        if ((object)titleComp != null)
                        {
                            string text = titleComp.text;
                            if (!string.IsNullOrEmpty(text))
                            {
                                _lastTitle = TextUtils.CleanRichText(text);
                                return _lastTitle;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"TutorialHandler: ReadTitle error: {ex.Message}");
            }

            return _lastTitle;
        }

        private string ReadInfoText(TutorialUIHandler handler)
        {
            // First try the cached currentInfoText property
            try
            {
                string infoText = handler.currentInfoText;
                if (!string.IsNullOrEmpty(infoText))
                {
                    _lastInfoText = TextUtils.CleanRichText(infoText);
                    return _lastInfoText;
                }
            }
            catch { }

            // Fallback: read from window's infoText TextMeshPro component
            try
            {
                var windows = handler.windows;
                if ((object)windows != null)
                {
                    for (int i = 0; i < windows.Count; i++)
                    {
                        var win = windows[i];
                        if ((object)win == null) continue;
                        var infoComp = win.infoText;
                        if ((object)infoComp != null)
                        {
                            string text = infoComp.text;
                            if (!string.IsNullOrEmpty(text))
                            {
                                _lastInfoText = TextUtils.CleanRichText(text);
                                return _lastInfoText;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"TutorialHandler: ReadInfoText error: {ex.Message}");
            }

            return _lastInfoText;
        }

        private string ReadPageInfo(TutorialUIHandler handler)
        {
            string page = "";
            string total = "";

            try
            {
                var windows = handler.windows;
                if ((object)windows != null)
                {
                    for (int i = 0; i < windows.Count; i++)
                    {
                        var win = windows[i];
                        if ((object)win == null) continue;

                        var pageComp = win.pageText;
                        var totalComp = win.totalPageText;

                        if ((object)pageComp != null) page = pageComp.text ?? "";
                        if ((object)totalComp != null) total = totalComp.text ?? "";

                        if (!string.IsNullOrEmpty(page)) break;
                    }
                }
            }
            catch { }

            if (!string.IsNullOrEmpty(page) && !string.IsNullOrEmpty(total))
            {
                return Loc.Get("tutorial_page", page, total);
            }

            return "";
        }

        /// <summary>
        /// Collect current tutorial info for screen review.
        /// </summary>
        public void CollectReviewItems(System.Collections.Generic.List<string> items)
        {
            if (!_wasTutorialShowing) return;

            if (!string.IsNullOrWhiteSpace(_lastTitle))
                items.Add(Loc.Get("tutorial_opened") + " " + _lastTitle);

            if (!string.IsNullOrWhiteSpace(_lastInfoText))
                items.Add(_lastInfoText);

            if (!string.IsNullOrWhiteSpace(_lastPageNum))
                items.Add(_lastPageNum);
        }

        private TutorialManager GetTutorialManager(bool canSearch)
        {
            if ((object)_cachedTutorialManager != null)
            {
                try
                {
                    if (_cachedTutorialManager.Pointer != System.IntPtr.Zero)
                        return _cachedTutorialManager;
                }
                catch { }
                _cachedTutorialManager = null;
            }
            if (!canSearch) return null;
            try
            {
                _cachedTutorialManager = UnityEngine.Object.FindObjectOfType<TutorialManager>();
            }
            catch { }
            return _cachedTutorialManager;
        }

    }
}
