using System.IO;

namespace Xmodem.Protocol.Tests
{
    public sealed class WXModemTests
    {
        [Fact]
        public void WireVectorUsesBoswellSynPrefixCrcAndExactDleEscapes()
        {
            var payload = new byte[128];
            System.Text.Encoding.ASCII.GetBytes("123456789").CopyTo(payload, 119);
            var frame = new WXModemPacketEncoder().Encode(new XModemPacket.Data(1, payload, 128));
            Assert.Equal(new byte[] { 0x16, 0x01, 0x01, 0xFE }, frame.Take(4));
            Assert.Equal(new byte[] { 0x31, 0xC3 }, frame.TakeLast(2));
            Assert.Equal(payload, Assert.IsType<XModemPacket.Data>(new WXModemPacketDecoder().Decode(frame)).Payload);

            var escaped = new WXModemPacketEncoder().Encode(
                new XModemPacket.Data(0x10, new byte[] { 0x10, 0x11, 0x13, 0x16, 0x18, 0x18 }, 6));
            Assert.Equal(new byte[] { 0x16, 0x01, 0x10, 0x50, 0xEF }, escaped.Take(5));
            Assert.Equal(new byte[] { 0x10, 0x50, 0x10, 0x51, 0x10, 0x53, 0x10, 0x56, 0x18, 0x18 }, escaped.Skip(5).Take(10));
            var decoded = Assert.IsType<XModemPacket.Data>(new WXModemPacketDecoder().Decode(escaped));
            Assert.Equal(new byte[] { 0x10, 0x11, 0x13, 0x16, 0x18, 0x18 }, decoded.Payload.Take(6));
        }

        [Fact]
        public void DecoderHandlesEverySplitRepeatedSynAndCoalescedPackets()
        {
            var data = Enumerable.Range(0, 128).Select(i => (byte)i).ToArray();
            var wire = new WXModemPacketEncoder().Encode(new XModemPacket.Data(1, data, 128));
            for (var split = 1; split < wire.Length; split++)
            {
                var decoder = new WXModemStreamDecoder();
                Assert.Empty(decoder.Feed(wire.Take(split).ToArray()));
                var packet = Assert.IsType<XModemEvent.PacketReceived>(
                    Assert.Single(decoder.Feed(wire.Skip(split).ToArray())));
                Assert.Equal(data, Assert.IsType<XModemPacket.Data>(packet.Packet).Payload);
            }

            var repeatedSyn = new byte[] { WXModemControlBytes.Syn }.Concat(wire).Concat(wire).ToArray();
            Assert.Equal(2, new WXModemStreamDecoder().Feed(repeatedSyn).Count);
        }

        [Fact]
        public void TruncatedFrameResynchronizesAtUnescapedSyn()
        {
            var wire = new WXModemPacketEncoder().Encode(new XModemPacket.Data(1, new byte[128], 128));
            var events = new WXModemStreamDecoder().Feed(wire.Take(11).Concat(wire).ToArray());
            Assert.IsType<XModemEvent.InvalidFrame>(events[0]);
            Assert.IsType<XModemEvent.PacketReceived>(events[1]);
            Assert.Throws<InvalidDataException>(() => new WXModemPacketDecoder().Decode(wire.Take(11).ToArray()));
            Assert.Throws<ArgumentException>(() => new WXModemPacketEncoder().Encode(new XModemPacket.Data(1, new byte[1024], 1024, 1024)));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(128)]
        [InlineData(129)]
        [InlineData(511)]
        [InlineData(512)]
        [InlineData(513)]
        [InlineData(8193)]
        [InlineData(32769)]
        public void ByteFixtureTransfersBoundariesAndSequenceWrap(int length)
        {
            var source = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
            var fixture = new WXModemWireFixture(source, 1);
            fixture.Run();
            Assert.Equal(source, fixture.Received.Take(source.Length));
            Assert.All(fixture.Received.Skip(source.Length), value => Assert.Equal(0x1A, value));
            Assert.Equal(length, fixture.Sender.Snapshot.BytesTransferred);
            Assert.Equal(fixture.Received.Count, fixture.Receiver.Snapshot.BytesTransferred);
        }

        [Theory]
        [InlineData("drop-ack")]
        [InlineData("corrupt-ack")]
        [InlineData("drop-data")]
        [InlineData("corrupt-data")]
        [InlineData("truncate-data")]
        [InlineData("drop-eot-ack")]
        public void FaultsRecoverWithoutDuplicateDelivery(string fault)
        {
            var source = Enumerable.Range(0, 1025).Select(i => (byte)i).ToArray();
            var fixture = new WXModemWireFixture(source, 1);
            var injected = false;
            fixture.MutateWire = (toReceiver, bytes) =>
            {
                if (injected)
                {
                    return bytes;
                }

                if (!toReceiver && bytes[0] == XModemControlBytes.Ack && bytes.Length == 2
                    && (fault == "drop-ack" || fault == "corrupt-ack"))
                {
                    injected = true;
                    if (fault == "drop-ack")
                    {
                        return null;
                    }

                    bytes[1] = 0xFF;
                }
                else if (toReceiver && bytes[0] == WXModemControlBytes.Syn && fault.EndsWith("-data", StringComparison.Ordinal))
                {
                    injected = true;
                    if (fault == "drop-data")
                    {
                        return null;
                    }

                    if (fault == "truncate-data")
                    {
                        return bytes.Take(13).ToArray();
                    }

                    bytes[^1] ^= 1;
                }
                else if (!toReceiver && bytes.Length == 1 && bytes[0] == XModemControlBytes.Ack && fault == "drop-eot-ack")
                {
                    injected = true;
                    return null;
                }

                return bytes;
            };
            fixture.Run();
            Assert.True(injected);
            Assert.Equal(source, fixture.Received.Take(source.Length));
            Assert.Equal(1152, fixture.Received.Count);
        }

        [Fact]
        public void WindowFillsFourBlocksAndCumulativeAckReleasesThem()
        {
            var sender = new WXModemSender();
            sender.Advance(new XModemEvent.PeerByteReceived((byte)'W'));
            for (var block = 1; block <= 4; block++)
            {
                var result = sender.Advance(new XModemEvent.DataBlockReady(block, new byte[128], 128, false));
                Assert.Single(result.Actions.OfType<XModemAction.SendBytes>());
                Assert.Equal(block == 4 ? 0 : 1, result.Actions.OfType<XModemAction.RequestDataBlock>().Count());
            }

            Assert.Equal(4, sender.Snapshot.OutstandingBlocks);
            Assert.Equal(XModemPhase.WaitingWindowAck, sender.Snapshot.Phase);
            var ack = sender.Advance(new XModemEvent.PeerBytesReceived(new byte[] { 6, 0 }));
            Assert.Equal(0, sender.Snapshot.OutstandingBlocks);
            Assert.Equal(512, sender.Snapshot.BytesTransferred);
            Assert.Equal(5, Assert.Single(ack.Actions.OfType<XModemAction.RequestDataBlock>()).BlockNumber);
        }

        [Fact]
        public void TimeoutProbesLastOutstandingBlockAndNakResendsFromRequestedSequence()
        {
            var sender = FillWindow();
            var probe = Assert.Single(sender.Advance(new XModemEvent.TimeoutElapsed()).Actions.OfType<XModemAction.SendBytes>());
            Assert.Equal(4, Assert.IsType<XModemPacket.Data>(new WXModemPacketDecoder().Decode(probe.Bytes)).BlockNumber);
            var resent = sender.Advance(new XModemEvent.PeerBytesReceived(new byte[] { 0x15, 2 }));
            Assert.Equal(Enumerable.Range(2, 3), resent.Actions.OfType<XModemAction.SendBytes>()
                .Select(send => Assert.IsType<XModemPacket.Data>(new WXModemPacketDecoder().Decode(send.Bytes)).BlockNumber));
            Assert.Equal(128, sender.Snapshot.BytesTransferred);
            Assert.Equal(5, Assert.Single(resent.Actions.OfType<XModemAction.RequestDataBlock>()).BlockNumber);
        }

        [Fact]
        public void FlowControlPausesNewDataAndExplicitTenSecondExpiryResumes()
        {
            var sender = new WXModemSender();
            sender.Advance(new XModemEvent.PeerByteReceived((byte)'W'));
            sender.Advance(new XModemEvent.PeerByteReceived(0x13));
            Assert.Empty(sender.Advance(new XModemEvent.DataBlockReady(1, new byte[128], 128, false)).Actions);
            Assert.Empty(sender.Advance(new XModemEvent.TimeoutElapsed()).Actions);
            Assert.True(sender.Snapshot.IsPaused);
            var resumed = sender.Advance(new XModemEvent.FlowControlTimeoutElapsed());
            Assert.False(sender.Snapshot.IsPaused);
            Assert.Single(resumed.Actions.OfType<XModemAction.SendBytes>());
            Assert.Single(resumed.Actions.OfType<XModemAction.RequestDataBlock>());
        }

        [Fact]
        public void PendingDiskWritesBufferTheFourPacketWindowAndDeferEot()
        {
            var receiver = new WXModemReceiver();
            receiver.Advance(new XModemEvent.StartRequested());
            var encoder = new WXModemPacketEncoder();
            var burst = Enumerable.Range(1, 4).SelectMany(i => encoder.Encode(new XModemPacket.Data(i, new byte[128], 128))).ToArray();
            var result = receiver.Advance(new XModemEvent.PeerBytesReceived(burst));
            Assert.Single(result.Actions.OfType<XModemAction.DeliverDataBlock>());
            Assert.Equal(4, receiver.Snapshot.OutstandingBlocks);
            for (var block = 1; block <= 4; block++)
            {
                var accepted = receiver.Advance(new XModemEvent.DataBlockAccepted());
                Assert.Equal(new byte[] { 6, (byte)(block & 3) }, Assert.Single(accepted.Actions.OfType<XModemAction.SendBytes>()).Bytes);
            }

            Assert.Equal(512, receiver.Snapshot.BytesTransferred);
            Assert.Single(receiver.Advance(new XModemEvent.PeerBytesReceived(new byte[] { 4 })).Actions.OfType<XModemAction.Complete>());
        }

        [Fact]
        public void ReceiverNegotiationFallsBackInBoundedStages()
        {
            var receiver = new WXModemReceiver();
            var requests = new List<byte>();
            requests.Add(Assert.Single(receiver.Advance(new XModemEvent.StartRequested()).Actions.OfType<XModemAction.SendBytes>()).Bytes[0]);
            for (var timeout = 0; timeout < 9; timeout++)
            {
                requests.Add(Assert.Single(receiver.Advance(new XModemEvent.TimeoutElapsed()).Actions.OfType<XModemAction.SendBytes>()).Bytes[0]);
            }

            Assert.Equal(new byte[] { 87, 87, 87, 67, 67, 67, 21, 21, 21, 21 }, requests);
            Assert.Single(receiver.Advance(new XModemEvent.TimeoutElapsed()).Actions.OfType<XModemAction.Fail>());
        }

        [Theory]
        [InlineData(0x43, WXModemMode.Crc)]
        [InlineData(0x15, WXModemMode.Checksum)]
        public void SenderFallbackUsesActualXmodemFraming(byte request, WXModemMode mode)
        {
            var sender = new WXModemSender();
            sender.Advance(new XModemEvent.PeerByteReceived(request));
            var result = sender.Advance(new XModemEvent.DataBlockReady(1, new byte[128], 128, true));
            var wire = Assert.Single(result.Actions.OfType<XModemAction.SendBytes>()).Bytes;
            Assert.Equal(mode, sender.Snapshot.Mode);
            Assert.Equal(1, wire[0]);
            Assert.Equal(mode == WXModemMode.Crc ? 133 : 132, wire.Length);
            Assert.IsType<XModemPacket.Data>(new XModemPacketDecoder().Decode(
                wire, mode == WXModemMode.Crc ? XModemIntegrity.Crc16 : XModemIntegrity.Checksum));
        }

        [Fact]
        public void RetryExhaustionAndLocalCancellationAreTerminal()
        {
            var sender = new WXModemSender(new WXModemOptions(maxRetries: 0));
            sender.Advance(new XModemEvent.PeerByteReceived((byte)'W'));
            sender.Advance(new XModemEvent.DataBlockReady(1, new byte[128], 128, true));
            Assert.Single(sender.Advance(new XModemEvent.TimeoutElapsed()).Actions.OfType<XModemAction.Fail>());
            Assert.Empty(sender.Advance(new XModemEvent.PeerBytesReceived(new byte[] { 6, 1 })).Actions);
            var receiver = new WXModemReceiver();
            var cancelled = receiver.Advance(new XModemEvent.CancelRequested("stop"));
            Assert.Equal(new byte[] { 24, 24 }, Assert.Single(cancelled.Actions.OfType<XModemAction.SendBytes>()).Bytes);
            Assert.Equal(XModemPhase.Cancelled, receiver.Snapshot.Phase);
        }

        [Fact]
        public void DroppingEveryAckInAWindowIsRecoveredByOneCumulativeAck()
        {
            var source = new byte[512];
            var fixture = new WXModemWireFixture(source);
            var dropped = 0;
            fixture.MutateWire = (toReceiver, bytes) =>
            {
                if (!toReceiver && bytes.Length == 2 && bytes[0] == XModemControlBytes.Ack && dropped < 4)
                {
                    dropped++;
                    return null;
                }

                return bytes;
            };
            fixture.Run();
            Assert.Equal(4, dropped);
            Assert.Equal(source, fixture.Received);
            Assert.Equal(5, fixture.Transcript.Count(item => item.ToReceiver && item.Bytes[0] == WXModemControlBytes.Syn));
        }

        [Fact]
        public void NakAfterStorageRejectionReplaysTheEntireUncommittedWindow()
        {
            var receiver = new WXModemReceiver();
            receiver.Advance(new XModemEvent.StartRequested());
            var encoder = new WXModemPacketEncoder();
            var burst = Enumerable.Range(1, 4).SelectMany(i => encoder.Encode(new XModemPacket.Data(i, new byte[128], 128))).ToArray();
            receiver.Advance(new XModemEvent.PeerBytesReceived(burst));
            var rejected = receiver.Advance(new XModemEvent.DataBlockRejected());
            Assert.Equal(new byte[] { 0x15, 1 }, Assert.Single(rejected.Actions.OfType<XModemAction.SendBytes>()).Bytes);
            Assert.Equal(0, receiver.Snapshot.BytesTransferred);
            Assert.Equal(0, receiver.Snapshot.OutstandingBlocks);
            Assert.Single(receiver.Advance(new XModemEvent.PeerBytesReceived(burst)).Actions.OfType<XModemAction.DeliverDataBlock>());
        }

        [Fact]
        public void IdleFlowTimerDoesNotStartAnUnnegotiatedSender()
        {
            var sender = new WXModemSender();
            Assert.Empty(sender.Advance(new XModemEvent.FlowControlTimeoutElapsed()).Actions);
            Assert.Equal(XModemPhase.WaitingReceiverRequest, sender.Snapshot.Phase);
        }

        [Theory]
        [InlineData(true, XModemMode.Crc)]
        [InlineData(true, XModemMode.Checksum)]
        [InlineData(false, XModemMode.Crc)]
        [InlineData(false, XModemMode.Checksum)]
        public void FallbackTransfersCompleteFilesWithLegacyXmodem(bool legacySender, XModemMode mode)
        {
            var source = Enumerable.Range(0, 257).Select(i => (byte)i).ToArray();
            var wxSender = new WXModemSender();
            var wxReceiver = new WXModemReceiver();
            var xSender = new XModemSender(new XModemOptions(mode));
            var xReceiver = new XModemReceiver(new XModemOptions(mode));
            var decoder = new XModemStreamDecoder(mode == XModemMode.Checksum ? XModemIntegrity.Checksum : XModemIntegrity.Crc16);
            var encoder = new XModemPacketEncoder();
            var wire = new Queue<(bool ToReceiver, byte[] Bytes)>();
            var received = new List<byte>();

            IReadOnlyList<XModemAction> SenderAdvance(XModemEvent protocolEvent) =>
                legacySender ? xSender.Advance(protocolEvent).Actions : wxSender.Advance(protocolEvent).Actions;
            IReadOnlyList<XModemAction> ReceiverAdvance(XModemEvent protocolEvent) =>
                legacySender ? wxReceiver.Advance(protocolEvent).Actions : xReceiver.Advance(protocolEvent).Actions;
            void Apply(bool sender, IReadOnlyList<XModemAction> actions)
            {
                Assert.Empty(actions.OfType<XModemAction.Fail>());
                foreach (var action in actions)
                {
                    switch (action)
                    {
                        case XModemAction.SendBytes send:
                            wire.Enqueue((sender, send.Bytes));
                            break;
                        case XModemAction.SendControl control:
                            wire.Enqueue((sender, new[] { control.Value }));
                            break;
                        case XModemAction.SendPacket packet:
                            wire.Enqueue((sender, encoder.Encode(packet.Packet, packet.Integrity)));
                            break;
                        case XModemAction.RequestDataBlock request:
                            var offset = (request.BlockNumber - 1) * 128;
                            var count = Math.Min(128, source.Length - offset);
                            Apply(true, SenderAdvance(new XModemEvent.DataBlockReady(
                                request.BlockNumber, source.AsSpan(offset, count).ToArray(), count, offset + count == source.Length)));
                            break;
                        case XModemAction.DeliverDataBlock data:
                            received.AddRange(data.Payload);
                            Apply(false, ReceiverAdvance(new XModemEvent.DataBlockAccepted()));
                            break;
                    }
                }
            }

            Apply(false, ReceiverAdvance(new XModemEvent.StartRequested()));
            for (var step = 0; step < 1000; step++)
            {
                var senderPhase = legacySender ? xSender.Snapshot.Phase : wxSender.Snapshot.Phase;
                var receiverPhase = legacySender ? wxReceiver.Snapshot.Phase : xReceiver.Snapshot.Phase;
                if (senderPhase == XModemPhase.Completed && receiverPhase == XModemPhase.Completed && wire.Count == 0)
                {
                    Assert.Equal(source, received.Take(source.Length));
                    Assert.Equal(384, received.Count);
                    return;
                }

                if (!wire.TryDequeue(out var item))
                {
                    Apply(false, ReceiverAdvance(new XModemEvent.TimeoutElapsed()));
                    continue;
                }

                if (!item.ToReceiver)
                {
                    foreach (var value in item.Bytes)
                    {
                        Apply(true, SenderAdvance(new XModemEvent.PeerByteReceived(value)));
                    }
                }
                else if (legacySender)
                {
                    Apply(false, ReceiverAdvance(new XModemEvent.PeerBytesReceived(item.Bytes)));
                }
                else
                {
                    foreach (var protocolEvent in decoder.Feed(item.Bytes))
                    {
                        Apply(false, ReceiverAdvance(protocolEvent));
                    }
                }
            }

            Assert.Fail("Fallback fixture exceeded its step limit.");
        }

        private static WXModemSender FillWindow()
        {
            var sender = new WXModemSender();
            sender.Advance(new XModemEvent.PeerByteReceived((byte)'W'));
            for (var i = 1; i <= 4; i++)
            {
                sender.Advance(new XModemEvent.DataBlockReady(i, new byte[128], 128, false));
            }

            return sender;
        }
    }
}
