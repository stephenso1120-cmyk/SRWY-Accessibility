namespace SRWYAccess
{
    /// <summary>
    /// Shared text utilities for screen reader output.
    /// </summary>
    internal static class TextUtils
    {
        /// <summary>
        /// Remove TextMeshPro/Unity rich text tags (angle-bracket tags like color, sprite, etc.)
        /// and trim whitespace. Safe for null/empty input.
        /// </summary>
        internal static string CleanRichText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var sb = new System.Text.StringBuilder(text.Length);
            bool inTag = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '<') { inTag = true; continue; }
                if (c == '>') { inTag = false; continue; }
                if (inTag) continue;

                // Strip zero-width and invisible Unicode characters that
                // confuse screen readers or cause silent gaps in speech
                if (c == '\u200B'   // zero-width space
                    || c == '\u200C' // zero-width non-joiner
                    || c == '\u200D' // zero-width joiner
                    || c == '\uFEFF' // zero-width no-break space (BOM)
                    || c == '\u200E' // left-to-right mark
                    || c == '\u200F' // right-to-left mark
                    || c == '\u2060' // word joiner
                    || c == '\u2028' // line separator
                    || c == '\u2029') // paragraph separator
                    continue;

                // Convert non-breaking space to regular space
                if (c == '\u00A0')
                {
                    sb.Append(' ');
                    continue;
                }

                sb.Append(c);
            }
            return sb.ToString().Trim();
        }
    }
}
