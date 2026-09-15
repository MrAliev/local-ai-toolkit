using LocalLm.Core;

namespace LocalLm.Tests;

public sealed class TranslationValidatorTests
{
    [Theory]
    [MemberData(nameof(FencedTranslationCases.Blocks), MemberType = typeof(FencedTranslationCases))]
    public void Markdown_validator_preserves_every_supported_fenced_source_range(
        string block, string after)
    {
        var source = "# Before\n\n" + block + after;
        var translated = "# До\n\n" + block + after.Replace("After", "После", StringComparison.Ordinal);

        var unchanged = TranslationValidator.ValidateMarkdown(source, translated);
        var modified = TranslationValidator.ValidateMarkdown(source,
            translated.Replace("var x = 1;", "var y = 999;", StringComparison.Ordinal));

        Assert.True(unchanged.Passed, unchanged.Detail);
        Assert.False(modified.Passed);
    }

    [Theory]
    [InlineData("~~~\nvar x = 1;\n~~~\n", "~~~\nvar y = 2;\n~~~\n")]
    [InlineData("```\nvar x = 1;\n```\n", "```\nvar y = 2;\n```\n")]
    public void Markdown_validator_rejects_reordering_protected_blocks(string first, string second)
    {
        Assert.False(TranslationValidator.ValidateMarkdown(first + "\n" + second,
            second + "\n" + first).Passed);
    }

    [Fact]
    public void Markdown_validator_rejects_code_hidden_in_a_different_Markdown_container()
    {
        const string block = "~~~\nvar x = 1;\n~~~\n";

        Assert.False(TranslationValidator.ValidateMarkdown(block, "<div>\n" + block + "</div>").Passed);
    }

    [Fact]
    public void Markdown_validator_preserves_structure_and_protected_tokens()
    {
        const string source =
            """
            # Heading

            - Item with `Code()` and [link](https://example.test/path)

            ```csharp
            Console.WriteLine("{value}");
            ```
            """;
        const string translated =
            """
            # Заголовок

            - Элемент с `Code()` и [ссылкой](https://example.test/path)

            ```csharp
            Console.WriteLine("{value}");
            ```
            """;

        var result = TranslationValidator.ValidateMarkdown(source, translated);

        Assert.True(result.Passed, result.Detail);
    }

    [Theory]
    [InlineData("# Heading\n\n`Code()`", "# Заголовок\n\n`Other()`")]
    [InlineData("# Heading\n\n- item", "Заголовок\n\n- элемент")]
    [InlineData("```\ncode\n```", "code")]
    public void Markdown_validator_rejects_structural_or_protected_token_loss(
        string source,
        string translated)
    {
        var result = TranslationValidator.ValidateMarkdown(source, translated);

        Assert.False(result.Passed);
    }

    [Fact]
    public void Markdown_validator_rejects_a_fence_merged_into_a_heading()
    {
        const string source =
            "# Heading\r\n\r\n" +
            "```text\r\nvalue\r\n```\r\n";
        const string translated =
            "# Заголовок```text\r\nvalue\r\n```\r\n";

        var result = TranslationValidator.ValidateMarkdown(source, translated);

        Assert.False(result.Passed);
    }

    [Theory]
    [InlineData("Hello", "")]
    [InlineData("English version", "Translate the following fragment from English to Russian.")]
    [InlineData("English version", "```\nprompt\n```")]
    [InlineData("One", "This response expanded far beyond a plausible short translation and contains unrelated instructions.")]
    public void Plain_validator_rejects_empty_leaked_or_anomalously_expanded_output(
        string source,
        string translated)
    {
        var result = TranslationValidator.ValidatePlain(source, translated);

        Assert.False(result.Passed);
    }

    [Fact]
    public void Plain_validator_accepts_a_plausible_translation()
    {
        var result = TranslationValidator.ValidatePlain(
            "English version",
            "Английская версия");

        Assert.True(result.Passed, result.Detail);
    }
}
