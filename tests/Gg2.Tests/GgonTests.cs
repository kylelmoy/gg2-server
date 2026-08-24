using Gg2.Maps;

namespace Gg2.Tests;

/// <summary>
/// The GGON parser, against the grammar in <c>Scripts/ggon/</c>.
/// </summary>
/// <remarks>
/// The point of most of these is that GGON is <em>not</em> JSON despite looking
/// like it: bare words are strings, quoting is single, and there are no numbers
/// at all — <c>{x: 2331}</c> yields the string "2331". A parser that returns a
/// number here would round-trip fine and then quietly lose precision on the one
/// map with a fractional scale.
/// </remarks>
public sealed class GgonTests
{
    [Fact]
    public void BareWordsAreStrings()
    {
        var map = Assert.IsType<GgonMap>(Ggon.Decode("{type:playerwall,x:534,yscale:7.30}"));

        Assert.Equal("playerwall", map.GetString("type"));
        Assert.Equal("534", map.GetString("x"));

        // Not 7.3: the text is preserved, and only the consumer decides what a
        // scale means numerically.
        Assert.Equal("7.30", map.GetString("yscale"));
    }

    [Fact]
    public void NegativeAndSignedBareWordsAreOneToken()
    {
        var map = Assert.IsType<GgonMap>(Ggon.Decode("{distance:-1,other:+2}"));

        Assert.Equal("-1", map.GetString("distance"));
        Assert.Equal("+2", map.GetString("other"));
    }

    [Fact]
    public void ListDecodesToAMapWithLength()
    {
        var map = Assert.IsType<GgonMap>(Ggon.Decode("[a,b,c]"));

        Assert.Equal("3", map.GetString("length"));
        Assert.Equal("a", map.GetString("0"));
        Assert.Equal("c", map.GetString("2"));

        var list = map.ToList();
        Assert.Equal(3, list.Count);
        Assert.Equal("b", list[1]!.AsString);
    }

    [Fact]
    public void EmptyListHasZeroLength()
    {
        var list = Assert.IsType<GgonMap>(Ggon.Decode("[]")).ToList();
        Assert.Empty(list);
    }

    [Fact]
    public void EmptyMapIsEmpty()
    {
        var map = Assert.IsType<GgonMap>(Ggon.Decode("{}"));
        Assert.Equal(0, map.Count);
    }

    /// <summary>
    /// A map written with numeric keys and a length is indistinguishable from a
    /// list once decoded — which is why <c>gg_debug</c>'s entity block loads at
    /// all. <c>ggon_map_to_list</c> is the only thing that resolves the two.
    /// </summary>
    [Fact]
    public void NumericallyKeyedMapReadsAsAList()
    {
        var map = Assert.IsType<GgonMap>(Ggon.Decode("{0:{type:a},10:{type:b},length:2}"));

        var list = map.ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal("a", list[0]!.AsMap!.GetString("type"));

        // Index 1 is absent, so the element is null rather than the "10" entry.
        Assert.Null(list[1]);
    }

    [Fact]
    public void QuotedStringsCarryPunctuationAndEscapes()
    {
        var map = Assert.IsType<GgonMap>(Ggon.Decode(@"{resource:'a,b{c}:d',nl:'x\ny',q:'it\'s'}"));

        Assert.Equal("a,b{c}:d", map.GetString("resource"));
        Assert.Equal("x\ny", map.GetString("nl"));
        Assert.Equal("it's", map.GetString("q"));
    }

    [Fact]
    public void WhitespaceBetweenTokensIsSkipped()
    {
        var map = Assert.IsType<GgonMap>(Ggon.Decode("{\n  a : 1 ,\r\n  b : 2\t}"));

        Assert.Equal("1", map.GetString("a"));
        Assert.Equal("2", map.GetString("b"));
    }

    [Fact]
    public void NestedMapsAndListsRoundTrip()
    {
        var root = Assert.IsType<GgonMap>(Ggon.Decode("[{a:[x,y]},{b:{c:d}}]")).ToList();

        Assert.Equal(2, root.Count);
        Assert.Equal("y", root[0]!.AsMap!.Get("a")!.AsMap!.ToList()[1]!.AsString);
        Assert.Equal("d", root[1]!.AsMap!.Get("b")!.AsMap!.GetString("c"));
    }

    [Theory]
    [InlineData("{a:1", "unexpected end")]      // unterminated map
    [InlineData("{a 1}", "after key")]          // missing colon
    [InlineData("{a:1;b:2}", "unexpected character")]
    [InlineData("[a b]", "after value")]        // missing comma
    [InlineData(@"{a:'\q'}", "escape sequence")]
    [InlineData("{a:'unterminated}", "unexpected end")]
    public void MalformedTextIsRejected(string text, string expectedFragment)
    {
        var error = Assert.Throws<GgonException>(() => Ggon.Decode(text));
        Assert.Contains(expectedFragment, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TopLevelStringIsAValidDocument()
    {
        // ggon_parse_tokens returns a bare string for a bare token; the entity
        // loader only ever passes it a map or a list, but the grammar allows it.
        Assert.Equal("bare", Ggon.Decode("bare").AsString);
    }
}
