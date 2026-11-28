using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.Services.Translation;
using Xunit;

namespace EhViewer.Desktop.Tests;

public sealed class TranslationRuntimeStabilityTests : DesktopTestBase
{
    [Theory]
    [InlineData(2048, 2048L, 1024, true)]
    [InlineData(2048, null, 1024, false)]
    [InlineData(4096, 2048L, 1024, false)]
    [InlineData(1024, 2048L, 1024, false)]
    [InlineData(512, 512L, 1024, false)]
    public void IsRangeSatisfiedAsComplete_ValidatesExactContentRangeMatch(
        long existingBytes,
        long? contentRangeTotalLength,
        long minValidBytes,
        bool expected)
    {
        bool actual = ComicBubbleOcrPipeline.IsRangeSatisfiedAsComplete(
            existingBytes,
            contentRangeTotalLength,
            minValidBytes);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IsValidModelFile_Onnx_AcceptsValidProtobufAndRejectsCorruptFiles()
    {
        string tempDir = CreateTempDirectory("trans-onnx-valid");

        string validOnnx = Path.Combine(tempDir, "valid.onnx");
        File.WriteAllBytes(validOnnx, CreateValidOnnxBytes(2048));

        string emptyFile = Path.Combine(tempDir, "empty.onnx");
        File.WriteAllBytes(emptyFile, []);

        string tooSmall = Path.Combine(tempDir, "small.onnx");
        File.WriteAllBytes(tooSmall, CreateValidOnnxBytes(512));

        string allZeros = Path.Combine(tempDir, "zeros.onnx");
        File.WriteAllBytes(allZeros, new byte[2048]);

        string htmlError = Path.Combine(tempDir, "html.onnx");
        File.WriteAllText(
            htmlError,
            "<!DOCTYPE html><html><body>502 Bad Gateway " + new string('x', 1200) + "</body></html>",
            Encoding.UTF8);

        string jsonError = Path.Combine(tempDir, "json.onnx");
        File.WriteAllText(
            jsonError,
            "{\"error\":\"Not Found\",\"detail\":\"" + new string('a', 1200) + "\"}",
            Encoding.UTF8);

        Assert.True(ComicBubbleOcrPipeline.IsValidModelFile(validOnnx, isVocabFile: false));
        Assert.False(ComicBubbleOcrPipeline.IsValidModelFile(emptyFile, isVocabFile: false));
        Assert.False(ComicBubbleOcrPipeline.IsValidModelFile(tooSmall, isVocabFile: false));
        Assert.False(ComicBubbleOcrPipeline.IsValidModelFile(allZeros, isVocabFile: false));
        Assert.False(ComicBubbleOcrPipeline.IsValidModelFile(htmlError, isVocabFile: false));
        Assert.False(ComicBubbleOcrPipeline.IsValidModelFile(jsonError, isVocabFile: false));
    }

    [Fact]
    public void IsValidModelFile_Vocab_AcceptsValidMultiLineVocabAndRejectsErrorPages()
    {
        string tempDir = CreateTempDirectory("trans-vocab-valid");

        string validVocab = Path.Combine(tempDir, "vocab.txt");
        File.WriteAllText(validVocab, CreateValidVocabText(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        string validBomVocab = Path.Combine(tempDir, "vocab-bom.txt");
        File.WriteAllText(validBomVocab, CreateValidVocabText(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        string shortVocab = Path.Combine(tempDir, "short-vocab.txt");
        File.WriteAllText(shortVocab, "tok1\ntok2\ntok3\n", Encoding.UTF8);

        string htmlVocab = Path.Combine(tempDir, "html-vocab.txt");
        File.WriteAllText(
            htmlVocab,
            "<html>\n<body>\n1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n" + new string('x', 128) + "\n</body>\n</html>",
            Encoding.UTF8);

        string jsonVocab = Path.Combine(tempDir, "json-vocab.txt");
        File.WriteAllText(
            jsonVocab,
            "{\n\"error\": \"rate limited\",\n\"a\":1,\n\"b\":2,\n\"c\":3,\n\"d\":4,\n\"e\":5,\n\"f\":6,\n\"g\":7,\n\"h\":8,\n\"i\":9\n}",
            Encoding.UTF8);

        Assert.True(ComicBubbleOcrPipeline.IsValidModelFile(validVocab, isVocabFile: true));
        Assert.True(ComicBubbleOcrPipeline.IsValidModelFile(validBomVocab, isVocabFile: true));
        Assert.False(ComicBubbleOcrPipeline.IsValidModelFile(shortVocab, isVocabFile: true));
        Assert.False(ComicBubbleOcrPipeline.IsValidModelFile(htmlVocab, isVocabFile: true));
        Assert.False(ComicBubbleOcrPipeline.IsValidModelFile(jsonVocab, isVocabFile: true));
    }

    [Fact]
    public async Task DownloadFileWithResumeAsync_On416WithoutContentRange_DeletesCorruptPartAndRetriesCleanly()
    {
        AppPaths paths = CreateTempAppPaths("trans-416-retry", ensureCreated: false);
        string destPath = Path.Combine(paths.OcrModelsDirectory, "model.onnx");
        string partPath = destPath + ".part";
        Directory.CreateDirectory(paths.OcrModelsDirectory);
        await File.WriteAllTextAsync(partPath, "corrupt partial data", Encoding.UTF8);

        byte[] validPayload = CreateValidOnnxBytes(2048);
        var seenRanges = new List<RangeHeaderValue?>();
        int callCount = 0;

        using var handler = new StubHttpMessageHandler(request =>
        {
            callCount++;
            seenRanges.Add(request.Headers.Range);
            if (callCount == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    Content = new ByteArrayContent([])
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(validPayload)
            };
        });

        using var httpClient = new HttpClient(handler);
        using var pipeline = new ComicBubbleOcrPipeline(paths, httpClient);

        await pipeline.DownloadFileWithResumeAsync(
            "https://example.test/model.onnx",
            destPath,
            "test-model",
            progress: null,
            CancellationToken.None);

        Assert.Equal(2, callCount);
        Assert.NotNull(seenRanges[0]);
        Assert.Null(seenRanges[1]);
        Assert.True(File.Exists(destPath));
        Assert.False(File.Exists(partPath));
        Assert.Equal(validPayload, await File.ReadAllBytesAsync(destPath));
    }

    [Fact]
    public async Task DownloadFileWithResumeAsync_On416WithMatchingContentRangeAndValidPart_PromotesDirectly()
    {
        AppPaths paths = CreateTempAppPaths("trans-416-promote", ensureCreated: false);
        string destPath = Path.Combine(paths.OcrModelsDirectory, "model.onnx");
        string partPath = destPath + ".part";
        Directory.CreateDirectory(paths.OcrModelsDirectory);

        byte[] validPayload = CreateValidOnnxBytes(2048);
        await File.WriteAllBytesAsync(partPath, validPayload);

        int callCount = 0;
        using var handler = new StubHttpMessageHandler(_ =>
        {
            callCount++;
            var content = new ByteArrayContent([]);
            content.Headers.ContentRange = new ContentRangeHeaderValue(validPayload.Length);
            return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                Content = content
            };
        });

        using var httpClient = new HttpClient(handler);
        using var pipeline = new ComicBubbleOcrPipeline(paths, httpClient);

        await pipeline.DownloadFileWithResumeAsync(
            "https://example.test/model.onnx",
            destPath,
            "test-model",
            progress: null,
            CancellationToken.None);

        Assert.Equal(1, callCount);
        Assert.True(File.Exists(destPath));
        Assert.False(File.Exists(partPath));
        Assert.Equal(validPayload, await File.ReadAllBytesAsync(destPath));
    }

    [Fact]
    public async Task DownloadFileWithResumeAsync_OnHtmlErrorPage200_RejectsAndCleansPartFile()
    {
        AppPaths paths = CreateTempAppPaths("trans-html-reject", ensureCreated: false);
        string destPath = Path.Combine(paths.OcrModelsDirectory, "model.onnx");
        string partPath = destPath + ".part";
        byte[] htmlPayload = Encoding.UTF8.GetBytes(
            "<!DOCTYPE html><html><body>Cloudflare error " + new string('e', 1500) + "</body></html>");

        using var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(htmlPayload)
        });

        using var httpClient = new HttpClient(handler);
        using var pipeline = new ComicBubbleOcrPipeline(paths, httpClient);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            pipeline.DownloadFileWithResumeAsync(
                "https://example.test/model.onnx",
                destPath,
                "test-model",
                progress: null,
                CancellationToken.None));

        Assert.False(File.Exists(destPath));
        Assert.False(File.Exists(partPath));
    }

    [Fact]
    public void FindFreePort_ReturnsPreferredWhenAvailableAndFallsBackWhenOccupied()
    {
        using var occupiedListener = new TcpListener(IPAddress.Loopback, 0);
        occupiedListener.Start();
        int occupiedPort = ((IPEndPoint)occupiedListener.LocalEndpoint).Port;

        int fallbackPort = LlamaCppServerManager.FindFreePort(occupiedPort);
        Assert.NotEqual(occupiedPort, fallbackPort);
        Assert.InRange(fallbackPort, 1025, 65535);

        occupiedListener.Stop();

        int expectedPreferred = Math.Clamp(occupiedPort, 1025, 65534);
        int freePort = LlamaCppServerManager.FindFreePort(expectedPreferred);
        Assert.Equal(expectedPreferred, freePort);
    }

    [Fact]
    public void ChildProcessJobObject_LayoutAndLifecycle_WorkOnWindowsX64()
    {
        if (Environment.Is64BitProcess)
        {
            Assert.Equal(144, LlamaCppServerManager.ChildProcessJobObject.ExtendedLimitInformationSize);
        }

        if (OperatingSystem.IsWindows())
        {
            LlamaCppServerManager.ChildProcessJobObject? job = LlamaCppServerManager.ChildProcessJobObject.TryCreate();
            Assert.NotNull(job);
            job!.Dispose();
            job.Dispose();
        }
    }

    private static byte[] CreateValidOnnxBytes(int totalLength)
    {
        var bytes = new byte[totalLength];
        // ONNX ModelProto field 1 (ir_version, varint tag 0x08) = 7, field 2 (producer_name, tag 0x12) = "test"
        bytes[0] = 0x08;
        bytes[1] = 0x07;
        bytes[2] = 0x12;
        bytes[3] = 0x04;
        bytes[4] = (byte)'t';
        bytes[5] = (byte)'e';
        bytes[6] = (byte)'s';
        bytes[7] = (byte)'t';
        for (int i = 8; i < totalLength; i++)
        {
            bytes[i] = (byte)((i % 251) + 1);
        }
        return bytes;
    }

    private static string CreateValidVocabText()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 16; i++)
        {
            sb.Append("token_").Append(i).AppendLine("_value");
        }
        return sb.ToString();
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
