using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Meerkat.Widgets.Notes.Markdown;

namespace Meerkat.Widgets.Notes.Views;

/// <summary>A text block that shows one line of Markdown with inline code, bold and italic text.</summary>
public sealed class MarkdownTextBlock : TextBlock
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown),
        typeof(string),
        typeof(MarkdownTextBlock),
        new PropertyMetadata(string.Empty, (sender, _) => ((MarkdownTextBlock)sender).Render()));

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private void Render()
    {
        Inlines.Clear();
        foreach (var run in MarkdownInline.Parse(Markdown ?? string.Empty))
        {
            var inline = new Run(run.Text);
            if (run.Style.HasFlag(InlineStyle.Bold))
            {
                inline.FontWeight = FontWeights.SemiBold;
            }

            if (run.Style.HasFlag(InlineStyle.Italic))
            {
                inline.FontStyle = FontStyles.Italic;
            }

            if (run.Style.HasFlag(InlineStyle.Code))
            {
                inline.SetResourceReference(TextElement.FontFamilyProperty, "Overlay.MonoFont");
                inline.SetResourceReference(TextElement.BackgroundProperty, "Overlay.SurfaceRaised");
                inline.FontSize = FontSize * 0.92;
            }

            Inlines.Add(inline);
        }
    }
}
