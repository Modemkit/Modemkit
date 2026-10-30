namespace LegacyModem.Protocol.Tests
{
    public sealed class LegacyRecoveryTests
    {
        [Theory]
        [InlineData(LegacyModemVariant.Modem7)]
        [InlineData(LegacyModemVariant.Telink)]
        [InlineData(LegacyModemVariant.SeaLink)]
        public void TimeoutBudgetsAreBoundedAndCancelOnWire(LegacyModemVariant variant)
        {
            var options = new LegacyModemOptions(variant, maxRetries: 2, allowFallback: false);
            var sender = new LegacyModemSender(options);
            var receiver = new LegacyModemReceiver(options);
            receiver.Advance(new LegacyModemEvent.StartRequested());
            for (var index = 0; index < 2; index++)
            {
                sender.Advance(new LegacyModemEvent.TimeoutElapsed());
                receiver.Advance(new LegacyModemEvent.TimeoutElapsed());
            }

            Assert.Equal(new byte[] { 0x18, 0x18 }, LegacyReferenceTests.Wire(sender.Advance(new LegacyModemEvent.TimeoutElapsed())));
            Assert.Equal(new byte[] { 0x18, 0x18 }, LegacyReferenceTests.Wire(receiver.Advance(new LegacyModemEvent.TimeoutElapsed())));
            Assert.Equal(LegacyModemPhase.Faulted, sender.Snapshot.Phase);
            Assert.Equal(LegacyModemPhase.Faulted, receiver.Snapshot.Phase);
            Assert.Empty(sender.Advance(new LegacyModemEvent.TimeoutElapsed()).Actions);
        }

        [Fact]
        public void CancellationRequiresTwoConsecutiveCanBytesAndDoesNotEcho()
        {
            var sender = new LegacyModemSender();
            Assert.Empty(sender.Advance(new LegacyModemEvent.BytesReceived(new byte[] { 0x18 })).Actions);
            var cancelled = sender.Advance(new LegacyModemEvent.BytesReceived(new byte[] { 0x18 }));
            Assert.Equal(LegacyModemPhase.Cancelled, cancelled.Snapshot.Phase);
            Assert.Empty(LegacyReferenceTests.Wire(cancelled));
            Assert.Contains(cancelled.Actions, action => action is LegacyModemAction.Cancel);
            var receiver = new LegacyModemReceiver();
            Assert.Equal(new byte[] { 0x18, 0x18 }, LegacyReferenceTests.Wire(receiver.Advance(new LegacyModemEvent.CancelRequested())));
        }

        [Fact]
        public void Modem7LostCharacterAckRestartsNameWithoutDuplicatingFile()
        {
            var fixture = new LegacyWireFixture(
                new[] { LegacyTransferTests.Content(129) },
                new LegacyModemOptions(LegacyModemVariant.Modem7),
                fault: (sender, index, bytes) => !sender && index == 3 ? null : bytes);
            Assert.Equal(LegacyTransferTests.Content(129), Assert.Single(fixture.Run()).Take(129));
            Assert.Null(fixture.Failure);
            Assert.Contains(fixture.SenderWrites, bytes => bytes.SequenceEqual(new byte[] { 0x75 }));
        }

        [Theory]
        [InlineData(LegacyModemVariant.Telink)]
        [InlineData(LegacyModemVariant.SeaLink)]
        public void DamagedMetadataIsRetried(LegacyModemVariant variant)
        {
            var fixture = new LegacyWireFixture(
                new[] { LegacyTransferTests.Content(129) },
                new LegacyModemOptions(variant),
                fault: (sender, index, bytes) =>
                {
                    if (sender && index == 1)
                    {
                        bytes[10] ^= 1;
                    }

                    return bytes;
                });
            Assert.Equal(LegacyTransferTests.Content(129), Assert.Single(fixture.Run()));
            Assert.Null(fixture.Failure);
        }

        [Fact]
        public void ResyncDiscardsPartialOldWindowFrameBeforeReadingItsAck()
        {
            var receiver = new LegacyModemReceiver();
            receiver.Advance(new LegacyModemEvent.StartRequested());
            receiver.Advance(new LegacyModemEvent.BytesReceived(LegacyModemCodec.EncodeHeader(
                LegacyModemVariant.SeaLink, new LegacyModemFileDescriptor("FILE.BIN", 256))));
            receiver.Advance(new LegacyModemEvent.FileHeaderAccepted());
            var first = LegacyModemCodec.EncodeData(1, new byte[128]);
            var second = LegacyModemCodec.EncodeData(2, new byte[128]);
            var corrupt = (byte[])first.Clone();
            corrupt[10] ^= 1;
            var damagedStep = receiver.Advance(new LegacyModemEvent.BytesReceived(corrupt.Concat(second.Take(17)).ToArray()));
            Assert.Equal(LegacyModemPhase.WaitingResyncAck, damagedStep.Snapshot.Phase);
            Assert.Equal(new byte[] { 0x16, 0x31, 0x03, 0x72, 0x26 }, LegacyReferenceTests.Wire(damagedStep));
            var resumed = receiver.Advance(new LegacyModemEvent.BytesReceived(new byte[] { 6 }.Concat(first).Concat(second).ToArray()));
            Assert.Equal(1, Assert.IsType<LegacyModemAction.DeliverDataBlock>(Assert.Single(resumed.Actions)).BlockNumber);
            var next = receiver.Advance(new LegacyModemEvent.DataBlockAccepted());
            Assert.Equal(2, Assert.Single(next.Actions.OfType<LegacyModemAction.DeliverDataBlock>()).BlockNumber);
            receiver.Advance(new LegacyModemEvent.DataBlockAccepted());
            Assert.Equal(256, receiver.Snapshot.BytesTransferred);
        }

        [Fact]
        public void TelinkRejectsPrematureEot()
        {
            var receiver = new LegacyModemReceiver(new LegacyModemOptions(LegacyModemVariant.Telink));
            receiver.Advance(new LegacyModemEvent.StartRequested());
            receiver.Advance(new LegacyModemEvent.BytesReceived(LegacyModemCodec.EncodeHeader(
                LegacyModemVariant.Telink, new LegacyModemFileDescriptor("FILE.BIN", 129))));
            receiver.Advance(new LegacyModemEvent.FileHeaderAccepted());
            var step = receiver.Advance(new LegacyModemEvent.BytesReceived(new byte[] { 4 }));
            Assert.Equal(new byte[] { 0x43 }, LegacyReferenceTests.Wire(step));
            Assert.DoesNotContain(step.Actions, action => action is LegacyModemAction.FileCompleted);
        }

        [Fact]
        public void SeaLinkFallsBackToTelinkWhenItsHeaderIsRefused()
        {
            var fixture = new LegacyWireFixture(
                new[] { LegacyTransferTests.Content(129) },
                new LegacyModemOptions(),
                new LegacyModemOptions(LegacyModemVariant.Telink, allowFallback: false));
            Assert.Equal(LegacyTransferTests.Content(129), Assert.Single(fixture.Run()));
            Assert.Null(fixture.Failure);
            Assert.Contains(fixture.SenderWrites, bytes => bytes.Length == 132 && bytes[0] == 0x16);
        }

        [Fact]
        public void SeaLinkReceiverCanNegotiateAModem7OnlyPeer()
        {
            var fixture = new LegacyWireFixture(
                new[] { LegacyTransferTests.Content(129) },
                new LegacyModemOptions(LegacyModemVariant.Modem7),
                new LegacyModemOptions());
            Assert.Equal(LegacyTransferTests.Content(129), Assert.Single(fixture.Run()).Take(129));
            Assert.Null(fixture.Failure);
        }

        [Fact]
        public void OversizedTransportReadFailsWithinTheConfiguredMemoryBound()
        {
            var receiver = new LegacyModemReceiver(new LegacyModemOptions(maxBufferedBytes: 133));
            receiver.Advance(new LegacyModemEvent.StartRequested());
            var result = receiver.Advance(new LegacyModemEvent.BytesReceived(new byte[134]));
            Assert.Equal(LegacyModemPhase.Faulted, result.Snapshot.Phase);
            Assert.Equal(new byte[] { 0x18, 0x18 }, LegacyReferenceTests.Wire(result));
        }

        [Fact]
        public void LostSeaLinkEofAckIsRecoveredByTheNextHeaderPoll()
        {
            var dropped = false;
            var fixture = new LegacyWireFixture(
                new[] { LegacyTransferTests.Content(3), LegacyTransferTests.Content(129) },
                fault: (sender, _, bytes) =>
                {
                    if (!sender && !dropped && bytes.SequenceEqual(new byte[] { 6, 2, 253 }))
                    {
                        dropped = true;
                        return null;
                    }

                    return bytes;
                });
            Assert.Equal(2, fixture.Run().Count);
            Assert.True(dropped);
            Assert.Null(fixture.Failure);
        }

        [Fact]
        public void AmbiguousOverdriveNakFailsInsteadOfReplayingAWrongWrappedBlock()
        {
            var damaged = false;
            var fixture = new LegacyWireFixture(
                new[] { LegacyTransferTests.Content(32769) },
                new LegacyModemOptions(overdrive: true, enableResync: false),
                fault: (sender, _, bytes) =>
                {
                    if (sender && !damaged && bytes.Length == 133 && bytes[1] == 2)
                    {
                        damaged = true;
                        bytes[20] ^= 1;
                    }

                    return bytes;
                });
            fixture.Run();
            Assert.True(damaged);
            Assert.NotNull(fixture.Failure);
            Assert.Contains("RESYNC", fixture.Failure);
            Assert.False(fixture.ReceiverCompleted);
        }

        [Theory]
        [InlineData(true, 1)]
        [InlineData(true, 2)]
        [InlineData(true, 3)]
        [InlineData(true, 4)]
        [InlineData(false, 1)]
        [InlineData(false, 2)]
        [InlineData(false, 3)]
        [InlineData(false, 4)]
        [InlineData(false, 5)]
        public void SeaLinkRecoversEachSingleFileMessageLoss(bool sendDirection, int lostMessage)
        {
            var contents = LegacyTransferTests.Content(3);
            var fixture = new LegacyWireFixture(
                new[] { contents },
                fault: (sender, index, bytes) => sender == sendDirection && index == lostMessage ? null : bytes);
            Assert.Equal(contents, Assert.Single(fixture.Run()));
            Assert.Null(fixture.Failure);
        }
    }
}
