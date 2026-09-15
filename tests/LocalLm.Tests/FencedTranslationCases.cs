namespace LocalLm.Tests;

public static class FencedTranslationCases
{
    public static TheoryData<string, string> Blocks => new()
    {
        { "~~~csharp\nvar x = 1;\n~~~~\n", "\nAfter" },
        { "  ~~~csharp\n  var x = 1;\n  ~~~\n", "\nAfter" },
        { "```csharp\nvar x = 1;\n````\n", "\nAfter" },
        { " ~~~csharp\n var x = 1;\n ~~~\n", "\nAfter" },
        { "   ~~~csharp\n   var x = 1;\n   ~~~\n", "\nAfter" },
        { " ```csharp\n var x = 1;\n ```\n", "\nAfter" },
        { "  ```csharp\n  var x = 1;\n  ```\n", "\nAfter" },
        { "   ```csharp\n   var x = 1;\n   ```\n", "\nAfter" },
        { "  ~~~csharp\n  var x = 1;\n~~~~~ \t\n", "\nAfter" },
        { "~~~csharp\r\nvar x = 1;\r\n~~~~\r\n", "\r\nAfter" },
        { "~~~csharp\rvar x = 1;\r~~~~\r", "\rAfter" },
        { "~~~csharp\nvar x = 1;\n~~~~", "" },
        { "~~~csharp\nvar x = 1;\n~~~~\n", "" },
        { "~~~csharp\nvar x = 1;", "" },
        { "```csharp\nvar x = 1;\n", "" },
        { "~~~csharp\nvar x = 1;\n\n\n", "" },
        { "   ~~~csharp\n\tvar x = 1;\n", "" },
        { "~~~text\nvar x = 1;\nTranslation performed by the local model: literal-code.\n", "" },
        { "~~~~csharp\n~~~\nvar x = 1;\n~~~~\n", "\nAfter" },
        { "~~~csharp\n~~~ not a closing fence\nvar x = 1;\n~~~~\n", "\nAfter" },
        { "> ~~~csharp\n> var x = 1;\n> ~~~~\n", "\nAfter" },
        { "- ~~~csharp\n  var x = 1;\n  ~~~~\n", "\nAfter" },
        { "1. ```csharp\n   var x = 1;\n   ````\n", "\nAfter" },
        { "> - ~~~csharp\n>   var x = 1;\n>   ~~~~\n", "\nAfter" },
        { "> ~~~csharp\n> var x = 1;\n", "\nAfter" },
        { "- ~~~csharp\n  var x = 1;\n\n", "After" },
    };
}
