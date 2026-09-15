using LocalLm.Core;

namespace LocalLm.Tests;

public sealed class TranslationProtectorTests
{
    [Theory]
    [MemberData(nameof(FencedTranslationCases.Blocks), MemberType = typeof(FencedTranslationCases))]
    public void Markdown_fences_are_excluded_from_translation_without_reformatting(
        string block, string after)
    {
        var source = "# Before\n\n" + block + after;

        var parts = TranslationProtector.SplitFencedCode(source);

        Assert.Equal(block, Assert.Single(parts, part => !part.IsTranslatable).Text);
        Assert.DoesNotContain("var x", string.Concat(parts.Where(part => part.IsTranslatable)
            .Select(part => part.Text)), StringComparison.Ordinal);
        Assert.Equal(source, string.Concat(parts.Select(part => part.Text)));
    }

    [Theory]
    [MemberData(nameof(FencedTranslationCases.Blocks), MemberType = typeof(FencedTranslationCases))]
    public void Protected_fences_round_trip_the_original_source_ranges(string block, string after)
    {
        var source = "# Before\n\n" + block + after;

        var protectedText = TranslationProtector.ProtectFencedCode(source);

        Assert.Equal(block, Assert.Single(protectedText.Segments).Original);
        Assert.DoesNotContain("var x", protectedText.Text, StringComparison.Ordinal);
        Assert.Equal(source, protectedText.Restore(protectedText.Text));
    }

    [Theory]
    [InlineData("Text with ~~~ in the middle of a sentence.")]
    [InlineData("<!--\n~~~\nvar x = 1;\n~~~\n-->")]
    [InlineData("<div>\n~~~\nvar x = 1;\n~~~\n</div>")]
    [InlineData("    ~~~\n    var x = 1;\n    ~~~")]
    public void Non_fenced_Markdown_is_not_mistaken_for_a_fenced_block(string source)
    {
        Assert.True(Assert.Single(TranslationProtector.SplitFencedCode(source)).IsTranslatable);
        Assert.Empty(TranslationProtector.ProtectFencedCode(source).Segments);
    }

    [Fact]
    public void Fenced_code_is_removed_from_model_input_and_restored_exactly()
    {
        const string source =
            "# Heading\r\n\r\nText.\r\n\r\n" +
            "```powershell\r\n" +
            "dotnet test LocalAi.slnx --configuration Release\r\n" +
            "```\r\n";

        var protectedText = TranslationProtector.ProtectFencedCode(source);

        Assert.DoesNotContain("dotnet test", protectedText.Text, StringComparison.Ordinal);
        var token = Assert.Single(protectedText.Segments).Token;
        Assert.Contains(token, protectedText.Text, StringComparison.Ordinal);
        Assert.Equal(source, protectedText.Restore(protectedText.Text));
    }

    [Fact]
    public void Restore_rejects_a_missing_or_duplicated_placeholder()
    {
        var protectedText = TranslationProtector.ProtectFencedCode(
            "Before\r\n```text\r\nvalue\r\n```\r\nAfter");
        var token = Assert.Single(protectedText.Segments).Token;

        Assert.Throws<InvalidDataException>(
            () => protectedText.Restore(
                protectedText.Text.Replace(token, string.Empty, StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(
            () => protectedText.Restore(protectedText.Text + token));
    }

    [Fact]
    public void Split_marks_fenced_code_as_non_translatable_and_preserves_order()
    {
        const string source =
            "Before\r\n\r\n```text\r\nvalue\r\n```\r\n\r\nAfter";

        var parts = TranslationProtector.SplitFencedCode(source);

        Assert.Equal(3, parts.Count);
        Assert.True(parts[0].IsTranslatable);
        Assert.False(parts[1].IsTranslatable);
        Assert.True(parts[2].IsTranslatable);
        Assert.Equal(source, string.Concat(parts.Select(part => part.Text)));
    }
}
