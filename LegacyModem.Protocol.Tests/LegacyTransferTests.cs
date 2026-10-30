namespace LegacyModem.Protocol.Tests
{
    public sealed class LegacyTransferTests
    {
        public static IEnumerable<object[]> Variants()
        {
            foreach (var variant in Enum.GetValues<LegacyModemVariant>())
            {
                foreach (var integrity in Enum.GetValues<LegacyModemIntegrity>())
                {
                    foreach (var size in new[] { 0, 1, 127, 128, 129, 769, 32769 })
                    {
                        yield return new object[] { variant, integrity, size };
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(Variants))]
        public void ByteLoopbackTransfersEmptyBoundaryAndWrappedFiles(LegacyModemVariant variant, LegacyModemIntegrity integrity, int size)
        {
            var bytes = Content(size);
            var options = new LegacyModemOptions(variant, integrity);
            var fixture = new LegacyWireFixture(new[] { bytes }, options, chunkSize: 1);
            var output = fixture.Run();
            Assert.Null(fixture.Failure);
            var actual = Assert.Single(output);
            Assert.Equal(bytes, actual.Take(bytes.Length));
            Assert.Equal(variant == LegacyModemVariant.Modem7 ? ((size + 127) / 128) * 128 : size, actual.Length);
        }

        [Theory]
        [InlineData(LegacyModemVariant.Modem7)]
        [InlineData(LegacyModemVariant.Telink)]
        [InlineData(LegacyModemVariant.SeaLink)]
        public void EmptyBatchAndMultiFileBatchComplete(LegacyModemVariant variant)
        {
            var options = new LegacyModemOptions(variant);
            var empty = new LegacyWireFixture(Array.Empty<byte[]>(), options);
            Assert.Empty(empty.Run());
            Assert.Null(empty.Failure);
            var fixture = new LegacyWireFixture(new[] { Content(129), Array.Empty<byte>(), Content(1024) }, options);
            var files = fixture.Run();
            Assert.Null(fixture.Failure);
            Assert.Equal(3, files.Count);
            Assert.Equal(Content(129), files[0].Take(129));
            Assert.Empty(files[1]);
            Assert.Equal(Content(1024), files[2]);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(6)]
        [InlineData(127)]
        public void SeaLinkHonorsNegotiatedWindow(int window)
        {
            var fixture = new LegacyWireFixture(new[] { Content(32769) }, new LegacyModemOptions(windowSize: window));
            Assert.Equal(Content(32769), Assert.Single(fixture.Run()));
            Assert.Null(fixture.Failure);
            Assert.Equal(window, fixture.MaximumOutstanding);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SeaLinkResumesAtAbsoluteBlock27(bool overdrive)
        {
            var fixture = new LegacyWireFixture(
                new[] { Content(8193) },
                new LegacyModemOptions(overdrive: overdrive),
                restartBlock: 27);
            Assert.Equal(Content(8193), Assert.Single(fixture.Run()));
            Assert.Null(fixture.Failure);
            Assert.Equal(27, fixture.RequestedBlocks[0]);
            Assert.Contains(fixture.ReceiverWrites, frame => frame.SequenceEqual(new byte[] { 0x16, 0x32, 0x37, 0x03, 0x43, 0x25 }));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void SeaLinkRepairsDataDamageWithNumberedNakOrResync(bool overdrive, bool resync)
        {
            var corrupted = false;
            var options = new LegacyModemOptions(overdrive: overdrive, enableResync: resync);
            var fixture = new LegacyWireFixture(
                new[] { Content(8193) },
                options,
                fault: (sender, _, bytes) =>
                {
                    if (sender && !corrupted && bytes.Length == 133 && bytes[1] == 2)
                    {
                        corrupted = true;
                        bytes[12] ^= 0x10;
                    }

                    return bytes;
                });
            Assert.Equal(Content(8193), Assert.Single(fixture.Run()));
            Assert.Null(fixture.Failure);
            Assert.True(corrupted);
        }

        [Fact]
        public void OverdriveSuppressesDataAcksButAcknowledgesHeaderAndEof()
        {
            var fixture = new LegacyWireFixture(new[] { Content(8193) }, new LegacyModemOptions(overdrive: true));
            fixture.Run();
            Assert.Null(fixture.Failure);
            Assert.Equal(2, fixture.ReceiverWrites.Count(bytes => bytes.Length == 3 && bytes[0] == 6));
        }

        [Fact]
        public void ReceiverMayDeclineOverdriveByContinuingToAcknowledgeData()
        {
            var fixture = new LegacyWireFixture(
                new[] { Content(8193) },
                new LegacyModemOptions(overdrive: true),
                new LegacyModemOptions(overdrive: false));
            Assert.Equal(Content(8193), Assert.Single(fixture.Run()));
            Assert.Null(fixture.Failure);
            Assert.True(fixture.ReceiverWrites.Count(bytes => bytes.Length == 3 && bytes[0] == 6) > 10);
        }

        internal static byte[] Content(int length)
        {
            return Enumerable.Range(0, length).Select(number => (byte)number).ToArray();
        }
    }
}
