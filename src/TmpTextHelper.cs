using System;
using Il2CppInterop.Runtime;
using Il2CppTMPro;

namespace SRWYAccess
{
    /// <summary>
    /// Helper methods for reading TextMeshPro (TMP) text safely.
    /// Reduces code duplication across handlers.
    /// </summary>
    internal static class TmpTextHelper
    {
        /// <summary>
        /// Safely reads text from a TextMeshProUGUI component.
        /// Handles all safety checks: null validation, VEH protection, rich text cleaning.
        /// </summary>
        /// <param name="tmp">The TMP component to read from</param>
        /// <param name="cleanRichText">If true, removes rich text tags like &lt;color&gt;</param>
        /// <returns>The text content, or null if reading failed</returns>
        public static string ReadTextSafe(TextMeshProUGUI tmp, bool cleanRichText = true)
        {
            // Validate TMP object
            if ((object)tmp == null || tmp.Pointer == IntPtr.Zero)
                return null;

            // Try VEH-protected read if available
            if (SafeCall.TmpTextMethodAvailable)
            {
                IntPtr il2cppStrPtr = SafeCall.ReadTmpTextSafe(tmp.Pointer);
                if (il2cppStrPtr != IntPtr.Zero)
                {
                    try
                    {
                        string text = IL2CPP.Il2CppStringToManaged(il2cppStrPtr);
                        if (!string.IsNullOrEmpty(text))
                        {
                            return cleanRichText ? TextUtils.CleanRichText(text) : text;
                        }
                    }
                    catch
                    {
                        // IL2CPP string conversion failed
                    }
                }
            }

            // Fallback: direct access (risky, but last resort)
            try
            {
                if (!SafeCall.ProbeObject(tmp.Pointer))
                    return null;

                string text = tmp.text;
                if (!string.IsNullOrEmpty(text))
                {
                    return cleanRichText ? TextUtils.CleanRichText(text) : text;
                }
            }
            catch
            {
                // Direct access failed (likely AV)
            }

            return null;
        }

        /// <summary>
        /// Reads multiple TMP texts and concatenates with separator.
        /// Skips null or empty texts.
        /// </summary>
        /// <param name="tmps">Array of TMP components</param>
        /// <param name="separator">Separator between texts (default: two spaces)</param>
        /// <returns>Concatenated text, or null if all reads failed</returns>
        public static string ReadMultipleTexts(TextMeshProUGUI[] tmps, string separator = "  ")
        {
            if (tmps == null || tmps.Length == 0)
                return null;

            System.Collections.Generic.List<string> parts = new System.Collections.Generic.List<string>();

            foreach (var tmp in tmps)
            {
                string text = ReadTextSafe(tmp);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    parts.Add(text);
                }
            }

            return parts.Count > 0 ? string.Join(separator, parts) : null;
        }

        /// <summary>
        /// Checks if a TMP component has visible text (not null, empty, or whitespace).
        /// </summary>
        public static bool HasVisibleText(TextMeshProUGUI tmp)
        {
            string text = ReadTextSafe(tmp);
            return !string.IsNullOrWhiteSpace(text);
        }

        /// <summary>
        /// Reads text and filters out numeric-only strings.
        /// Useful for extracting meaningful labels from UI that contains counters.
        /// </summary>
        public static string ReadNonNumericText(TextMeshProUGUI tmp)
        {
            string text = ReadTextSafe(tmp);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            // Check if text is purely numeric (digits, spaces, commas, periods)
            bool isNumeric = true;
            foreach (char c in text)
            {
                if (!char.IsDigit(c) && c != ' ' && c != ',' && c != '.' && c != '/')
                {
                    isNumeric = false;
                    break;
                }
            }

            return isNumeric ? null : text;
        }
    }
}
