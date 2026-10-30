namespace Ymodem.Protocol.Tests
{
    public sealed class WireTransferTests
    {
        public static IEnumerable<object[]> Boundaries()
        {
            foreach (var mode in new[] { YModemTransferMode.Crc, YModemTransferMode.G })
            {
                foreach (var size in new[] { 0, 1, 127, 128, 129, 1024, 1025, (257 * 1024) + 1 })
                {
                    yield return new object[] { mode, size };
                }
            }
        }

        [Theory]
        [MemberData(nameof(Boundaries))]
        public void SingleFileSurvivesByteFramingAndRollover(YModemTransferMode mode, int size)
        {
            var content = CreateContent(size);
            var fixture = new YModemLoopbackFixture(new[] { content }, false, mode, chunkSize: 1);
            var output = fixture.Run();
            Assert.Null(fixture.Failure);
            Assert.Equal(content, Assert.Single(output));
            Assert.True(fixture.SenderCompleted);
            Assert.True(fixture.ReceiverCompleted);
            if (mode == YModemTransferMode.G)
            {
                Assert.DoesNotContain(YModemControlBytes.Nak, fixture.ReceiverControls);
                Assert.DoesNotContain(YModemControlBytes.CrcRequest, fixture.ReceiverControls);
            }
        }

        [Theory]
        [InlineData(YModemTransferMode.Crc)]
        [InlineData(YModemTransferMode.G)]
        public void BatchIncludesEmptyFilesAndResetsBlockNumbers(YModemTransferMode mode)
        {
            var contents = new[] { CreateContent(1025), Array.Empty<byte>(), CreateContent(128) };
            var fixture = new YModemLoopbackFixture(contents, true, mode);
            var output = fixture.Run();
            Assert.Null(fixture.Failure);
            Assert.Equal(contents.Length, output.Count);
            for (var index = 0; index < contents.Length; index++)
            {
                Assert.Equal(contents[index], output[index]);
            }
        }

        [Theory]
        [InlineData(YModemTransferMode.Crc)]
        [InlineData(YModemTransferMode.G)]
        public void EmptyBatchCompletes(YModemTransferMode mode)
        {
            var fixture = new YModemLoopbackFixture(Array.Empty<byte[]>(), true, mode);
            Assert.Empty(fixture.Run());
            Assert.Null(fixture.Failure);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void DroppedSenderFramesRecoverThroughExplicitTimeouts(int droppedFrame)
        {
            var content = CreateContent(3);
            var fixture = new YModemLoopbackFixture(
                new[] { content },
                false,
                fault: (sender, index, bytes) => sender && index == droppedFrame ? null : bytes);
            Assert.Equal(content, Assert.Single(fixture.Run()));
            Assert.Null(fixture.Failure);
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        public void DroppedReceiverControlsDoNotDuplicateFileData(int droppedControl)
        {
            var content = CreateContent(3);
            var fixture = new YModemLoopbackFixture(
                new[] { content },
                false,
                fault: (sender, index, bytes) => !sender && index == droppedControl ? null : bytes);
            Assert.Equal(content, Assert.Single(fixture.Run()));
            Assert.Null(fixture.Failure);
        }

        [Theory]
        [InlineData(YModemTransferMode.Crc, false)]
        [InlineData(YModemTransferMode.G, true)]
        public void DataCrcErrorRetriesOrAbortsAccordingToMode(YModemTransferMode mode, bool shouldFail)
        {
            var content = CreateContent(1025);
            var fixture = new YModemLoopbackFixture(
                new[] { content },
                false,
                mode,
                fault: (sender, index, bytes) =>
                {
                    if (sender && index == 2)
                    {
                        bytes[10] ^= 0x40;
                    }

                    return bytes;
                });
            var output = fixture.Run();
            if (shouldFail)
            {
                Assert.NotNull(fixture.Failure);
                Assert.False(fixture.ReceiverCompleted);
                Assert.Contains(YModemControlBytes.Can, fixture.ReceiverControls);
            }
            else
            {
                Assert.Null(fixture.Failure);
                Assert.Equal(content, Assert.Single(output));
            }
        }

        [Theory]
        [InlineData(YModemBlockMode.Dynamic1K)]
        [InlineData(YModemBlockMode.Fixed128)]
        [InlineData(YModemBlockMode.Fixed1K)]
        public void EveryBlockModeTransfersExactBinaryContent(YModemBlockMode blockMode)
        {
            var content = CreateContent((256 * 128) + 1);
            var fixture = new YModemLoopbackFixture(new[] { content }, false, blockMode: blockMode);
            Assert.Equal(content, Assert.Single(fixture.Run()));
            Assert.Null(fixture.Failure);
        }

        private static byte[] CreateContent(int length)
        {
            return Enumerable.Range(0, length).Select(index => (byte)index).ToArray();
        }
    }
}
