using TimboJimbo.UINext.Text;
using UnityEngine;

namespace TimboJimbo.UINext
{
    /// <summary>Text fields from code, as <see cref="UIBuild"/> builds the rest.</summary>
    public static class TextFieldBuild
    {
        /// <summary>
        /// Makes <paramref name="node"/> a text field: padded by <paramref name="padding"/>, clipping its text at its
        /// edge (rounded by its ClipRadii), one line high, or with <paramref name="multiline"/> growing with its lines up
        /// to <paramref name="maxLines"/> (0: all of them) before it scrolls, with its text on a node inside it. Give it
        /// a width, and a Box for its background; its values (a border's colour <c>When(Focused, ...)</c>) follow its
        /// states.
        /// </summary>
        public static TextField TextField(this UINode node, string placeholder = "", float fontSize = 17f, bool multiline = false,
            int maxLines = 5, Insets padding = default)
        {
            var field = node.GetComponent<TextField>();
            if (field == null)
                field = node.gameObject.AddComponent<TextField>();
            var config = field.Config;
            config.Multiline = multiline;
            field.Config = config;
            field.Placeholder.Value = placeholder;
            field.FontSize.Value = fontSize;
            node.Padding.Value = padding;
            node.Clips.Value = true;
            float line = TextLayout.LineHeightOf(null, fontSize);
            node.Height.Value = multiline
                ? Sizing.Fit(line + padding.Vertical, maxLines > 0 ? line * maxLines + padding.Vertical : 0f)
                : Sizing.Fit();
            var text = node.Child("Text").Sized(Sizing.Grow(), Sizing.Fit());
            field.Setup(text.gameObject.AddComponent<TextFieldText>());
            return field;
        }
    }
}
