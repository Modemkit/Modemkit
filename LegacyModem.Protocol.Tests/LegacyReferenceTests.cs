using System.IO;
using System.Text;

namespace LegacyModem.Protocol.Tests
{
    public sealed class LegacyReferenceTests
    {
        [Fact]
        public void ResyncUsesThePublishedFts0007Block27Vector()
        {
            var published = new byte[] { 0x16, 0x32, 0x37, 0x03, 0x43, 0x25 };
            Assert.Equal(published, LegacyModemCodec.EncodeResync(27));
            Assert.Equal(27, LegacyModemCodec.DecodeResync(published));
            published[4] ^= 1;
            Assert.Throws<InvalidDataException>(() => LegacyModemCodec.DecodeResync(published));
        }

        [Theory]
        [InlineData(LegacyModemVariant.Telink)]
        [InlineData(LegacyModemVariant.SeaLink)]
        public void MetadataMatchesFtsOffsetsAndIndependentChecksumVectors(LegacyModemVariant variant)
        {
            var expected = ReferenceHeader(variant);
            var file = new LegacyModemFileDescriptor("TEST.BIN", 3, 0x11223344);
            Assert.Equal(expected, LegacyModemCodec.EncodeHeader(variant, file, overdrive: true, supportsResync: true, programName: "Unit"));
            var decoded = LegacyModemCodec.DecodeHeader(expected);
            Assert.Equal(3, decoded.File.FileSize);
            Assert.Equal(0x11223344U, decoded.File.Timestamp);
            Assert.Equal("TEST.BIN", decoded.File.FileName);
            Assert.Equal("Unit", decoded.ProgramName);
            Assert.Equal(variant == LegacyModemVariant.SeaLink, decoded.Overdrive);
        }

        [Fact]
        public void Modem7NameIsElevenUppercaseCharactersAndChecksumIncludesSub()
        {
            var name = Encoding.ASCII.GetBytes("TEST    BIN");
            Assert.Equal(name, LegacyModemCodec.EncodeModem7Name("test.bin"));
            Assert.Equal("TEST.BIN", LegacyModemCodec.DecodeModem7Name(name));
            Assert.Equal(0xb3, LegacyModemCodec.ComputeModem7NameChecksum(name));
            Assert.Throws<ArgumentException>(() => LegacyModemCodec.EncodeModem7Name("TOO_LONG_NAME.BIN"));
        }

        [Fact]
        public void DataCrcMatchesAnIndependentBinasciiVector()
        {
            Assert.Equal(ReferenceData(), LegacyModemCodec.EncodeData(1, Encoding.ASCII.GetBytes("ABC")));
            var data = LegacyModemCodec.DecodeData(ReferenceData());
            Assert.Equal(1, data.BlockNumber);
            Assert.Equal(Encoding.ASCII.GetBytes("ABC"), data.Data.Take(3));
            Assert.All(data.Data.Skip(3), value => Assert.Equal(0x1a, value));
        }

        [Theory]
        [InlineData(LegacyModemVariant.Telink)]
        [InlineData(LegacyModemVariant.SeaLink)]
        public void ReceiverFollowsIndependentLiteralPeerTransaction(LegacyModemVariant variant)
        {
            var receiver = new LegacyModemReceiver(new LegacyModemOptions(variant));
            Assert.Equal(new byte[] { 0x43 }, Wire(receiver.Advance(new LegacyModemEvent.StartRequested())));
            var offered = receiver.Advance(new LegacyModemEvent.BytesReceived(ReferenceHeader(variant)));
            Assert.IsType<LegacyModemAction.OfferFileHeader>(Assert.Single(offered.Actions));
            Assert.Equal(
                variant == LegacyModemVariant.SeaLink ? new byte[] { 6, 0, 255 } : new byte[] { 6 },
                Wire(receiver.Advance(new LegacyModemEvent.FileHeaderAccepted())));
            var delivered = receiver.Advance(new LegacyModemEvent.BytesReceived(ReferenceData()));
            Assert.Equal(Encoding.ASCII.GetBytes("ABC"), Assert.IsType<LegacyModemAction.DeliverDataBlock>(Assert.Single(delivered.Actions)).Data);
            Assert.Equal(
                variant == LegacyModemVariant.SeaLink ? new byte[] { 6, 1, 254 } : new byte[] { 6 },
                Wire(receiver.Advance(new LegacyModemEvent.DataBlockAccepted())));
            var eof = receiver.Advance(new LegacyModemEvent.BytesReceived(new byte[] { 4 }));
            Assert.Contains(eof.Actions, action => action is LegacyModemAction.FileCompleted);
            Assert.Equal(variant == LegacyModemVariant.SeaLink ? new byte[] { 6, 2, 253, 0x43 } : new byte[] { 6, 0x43 }, Wire(eof));
            Assert.IsType<LegacyModemAction.Complete>(Assert.Single(receiver.Advance(new LegacyModemEvent.BytesReceived(new byte[] { 4 })).Actions));
        }

        [Theory]
        [InlineData(LegacyModemVariant.Telink)]
        [InlineData(LegacyModemVariant.SeaLink)]
        public void SenderFollowsIndependentLiteralPeerTransaction(LegacyModemVariant variant)
        {
            var sender = new LegacyModemSender(new LegacyModemOptions(variant));
            Assert.IsType<LegacyModemAction.RequestFileHeader>(Assert.Single(sender.Advance(new LegacyModemEvent.BytesReceived(new byte[] { 0x43 })).Actions));
            var header = Wire(sender.Advance(new LegacyModemEvent.FileHeaderReady(new LegacyModemFileDescriptor("TEST.BIN", 3, 0x11223344))));
            Assert.Equal(variant == LegacyModemVariant.SeaLink ? 1 : 0x16, header[0]);
            Assert.Equal(new byte[] { 0, 255, 3, 0, 0, 0, 0x44, 0x33, 0x22, 0x11 }, header.Skip(1).Take(10));
            var dataRequest = sender.Advance(new LegacyModemEvent.BytesReceived(
                variant == LegacyModemVariant.SeaLink ? new byte[] { 6, 0, 255 } : new byte[] { 6 }));
            Assert.Equal(3, Assert.IsType<LegacyModemAction.RequestDataBlock>(Assert.Single(dataRequest.Actions)).DataLength);
            Assert.Equal(ReferenceData(), Wire(sender.Advance(new LegacyModemEvent.DataBlockReady(1, Encoding.ASCII.GetBytes("ABC")))));
            Assert.Equal(new byte[] { 4 }, Wire(sender.Advance(new LegacyModemEvent.BytesReceived(
                variant == LegacyModemVariant.SeaLink ? new byte[] { 6, 1, 254 } : new byte[] { 6 }))));
            sender.Advance(new LegacyModemEvent.BytesReceived(variant == LegacyModemVariant.SeaLink ? new byte[] { 6, 2, 253 } : new byte[] { 6 }));
            sender.Advance(new LegacyModemEvent.BytesReceived(new byte[] { 0x43 }));
            var complete = sender.Advance(new LegacyModemEvent.NoMoreFiles());
            Assert.Equal(new byte[] { 4 }, Wire(complete));
            Assert.Contains(complete.Actions, action => action is LegacyModemAction.Complete);
        }

        [Fact]
        public void Modem7SenderUsesCharacterAcknowledgementsAndLiteralChecksum()
        {
            var sender = new LegacyModemSender(new LegacyModemOptions(LegacyModemVariant.Modem7));
            sender.Advance(new LegacyModemEvent.BytesReceived(new byte[] { 0x15 }));
            var name = Encoding.ASCII.GetBytes("TEST    BIN");
            Assert.Equal(new byte[] { 6, name[0] }, Wire(sender.Advance(
                new LegacyModemEvent.FileHeaderReady(new LegacyModemFileDescriptor("TEST.BIN", 3)))));
            for (var index = 1; index < name.Length; index++)
            {
                Assert.Equal(new[] { name[index] }, Wire(sender.Advance(new LegacyModemEvent.BytesReceived(new byte[] { 6 }))));
            }

            Assert.Equal(new byte[] { 0x1a }, Wire(sender.Advance(new LegacyModemEvent.BytesReceived(new byte[] { 6 }))));
            Assert.Equal(new byte[] { 6 }, Wire(sender.Advance(new LegacyModemEvent.BytesReceived(new byte[] { 0xb3 }))));
            Assert.IsType<LegacyModemAction.RequestDataBlock>(Assert.Single(sender.Advance(new LegacyModemEvent.BytesReceived(new byte[] { 0x43 })).Actions));
            Assert.Equal(ReferenceData(), Wire(sender.Advance(new LegacyModemEvent.DataBlockReady(1, Encoding.ASCII.GetBytes("ABC")))));
            Assert.Equal(new byte[] { 4 }, Wire(sender.Advance(new LegacyModemEvent.BytesReceived(new byte[] { 6 }))));
            Assert.Equal(new byte[] { 4 }, Wire(sender.Advance(new LegacyModemEvent.BytesReceived(new byte[] { 0x15 }))));
            sender.Advance(new LegacyModemEvent.BytesReceived(new byte[] { 6, 0x15 }));
            Assert.Equal(new byte[] { 4 }, Wire(sender.Advance(new LegacyModemEvent.NoMoreFiles())));
            Assert.Equal(LegacyModemPhase.Completed, sender.Snapshot.Phase);
        }

        [Fact]
        public void Modem7ReceiverPreservesUnknownLengthPaddingAndDoubleEot()
        {
            var receiver = new LegacyModemReceiver(new LegacyModemOptions(LegacyModemVariant.Modem7));
            Assert.Equal(new byte[] { 0x15 }, Wire(receiver.Advance(new LegacyModemEvent.StartRequested())));
            var name = new byte[] { 6 }.Concat(Encoding.ASCII.GetBytes("TEST    BIN")).Concat(new byte[] { 0x1a }).ToArray();
            Assert.Equal(
                Enumerable.Repeat((byte)6, 11).Concat(new byte[] { 0xb3 }),
                Wire(receiver.Advance(new LegacyModemEvent.BytesReceived(name))));
            Assert.Equal(new byte[] { 0x43 }, Wire(receiver.Advance(new LegacyModemEvent.BytesReceived(new byte[] { 6 }))));
            var offer = receiver.Advance(new LegacyModemEvent.BytesReceived(ReferenceData()));
            Assert.Null(Assert.IsType<LegacyModemAction.OfferFileHeader>(Assert.Single(offer.Actions)).File.FileSize);
            var deliver = receiver.Advance(new LegacyModemEvent.FileHeaderAccepted());
            Assert.Equal(128, Assert.IsType<LegacyModemAction.DeliverDataBlock>(Assert.Single(deliver.Actions)).Data.Length);
            Assert.Equal(new byte[] { 6 }, Wire(receiver.Advance(new LegacyModemEvent.DataBlockAccepted())));
            Assert.Equal(new byte[] { 0x15 }, Wire(receiver.Advance(new LegacyModemEvent.BytesReceived(new byte[] { 4 }))));
            Assert.Equal(new byte[] { 6, 0x15 }, Wire(receiver.Advance(new LegacyModemEvent.BytesReceived(new byte[] { 4 }))));
            receiver.Advance(new LegacyModemEvent.BytesReceived(new byte[] { 4 }));
            Assert.Equal(LegacyModemPhase.Completed, receiver.Snapshot.Phase);
        }

        internal static byte[] Wire(LegacyModemStepResult step)
        {
            return step.Actions.OfType<LegacyModemAction.SendBytes>().SelectMany(action => action.Bytes).ToArray();
        }

        private static byte[] ReferenceHeader(LegacyModemVariant variant)
        {
            // CRC constants were independently calculated with Python binascii.
            // Offsets come from FTS-0007.003 pages 15-18.
            var frame = new byte[variant == LegacyModemVariant.SeaLink ? 133 : 132];
            frame[0] = variant == LegacyModemVariant.SeaLink ? (byte)1 : (byte)0x16;
            frame[2] = 255;
            frame[3] = 3;
            new byte[] { 0x44, 0x33, 0x22, 0x11 }.CopyTo(frame, 7);
            if (variant == LegacyModemVariant.Telink)
            {
                Array.Fill(frame, (byte)0x20, 11, 16);
            }

            Encoding.ASCII.GetBytes("TEST.BIN").CopyTo(frame, 11);
            Encoding.ASCII.GetBytes("Unit").CopyTo(frame, 28);
            frame[44] = 1;
            if (variant == LegacyModemVariant.SeaLink)
            {
                frame[43] = 1;
                frame[131] = 0x88;
                frame[132] = 0x97;
            }
            else
            {
                frame[131] = 0x95;
            }

            return frame;
        }

        private static byte[] ReferenceData()
        {
            var frame = Enumerable.Repeat((byte)0x1a, 133).ToArray();
            new byte[] { 1, 1, 254, 65, 66, 67 }.CopyTo(frame, 0);
            frame[131] = 0x41;
            frame[132] = 0x4b;
            return frame;
        }
    }
}
