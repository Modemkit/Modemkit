namespace Ymodem.Protocol.Tests
{
    public sealed class ChecksumBranchTests
    {
        [Theory]
        [InlineData(128, 0xc0)]
        [InlineData(1024, 0x00)]
        public void UsesIndependentAdditiveChecksumVector(int blockSize, byte expected)
        {
            var payload = Enumerable.Range(0, blockSize).Select(i => (byte)i).ToArray();
            var packet = new YModemPacket.Data(1, payload, payload.Length, blockSize);
            var frame = new YModemPacketEncoder().Encode(packet, YModemIntegrity.Checksum);
            Assert.Equal(blockSize + 4, frame.Length);
            Assert.Equal(expected, frame[^1]);
            Assert.Equal(1, frame[1]);
            Assert.Equal(0xfe, frame[2]);
            var decoded = Assert.IsType<YModemPacket.Data>(
                new YModemPacketDecoder().Decode(frame, YModemIntegrity.Checksum, true));
            Assert.Equal(payload, decoded.Payload);
            frame[17] ^= 0x80;
            Assert.Throws<InvalidOperationException>(() => new YModemPacketDecoder().Decode(frame, YModemIntegrity.Checksum, true));
        }

        [Fact]
        public void HeaderAndTrailerAlsoCarryOneChecksumByte()
        {
            var encoder = new YModemPacketEncoder();
            var header = encoder.Encode(new YModemPacket.Header(new YModemFileDescriptor("A", 0)), YModemIntegrity.Checksum);
            Assert.Equal(132, header.Length);
            Assert.Equal(0x71, header[^1]);
            var trailer = encoder.Encode(new YModemPacket.BatchTrailer(), YModemIntegrity.Checksum);
            Assert.Equal(132, trailer.Length);
            Assert.Equal(0, trailer[^1]);
            Assert.IsType<YModemPacket.BatchTrailer>(new YModemPacketDecoder().Decode(trailer, YModemIntegrity.Checksum));
        }

        [Theory]
        [InlineData(false, YModemBlockMode.Fixed128)]
        [InlineData(false, YModemBlockMode.Dynamic1K)]
        [InlineData(false, YModemBlockMode.Fixed1K)]
        [InlineData(true, YModemBlockMode.Fixed128)]
        [InlineData(true, YModemBlockMode.Dynamic1K)]
        [InlineData(true, YModemBlockMode.Fixed1K)]
        public void TransfersChecksumBranchWithRollover(bool batch, YModemBlockMode blockMode)
        {
            var data = Enumerable.Range(0, 33025).Select(i => (byte)i).ToArray();
            var files = batch ? new[] { Array.Empty<byte>(), data, new byte[] { 1 } } : new[] { data };
            var fixture = new YModemLoopbackFixture(files, batch, YModemTransferMode.Checksum, 1, blockMode);
            var received = fixture.Run();
            Assert.Null(fixture.Failure);
            Assert.Equal(files.Length, received.Count);
            for (var index = 0; index < files.Length; index++)
            {
                Assert.Equal(files[index], received[index]);
            }

            Assert.DoesNotContain(YModemControlBytes.CrcRequest, fixture.ReceiverControls);
            Assert.All(
                fixture.SenderFrames.Where(frame => frame.Length > 1),
                frame => Assert.True(frame.Length == 132 || frame.Length == 1028));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        public void RecoversEachLostHandshakeAndAcknowledgement(int droppedControl)
        {
            var fixture = new YModemLoopbackFixture(
                new[] { new byte[1025] },
                true,
                YModemTransferMode.Checksum,
                fault: (sender, sequence, bytes) => !sender && sequence == droppedControl ? null : bytes);
            fixture.Run();
            Assert.Null(fixture.Failure);
            Assert.True(fixture.SenderCompleted);
            Assert.True(fixture.ReceiverCompleted);
        }

        [Fact]
        public void CorruptionRequestsRetransmissionInSelectedIntegrity()
        {
            var data = Enumerable.Range(0, 2000).Select(i => (byte)i).ToArray();
            var fixture = new YModemLoopbackFixture(
                new[] { data },
                true,
                YModemTransferMode.Checksum,
                fault: (sender, sequence, bytes) =>
                {
                    if (sender && sequence == 2)
                    {
                        bytes[27] ^= 1;
                    }

                    return bytes;
                });
            Assert.Equal(data, Assert.Single(fixture.Run()));
            Assert.Null(fixture.Failure);
        }

        [Theory]
        [InlineData(YModemControlBytes.CrcRequest, YModemIntegrity.Crc16)]
        [InlineData(YModemControlBytes.Nak, YModemIntegrity.Checksum)]
        public void SenderNegotiatesFromPeerRequest(byte request, YModemIntegrity integrity)
        {
            var sender = new YModemSender();
            Assert.IsType<YModemAction.RequestFileHeader>(Assert.Single(sender.Advance(new YModemEvent.PeerByteReceived(request)).Actions));
            var action = Assert.IsType<YModemAction.SendPacket>(Assert.Single(
                sender.Advance(new YModemEvent.FileHeaderReady(new YModemFileDescriptor("a", 1))).Actions));
            Assert.Equal(integrity, sender.Integrity);
            Assert.Equal(integrity, action.Integrity);
        }

        [Fact]
        public void DecoderCannotChangeIntegrityInAnIncompleteFrame()
        {
            var decoder = new YModemStreamDecoder(YModemIntegrity.Checksum);
            decoder.Feed(new byte[] { YModemControlBytes.Soh });
            Assert.Throws<InvalidOperationException>(() => decoder.Integrity = YModemIntegrity.Crc16);
            decoder.Timeout();
            decoder.Integrity = YModemIntegrity.Crc16;
            Assert.Equal(YModemIntegrity.Crc16, decoder.Integrity);
        }
    }
}
