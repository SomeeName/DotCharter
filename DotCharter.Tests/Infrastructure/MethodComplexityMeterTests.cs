namespace DotCharter.Tests.Infrastructure;

public sealed class MethodComplexityMeterTests
{
    private static MethodComplexityMeter.Measurement Single(string source, string key)
    {
        var all = MethodComplexityMeter.Measure("X.cs", source);
        return Assert.Single(all, m => m.Key == $"X.cs::{key}");
    }

    [Fact]
    public void AStraightLineMethod_IsOne()
    {
        const string source = "class C { int M() { var a = 1; return a + 2; } }";

        Assert.Equal(1, Single(source, "C.M(0)").Cyclomatic);
    }

    [Fact]
    public void EveryDecisionPointAddsOne()
    {
        const string source = """
            class C
            {
                int M(int a, bool b, string? s)
                {
                    if (a > 0 && b) return 1;          // if, &&
                    while (a < 0 || !b) a++;           // while, ||
                    for (var i = 0; i < 2; i++) { }    // for
                    foreach (var x in new[] { 1 }) { } // foreach
                    do { a--; } while (a > 9);         // do
                    try { a++; } catch (System.Exception) when (b) { } // catch, when
                    s ??= "x";                         // ??=
                    var n = s ?? "y";                  // ??
                    var t = b ? 1 : 2;                 // ?:
                    var u = a is 1 or 2;               // pattern or
                    return a switch { 1 => 1, 2 => 2, _ => 0 }; // two arms, the discard is free
                }
            }
            """;

        Assert.Equal(1 + 15, Single(source, "C.M(3)").Cyclomatic);
    }

    [Fact]
    public void ASwitchStatement_CountsCaseLabels_NotDefault()
    {
        const string source = """
            class C
            {
                void M(int a)
                {
                    switch (a)
                    {
                        case 1:
                        case 2: break;
                        case int n when n > 5: break;
                        default: break;
                    }
                }
            }
            """;

        // case 1, case 2, the pattern label and its when guard.
        Assert.Equal(1 + 4, Single(source, "C.M(1)").Cyclomatic);
    }

    [Fact]
    public void LambdasAndLocalFunctions_CountTowardsTheirMember()
    {
        const string source = """
            class C
            {
                int M(int[] xs)
                {
                    int Local(int x) => x > 0 ? x : 0;
                    return xs.Where(x => x > 1 && x < 9).Sum(Local);
                }
            }
            """;

        Assert.Equal(1 + 2, Single(source, "C.M(1)").Cyclomatic);
        Assert.DoesNotContain(MethodComplexityMeter.Measure("X.cs", source), m => m.Key.Contains("Local", StringComparison.Ordinal));
    }

    [Fact]
    public void Lines_SpanTheMember_WithoutItsDocComment()
    {
        const string source = """
            class C
            {
                /// <summary>Not counted.</summary>
                /// <remarks>Neither is this.</remarks>
                void M()
                {
                    var a = 1;
                }
            }
            """;

        Assert.Equal(4, Single(source, "C.M(0)").Lines);
    }

    [Fact]
    public void Keys_NameNestedTypesAccessorsAndOverloads()
    {
        const string source = """
            class Outer
            {
                class Inner
                {
                    int P { get { return 1; } set { } }
                    int Q => 2;
                    Inner(int a) { }
                    void M(int a) { }
                    void M(string a) { }
                    void M() { }
                }
            }
            """;

        var keys = MethodComplexityMeter.Measure("X.cs", source).Select(m => m.Key).ToList();
        string[] expected =
        [
            "X.cs::Outer.Inner.P.get", "X.cs::Outer.Inner.P.set", "X.cs::Outer.Inner.Q",
            "X.cs::Outer.Inner..ctor(1)", "X.cs::Outer.Inner.M(1)", "X.cs::Outer.Inner.M(1)#2",
            "X.cs::Outer.Inner.M(0)",
        ];

        Assert.Equal(expected, keys);
    }

    [Fact]
    public void UnsafeCode_IsNotMeasured()
    {
        const string source = """
            class C
            {
                unsafe void A(int* p) { }
                void B(byte[] b) { unsafe { fixed (byte* p = b) { } } }
                void D() { }
            }
            unsafe class U { void E() { } }
            """;

        var keys = MethodComplexityMeter.Measure("X.cs", source).Select(m => m.Key).ToList();

        Assert.Equal("X.cs::C.D(0)", Assert.Single(keys));
    }
}
