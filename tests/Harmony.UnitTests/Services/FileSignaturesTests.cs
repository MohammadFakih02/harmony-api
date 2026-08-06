using FluentAssertions;
using Harmony.Application.Services;

namespace Harmony.UnitTests.Services;

/// <summary>
/// Pure magic-byte sniffer tests — no IO, no MinIO. Each allowed non-image type matches its real
/// signature and rejects a mismatch; signature-less text is accepted; unknown types are rejected.
/// </summary>
public class FileSignaturesTests
{
    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    public void IsImage_TrueForImageTypes(string contentType) =>
        FileSignatures.IsImage(contentType).Should().BeTrue();

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("video/mp4")]
    [InlineData("audio/mpeg")]
    [InlineData("application/zip")]
    public void IsImage_FalseForNonImageTypes(string contentType) =>
        FileSignatures.IsImage(contentType).Should().BeFalse();

    [Fact]
    public void Pdf_MatchesHeader() =>
        FileSignatures.IsConsistent("application/pdf", "%PDF-1.7"u8).Should().BeTrue();

    [Fact]
    public void Pdf_RejectsNonPdf() =>
        FileSignatures.IsConsistent("application/pdf", "nope"u8).Should().BeFalse();

    [Fact]
    public void Mp4_MatchesFtypAtOffset4()
    {
        byte[] head = [0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'m', (byte)'p', (byte)'4', (byte)'2'];
        FileSignatures.IsConsistent("video/mp4", head).Should().BeTrue();
        FileSignatures.IsConsistent("video/quicktime", head).Should().BeTrue();
    }

    [Fact]
    public void Mp4_RejectsWithoutFtyp()
    {
        byte[] head = [0x00, 0x00, 0x00, 0x18, (byte)'x', (byte)'x', (byte)'x', (byte)'x'];
        FileSignatures.IsConsistent("video/mp4", head).Should().BeFalse();
    }

    [Fact]
    public void Webm_MatchesEbmlHeader()
    {
        byte[] head = [0x1A, 0x45, 0xDF, 0xA3, 0x01, 0x02];
        FileSignatures.IsConsistent("video/webm", head).Should().BeTrue();
        FileSignatures.IsConsistent("audio/webm", head).Should().BeTrue();
    }

    [Fact]
    public void Mp3_MatchesId3Tag() =>
        FileSignatures.IsConsistent("audio/mpeg", "ID3\x03"u8).Should().BeTrue();

    [Fact]
    public void Mp3_MatchesFrameSync()
    {
        byte[] head = [0xFF, 0xFB, 0x90, 0x00];
        FileSignatures.IsConsistent("audio/mpeg", head).Should().BeTrue();
    }

    [Fact]
    public void Mp3_RejectsGarbage() =>
        FileSignatures.IsConsistent("audio/mpeg", "garbage"u8).Should().BeFalse();

    [Fact]
    public void Ogg_MatchesHeader() =>
        FileSignatures.IsConsistent("audio/ogg", "OggS\x00"u8).Should().BeTrue();

    [Fact]
    public void Wav_MatchesRiffWave()
    {
        byte[] head = [(byte)'R', (byte)'I', (byte)'F', (byte)'F', 0x24, 0x08, 0x00, 0x00, (byte)'W', (byte)'A', (byte)'V', (byte)'E'];
        FileSignatures.IsConsistent("audio/wav", head).Should().BeTrue();
    }

    [Fact]
    public void Wav_RejectsRiffWithoutWave()
    {
        byte[] head = [(byte)'R', (byte)'I', (byte)'F', (byte)'F', 0x24, 0x08, 0x00, 0x00, (byte)'A', (byte)'V', (byte)'I', (byte)' '];
        FileSignatures.IsConsistent("audio/wav", head).Should().BeFalse();
    }

    [Fact]
    public void Zip_MatchesLocalFileSignature()
    {
        byte[] head = [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00];
        FileSignatures.IsConsistent("application/zip", head).Should().BeTrue();
    }

    [Fact]
    public void Zip_RejectsNonZip()
    {
        byte[] head = [0x50, 0x4B, 0x01, 0x02]; // central-directory header, not a stream start
        FileSignatures.IsConsistent("application/zip", head).Should().BeFalse();
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("text/csv")]
    [InlineData("text/markdown")]
    public void Text_AcceptedWithoutSignature(string contentType) =>
        FileSignatures.IsConsistent(contentType, "any bytes at all"u8).Should().BeTrue();

    [Fact]
    public void UnknownType_Rejected() =>
        FileSignatures.IsConsistent("application/x-msdownload", "MZ"u8).Should().BeFalse();

    [Fact]
    public void EmptyBuffer_RejectsSignatureType() =>
        FileSignatures.IsConsistent("application/pdf", ReadOnlySpan<byte>.Empty).Should().BeFalse();

    [Fact]
    public void ImageType_NotHandledHere() =>
        // Images are validated by the caller's decode, not by this sniffer.
        FileSignatures.IsConsistent("image/png", [0x89, 0x50, 0x4E, 0x47]).Should().BeFalse();
}
