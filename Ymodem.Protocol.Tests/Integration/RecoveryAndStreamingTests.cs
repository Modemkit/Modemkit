using System.Text;

namespace Ymodem.Protocol.Tests
{
    public sealed class RecoveryAndStreamingTests
    {
        [Fact]
        public void StreamingTrailerCompletesSenderWithoutWaitingForAck()
        {
            var sender = new YModemBatchSender(new YModemBlockOptions(), new YModemTransferOptions(YModemTransferMode.G));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.StreamingRequest));
            var final = sender.Advance(new YModemEvent.NoMoreFiles());
            Assert.IsType<YModemPacket.BatchTrailer>(Assert.IsType<YModemAction.SendPacket>(final.Actions[0]).Packet);
            Assert.IsType<YModemAction.Complete>(final.Actions[1]);
            Assert.Equal(YModemBatchSenderPhase.Completed, final.Snapshot.Phase);
        }

        [Fact]
        public void StreamingUsesGAfterHeaderAndOnlyAcknowledgesEot()
        {
            var fixture = new YModemLoopbackFixture(new[] { new byte[2048] }, false, YModemTransferMode.G);
            fixture.Run();
            Assert.Null(fixture.Failure);
            Assert.Equal(
                new[] { YModemControlBytes.StreamingRequest, YModemControlBytes.StreamingRequest, YModemControlBytes.Ack, YModemControlBytes.StreamingRequest },
                fixture.ReceiverControls);
        }

        [Fact]
        public void ReceiverRejectsPrematureEotWithoutCompletingFile()
        {
            var receiver = StartReceiver(129);
            var result = receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Eot()));
            Assert.Equal(YModemReceiverPhase.WaitingDataPacketOrEot, result.Snapshot.Phase);
            Assert.Equal(129, result.Snapshot.RemainingFileBytes);
            Assert.Equal(YModemControlBytes.Nak, Assert.IsType<YModemAction.SendControl>(Assert.Single(result.Actions)).Value);
        }

        [Theory]
        [InlineData(2, true)]
        [InlineData(4, true)]
        [InlineData(3, false)]
        [InlineData(0, false)]
        public void SenderRejectsInconsistentAdvertisedLength(int length, bool final)
        {
            var sender = new YModemSender();
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            sender.Advance(new YModemEvent.FileHeaderReady(new YModemFileDescriptor("length.bin", 3)));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            var result = sender.Advance(new YModemEvent.DataBlockReady(1, new byte[length], length, final));
            Assert.Equal(YModemSenderPhase.Faulted, result.Snapshot.Phase);
            Assert.IsType<YModemAction.Fail>(Assert.Single(result.Actions));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ExplicitTimeoutBudgetTerminatesUnresponsiveSessions(bool senderSide)
        {
            var options = new YModemTransferOptions(maxRetries: 2);
            if (senderSide)
            {
                var sender = new YModemSender(new YModemBlockOptions(), options);
                sender.Advance(new YModemEvent.TimeoutElapsed());
                sender.Advance(new YModemEvent.TimeoutElapsed());
                var final = sender.Advance(new YModemEvent.TimeoutElapsed());
                Assert.Equal(YModemSenderPhase.Faulted, final.Snapshot.Phase);
                Assert.Contains(final.Actions, action => action is YModemAction.SendControl control && control.Value == YModemControlBytes.Can);
                Assert.Empty(sender.Advance(new YModemEvent.TimeoutElapsed()).Actions);
            }
            else
            {
                var receiver = new YModemReceiver(options);
                receiver.Advance(new YModemEvent.StartRequested());
                receiver.Advance(new YModemEvent.TimeoutElapsed());
                receiver.Advance(new YModemEvent.TimeoutElapsed());
                var final = receiver.Advance(new YModemEvent.TimeoutElapsed());
                Assert.Equal(YModemReceiverPhase.Faulted, final.Snapshot.Phase);
                Assert.Contains(final.Actions, action => action is YModemAction.SendControl control && control.Value == YModemControlBytes.Can);
                Assert.Empty(receiver.Advance(new YModemEvent.TimeoutElapsed()).Actions);
            }
        }

        [Fact]
        public void NakRetriesAlsoRespectBudget()
        {
            var sender = new YModemSender(new YModemBlockOptions(), new YModemTransferOptions(maxRetries: 1));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            sender.Advance(new YModemEvent.FileHeaderReady(new YModemFileDescriptor("test.bin", 1)));
            Assert.IsType<YModemAction.SendPacket>(Assert.Single(sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Nak)).Actions));
            var final = sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Nak));
            Assert.Equal(YModemSenderPhase.Faulted, final.Snapshot.Phase);
        }

        [Fact]
        public void DataDecisionUsesDataLengthInsteadOfBackingBufferCapacity()
        {
            var receiver = StartReceiver(3);
            var result = receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Data(1, new byte[1024], 1)));
            Assert.Equal(1, Assert.IsType<YModemAction.DeliverDataBlock>(Assert.Single(result.Actions)).DataLength);
            Assert.Equal(2, receiver.Advance(new YModemEvent.DataBlockAccepted()).Snapshot.RemainingFileBytes);
        }

        [Fact]
        public void UnknownLengthHeadersRoundTripAndReceiverPreservesPadding()
        {
            var frame = new YModemPacketEncoder().Encode(new YModemPacket.Header(new YModemFileDescriptor("unknown.bin")));
            var header = Assert.IsType<YModemPacket.Header>(new YModemPacketDecoder().Decode(frame));
            Assert.False(header.File.HasFileSize);
            var receiver = new YModemReceiver();
            receiver.Advance(new YModemEvent.StartRequested());
            receiver.Advance(new YModemEvent.PacketReceived(header));
            receiver.Advance(new YModemEvent.FileHeaderAccepted());
            var result = receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Data(1, new byte[128], 128)));
            Assert.Equal(128, Assert.IsType<YModemAction.DeliverDataBlock>(Assert.Single(result.Actions)).DataLength);
            Assert.Equal(-1, receiver.Advance(new YModemEvent.DataBlockAccepted()).Snapshot.RemainingFileBytes);
            Assert.Equal(YModemReceiverPhase.WaitingSecondEot, receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Eot())).Snapshot.Phase);
        }

        [Fact]
        public void StreamDecoderAcceptsCoalescedFramesAndControlBytesInsidePayload()
        {
            var encoder = new YModemPacketEncoder();
            var frame1 = encoder.Encode(new YModemPacket.Data(255, Enumerable.Range(0, 128).Select(index => (byte)index).ToArray(), 128, 128));
            var frame2 = encoder.Encode(new YModemPacket.Data(256, new byte[128], 128, 128));
            var decoder = new YModemStreamDecoder();
            var events = decoder.Feed(frame1.Concat(frame2).Concat(new[] { YModemControlBytes.Eot }).ToArray(), true);
            Assert.Equal(3, events.Count);
            Assert.Equal(255, Assert.IsType<YModemPacket.Data>(Assert.IsType<YModemEvent.PacketReceived>(events[0]).Packet).BlockNumber);
            Assert.Equal(0, Assert.IsType<YModemPacket.Data>(Assert.IsType<YModemEvent.PacketReceived>(events[1]).Packet).BlockNumber);
            Assert.IsType<YModemPacket.Eot>(Assert.IsType<YModemEvent.PacketReceived>(events[2]).Packet);
            Assert.Equal(0, decoder.BufferedByteCount);
        }

        [Fact]
        public void StreamDecoderRecoversAfterTruncatedFrameAndTimeout()
        {
            var frame = new YModemPacketEncoder().Encode(new YModemPacket.Data(1, new byte[128], 128, 128));
            var decoder = new YModemStreamDecoder();
            Assert.Empty(decoder.Feed(frame.Take(17).ToArray(), true));
            Assert.Equal(17, decoder.BufferedByteCount);
            Assert.IsType<YModemEvent.TimeoutElapsed>(decoder.Timeout());
            Assert.Equal(0, decoder.BufferedByteCount);
            Assert.IsType<YModemEvent.PacketReceived>(Assert.Single(decoder.Feed(frame, true)));
        }

        [Fact]
        public void StreamDecoderReportsOneCrcErrorAndContinuesAtNextFrame()
        {
            var frame = new YModemPacketEncoder().Encode(new YModemPacket.Data(1, new byte[128], 128, 128));
            var broken = (byte[])frame.Clone();
            broken[10] ^= 0x80;
            var events = new YModemStreamDecoder().Feed(broken.Concat(frame).ToArray(), true);
            Assert.IsType<YModemEvent.InvalidFrameReceived>(events[0]);
            Assert.IsType<YModemEvent.PacketReceived>(events[1]);
        }

        [Theory]
        [InlineData("-1")]
        [InlineData("+1")]
        [InlineData("9223372036854775808")]
        public void HeaderDecoderRejectsMalformedSizeAsWireError(string size)
        {
            var metadata = Encoding.ASCII.GetBytes("test.bin\0" + size + "\0");
            var frame = new YModemPacketEncoder().Encode(new YModemPacket.Data(0, metadata, metadata.Length, 128));
            Assert.IsType<YModemEvent.InvalidFrameReceived>(Assert.Single(new YModemStreamDecoder().Feed(frame)));
        }

        [Fact]
        public void EmptyFilenameEndsBatchEvenWhenPaddingIsNotZero()
        {
            var frame = new YModemPacketEncoder().Encode(new YModemPacket.Data(0, new byte[] { 0 }, 1, 128));
            Assert.IsType<YModemPacket.BatchTrailer>(new YModemPacketDecoder().Decode(frame));
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void StreamingNegotiationAndEotControlsCanBeRetried(int droppedControl)
        {
            var contents = new byte[] { 1, 2, 3 };
            var fixture = new YModemLoopbackFixture(
                new[] { contents },
                false,
                YModemTransferMode.G,
                fault: (sender, index, bytes) => !sender && index == droppedControl ? null : bytes);
            Assert.Equal(contents, Assert.Single(fixture.Run()));
            Assert.Null(fixture.Failure);
        }

        [Fact]
        public void CorruptStreamingFrameEmitsConsecutiveCancelBytes()
        {
            var receiver = new YModemReceiver(new YModemTransferOptions(YModemTransferMode.G));
            receiver.Advance(new YModemEvent.StartRequested());
            var result = receiver.Advance(new YModemEvent.InvalidFrameReceived());
            Assert.Equal(
                new[] { YModemControlBytes.Can, YModemControlBytes.Can },
                result.Actions.OfType<YModemAction.SendControl>().Select(action => action.Value));
            Assert.Equal(YModemReceiverPhase.Faulted, result.Snapshot.Phase);
        }

        [Fact]
        public void WhitespaceFilenameIsAnInvalidFrameInsteadOfAnEscapedException()
        {
            var metadata = Encoding.ASCII.GetBytes("   \0" + "1\0");
            var frame = new YModemPacketEncoder().Encode(new YModemPacket.Data(0, metadata, metadata.Length, 128));
            Assert.IsType<YModemEvent.InvalidFrameReceived>(Assert.Single(new YModemStreamDecoder().Feed(frame)));
        }

        [Fact]
        public void RetransmissionsAreUnaffectedByCallerBufferMutations()
        {
            var sender = new YModemSender();
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            sender.Advance(new YModemEvent.FileHeaderReady(new YModemFileDescriptor("test.bin", 3)));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            var original = new byte[] { 1, 2, 3 };
            var first = Assert.IsType<YModemAction.SendPacket>(Assert.Single(
                sender.Advance(new YModemEvent.DataBlockReady(1, original, 3, true)).Actions));
            var encoder = new YModemPacketEncoder();
            var expected = encoder.Encode(first.Packet);
            original[0] = 99;
            Assert.IsType<YModemPacket.Data>(first.Packet).Payload[1] = 98;
            var resend = Assert.IsType<YModemAction.SendPacket>(Assert.Single(
                sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Nak)).Actions));
            Assert.Equal(expected, encoder.Encode(resend.Packet));
        }

        [Fact]
        public void SequentialDispatchTracksRolloverInsideOneHugeTransportChunk()
        {
            const int blocks = 257;
            var receiver = new YModemReceiver(new YModemTransferOptions(YModemTransferMode.G));
            receiver.Advance(new YModemEvent.StartRequested());
            var encoder = new YModemPacketEncoder();
            var wire = new List<byte>(encoder.Encode(new YModemPacket.Header(new YModemFileDescriptor("wrap.bin", blocks * 128))));
            for (var number = 1; number <= blocks; number++)
            {
                wire.AddRange(encoder.Encode(new YModemPacket.Data(number, new byte[128], 128, 128)));
            }

            wire.Add(YModemControlBytes.Eot);
            wire.AddRange(encoder.Encode(new YModemPacket.BatchTrailer()));
            var delivered = 0;
            var decoder = new YModemStreamDecoder();
            decoder.Feed(
                wire.ToArray(),
                () => receiver.Snapshot.Phase == YModemReceiverPhase.WaitingDataPacketOrEot && receiver.Snapshot.NextBlockNumber > 1,
                input =>
                {
                    var result = receiver.Advance(input);
                    Assert.DoesNotContain(result.Actions, action => action is YModemAction.Fail);
                    if (result.Actions.Any(action => action is YModemAction.OfferFileHeader))
                    {
                        receiver.Advance(new YModemEvent.FileHeaderAccepted());
                    }

                    if (result.Actions.Any(action => action is YModemAction.DeliverDataBlock))
                    {
                        delivered++;
                        receiver.Advance(new YModemEvent.DataBlockAccepted());
                    }
                });
            Assert.Equal(blocks, delivered);
            Assert.Equal(YModemReceiverPhase.Completed, receiver.Snapshot.Phase);
        }

        [Fact]
        public void CrcMatchesIndependentPythonBinasciiGoldenVector()
        {
            // binascii.crc_hqx(bytes(range(128)), 0) == 0xe80a.
            var payload = Enumerable.Range(0, 128).Select(value => (byte)value).ToArray();
            var frame = new YModemPacketEncoder().Encode(new YModemPacket.Data(1, payload, 128, 128));
            Assert.Equal(0xe8, frame[131]);
            Assert.Equal(0x0a, frame[132]);
            var expectedFrame = new byte[133];
            expectedFrame[0] = 1;
            expectedFrame[1] = 1;
            expectedFrame[2] = 254;
            payload.CopyTo(expectedFrame, 3);
            expectedFrame[131] = 0xe8;
            expectedFrame[132] = 0x0a;
            Assert.Equal(payload, Assert.IsType<YModemPacket.Data>(new YModemPacketDecoder().Decode(expectedFrame, true)).Payload);
        }

        [Theory]
        [InlineData("a\tb")]
        [InlineData("a\0b")]
        [InlineData("a\u007fb")]
        public void FileDescriptorsRejectWireControlCharacters(string name)
        {
            Assert.Throws<ArgumentException>(() => new YModemFileDescriptor(name, 1));
        }

        private static YModemReceiver StartReceiver(long size)
        {
            var receiver = new YModemReceiver();
            receiver.Advance(new YModemEvent.StartRequested());
            receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Header(new YModemFileDescriptor("test.bin", size))));
            receiver.Advance(new YModemEvent.FileHeaderAccepted());
            return receiver;
        }
    }
}
