using System.Text;
using Zmodem.Protocol;

namespace Zmodem.Protocol.Tests;

public sealed class CodecTests
{
    [Fact]
    public void StandardCrcCheckValues()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("123456789");
        Assert.Equal(0x31c3, ZModemCodec.ComputeCrc16(bytes));
        Assert.Equal(0xcbf43926u, ZModemCodec.ComputeCrc32(bytes));
    }

    [Fact]
    public void MatchesPublishedHexInitVector()
    {
        // Common lrzsz ZRINIT: CANFDX | CANOVIO | CANFC32, unlimited buffer.
        byte[] expected = Encoding.ASCII.GetBytes("**\x18" + "B0100000023be50\r\n\x11");
        Assert.Equal(expected, ZModemCodec.EncodeHeader(new ZModemHeader(ZModemHeaderType.Zrinit, 0x23000000)));
        var decoded = Assert.IsType<ZModemWireEvent.HeaderReceived>(Assert.Single(new ZModemStreamDecoder().Feed(expected)));
        Assert.Equal(0x23000000u, decoded.Header.Position);
    }

    [Theory]
    [InlineData(ZModemHeaderFormat.Hex)]
    [InlineData(ZModemHeaderFormat.Binary16)]
    [InlineData(ZModemHeaderFormat.Binary32)]
    public void HeaderRoundTripsEverySplitPoint(ZModemHeaderFormat format)
    {
        byte[] wire = ZModemCodec.EncodeHeader(new ZModemHeader(ZModemHeaderType.Zrpos, 0xff181113, format));
        for (int split = 0; split <= wire.Length; split++)
        {
            var decoder = new ZModemStreamDecoder();
            var events = decoder.Feed(wire.Take(split).ToArray()).Concat(decoder.Feed(wire.Skip(split).ToArray())).ToArray();
            var decoded = Assert.IsType<ZModemWireEvent.HeaderReceived>(Assert.Single(events));
            Assert.Equal(0xff181113u, decoded.Header.Position);
            Assert.Equal(format, decoded.Header.Format);
        }
    }

    [Theory]
    [InlineData(false, ZModemDataEnd.End)]
    [InlineData(false, ZModemDataEnd.Continue)]
    [InlineData(false, ZModemDataEnd.ContinueAndAck)]
    [InlineData(false, ZModemDataEnd.EndAndAck)]
    [InlineData(true, ZModemDataEnd.End)]
    [InlineData(true, ZModemDataEnd.Continue)]
    [InlineData(true, ZModemDataEnd.ContinueAndAck)]
    [InlineData(true, ZModemDataEnd.EndAndAck)]
    public void DataRoundTripsAllBytesWithSingleByteFragments(bool crc32, ZModemDataEnd end)
    {
        byte[] payload = Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray();
        byte[] wire = ZModemCodec.EncodeHeader(new ZModemHeader(ZModemHeaderType.Zdata, 0, crc32 ? ZModemHeaderFormat.Binary32 : ZModemHeaderFormat.Binary16))
            .Concat(ZModemCodec.EncodeData(payload, end, crc32)).ToArray();
        var decoder = new ZModemStreamDecoder();
        var events = wire.SelectMany(value => decoder.Feed(new[] { value })).ToArray();
        Assert.Equal(2, events.Length);
        var data = Assert.IsType<ZModemWireEvent.DataReceived>(events[1]);
        Assert.Equal(payload, data.Payload);
        Assert.Equal(end, data.End);
    }

    [Fact]
    public void StreamingSubpacketsAndHeaderCanBeCoalesced()
    {
        byte[] wire = ZModemCodec.EncodeHeader(new ZModemHeader(ZModemHeaderType.Zdata, 0, ZModemHeaderFormat.Binary32))
            .Concat(ZModemCodec.EncodeData(new byte[] { 1 }, ZModemDataEnd.Continue))
            .Concat(ZModemCodec.EncodeData(new byte[] { 2 }, ZModemDataEnd.ContinueAndAck))
            .Concat(ZModemCodec.EncodeData(new byte[] { 3 }, ZModemDataEnd.End))
            .Concat(ZModemCodec.EncodeHeader(new ZModemHeader(ZModemHeaderType.Zeof, 3))).ToArray();
        var events = new ZModemStreamDecoder().Feed(wire);
        Assert.Equal(5, events.Count);
        Assert.Equal(new byte[] { 1, 2, 3 }, events.OfType<ZModemWireEvent.DataReceived>().SelectMany(x => x.Payload));
    }

    [Fact]
    public void CorruptChecksumIsRejectedAndFollowingHeaderResynchronizes()
    {
        byte[] bad = Encoding.ASCII.GetBytes("**\x18" + "B0100000023be51\r\n\x11");
        byte[] good = Encoding.ASCII.GetBytes("**\x18" + "B00000000000000\r\n\x11");
        var events = new ZModemStreamDecoder().Feed(bad.Concat(good).ToArray());
        Assert.IsType<ZModemWireEvent.InvalidFrame>(events[0]);
        Assert.IsType<ZModemWireEvent.HeaderReceived>(events[1]);
    }

    [Fact]
    public void CancellationSpansChunksAndDoesNotConfuseEscapedCan()
    {
        var decoder = new ZModemStreamDecoder();
        Assert.Empty(decoder.Feed(new byte[] { 24, 24, 24, 24 }));
        Assert.IsType<ZModemWireEvent.Cancelled>(Assert.Single(decoder.Feed(new byte[] { 24 })));
    }

    [Fact]
    public void NoiseAndUnterminatedDataUseBoundedBuffer()
    {
        var decoder = new ZModemStreamDecoder();
        decoder.Feed(ZModemCodec.EncodeHeader(new ZModemHeader(ZModemHeaderType.Zdata, 0, ZModemHeaderFormat.Binary16)));
        var events = decoder.Feed(Enumerable.Repeat((byte)0x55, 100000).ToArray());
        Assert.Single(events.OfType<ZModemWireEvent.InvalidFrame>());
        Assert.InRange(decoder.BufferedByteCount, 0, 2112);
    }

    [Fact]
    public void CodecArgumentsAreValidated()
    {
        Assert.Throws<ArgumentNullException>(() => ZModemCodec.ComputeCrc16(null!));
        Assert.Throws<ArgumentNullException>(() => ZModemCodec.EncodeHeader(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => ZModemCodec.EncodeData(new byte[1025], ZModemDataEnd.End));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZModemHeader((ZModemHeaderType)255));
        Assert.Throws<ArgumentException>(() => new ZModemFileDescriptor("a\0b", 0));
    }
}
