using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

/// <summary>
/// Whether an edit can change how code in another file binds.
///
/// It can only do that by changing what the file declares. A body is invisible to everyone else:
/// no name lookup, no overload resolution and no inheritance depends on it. So if the declared
/// surface is unchanged, the occurrences recorded for every other file stay correct as they are,
/// and only the edited file needs recomputing.
///
/// The rule is equality, deliberately, rather than a classification of differences: adding a
/// member is not safe either -- a new overload or a new extension method can send an existing call
/// site somewhere else -- and a rule that tried to say which additions are harmless would be a
/// rule that is wrong in a way nobody notices.
/// </summary>
public sealed class WhetherAnEditCanChangeHowOtherCodeBindsTests
{
    [Fact]
    public void A_body_is_invisible_from_outside()
    {
        const string before =
            """
            namespace Demo;
            public class Service
            {
                public int Total(int a, int b) { return a + b; }
            }
            """;
        const string after =
            """
            namespace Demo;
            public class Service
            {
                public int Total(int a, int b) { var sum = a + b; return sum; }
            }
            """;

        Assert.True(DeclaredSurface.Unchanged(before, after));
    }

    [Fact]
    public void Comments_and_spacing_are_invisible_from_outside()
    {
        const string before =
            """
            namespace Demo;
            public class Service
            {
                public int Total(int a, int b) { return a + b; }
            }
            """;
        const string after =
            """
            namespace Demo;

            // Adds them up.
            public class Service
            {

                public int Total(int a, int b) { return a + b; }

            }
            """;

        Assert.True(DeclaredSurface.Unchanged(before, after));
    }

    /// <summary>
    /// Moving a member changes no lookup: C# has no ordering rule that another file can see.
    /// </summary>
    [Fact]
    public void The_order_of_members_is_invisible_from_outside()
    {
        const string before =
            """
            namespace Demo;
            public class Service
            {
                public int A() { return 1; }
                public int B() { return 2; }
            }
            """;
        const string after =
            """
            namespace Demo;
            public class Service
            {
                public int B() { return 2; }
                public int A() { return 1; }
            }
            """;

        Assert.True(DeclaredSurface.Unchanged(before, after));
    }

    [Theory]
    // A new member: an overload can capture a call site that used to bind elsewhere.
    [InlineData("public int Total(int a, int b) { return 0; } public int Total(long a, long b) { return 0; }")]
    // A changed signature.
    [InlineData("public int Total(int a, long b) { return 0; }")]
    // A changed return type.
    [InlineData("public long Total(int a, int b) { return 0; }")]
    // A changed accessibility: what another assembly can see.
    [InlineData("internal int Total(int a, int b) { return 0; }")]
    // A new member with a new name: it can shadow one inherited from elsewhere.
    [InlineData("public int Total(int a, int b) { return 0; } public int Extra() { return 0; }")]
    public void A_change_to_what_is_declared_is_visible_from_outside(string members)
    {
        const string before =
            """
            namespace Demo;
            public class Service
            {
                public int Total(int a, int b) { return 0; }
            }
            """;
        var after = "namespace Demo; public class Service { " + members + " }";

        Assert.False(DeclaredSurface.Unchanged(before, after));
    }

    [Fact]
    public void A_changed_base_list_is_visible_from_outside()
    {
        const string before =
            """
            namespace Demo;
            public class Service { }
            """;
        const string after =
            """
            namespace Demo;
            public class Service : IDisposable { }
            """;

        Assert.False(DeclaredSurface.Unchanged(before, after));
    }

    [Fact]
    public void A_new_type_is_visible_from_outside()
    {
        const string before =
            """
            namespace Demo;
            public class Service { }
            """;
        const string after =
            """
            namespace Demo;
            public class Service { }
            public enum Colour { Red }
            """;

        Assert.False(DeclaredSurface.Unchanged(before, after));
    }

    /// <summary>
    /// A file that does not parse has no surface anyone can rely on. Treating it as unchanged
    /// would keep a stale one; the safe answer is that it differs from everything, including
    /// itself.
    /// </summary>
    [Fact]
    public void Source_that_does_not_parse_is_never_equal_to_anything()
    {
        const string broken = "namespace Demo; public class Service { public int";

        Assert.False(DeclaredSurface.Unchanged(broken, broken));
    }
}
