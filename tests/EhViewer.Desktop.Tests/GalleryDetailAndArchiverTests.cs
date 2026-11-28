using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Client.Parser;
using EhViewer.Core.Network;
using EhViewer.Core.Storage;
using EhViewer.Desktop.Downloads;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EhViewer.Desktop.Tests;

public sealed class GalleryDetailAndArchiverTests : DesktopTestBase
{
    private readonly string _root;

    public GalleryDetailAndArchiverTests()
    {
        _root = CreateTempDirectory("eh-detail-archiver");
    }

    [Fact]
    public async Task ArchiverDownloadManager_HttpRange206Resume_CompletesWithExactPayload()
    {
        byte[] payload = new byte[1024];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        var firstChunkRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long? requestedRangeStart = null;
        int callCount = 0;

        var handler = new DelegateHttpHandler(async (request, token) =>
        {
            int attempt = Interlocked.Increment(ref callCount);
            if (attempt == 1)
            {
                Assert.Null(request.Headers.Range);
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new PauseAfterChunkContent(payload[..512], firstChunkRead)
                };
                resp.Content.Headers.ContentLength = payload.Length;
                return resp;
            }

            requestedRangeStart = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
            long start = requestedRangeStart ?? 0;
            byte[] slice = payload[(int)start..];
            var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(slice)
            };
            partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, payload.Length - 1, payload.Length);
            partial.Content.Headers.ContentLength = slice.Length;
            await Task.Yield();
            return partial;
        });

        using var httpClient = new HttpClient(handler);
        using var manager = new ArchiverDownloadManager(() => httpClient, () => _root);

        string taskId = manager.Enqueue(5001, "Test Gallery", "https://example.org/archive.zip", "org");
        await firstChunkRead.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Wait until the first 512 bytes are flushed to the .part stream.
        for (int i = 0; i < 100; i++)
        {
            if (manager.TryGetSnapshot(taskId)?.BytesReceived >= 512)
            {
                break;
            }
            await Task.Delay(15);
        }

        Assert.True(manager.Pause(taskId));
        ArchiverTaskSnapshot paused = await manager.WaitForCompletionAsync(taskId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ArchiverTaskState.Paused, paused.State);
        Assert.True(File.Exists(paused.FilePath + ".part"));
        Assert.True(paused.BytesReceived >= 512);

        Assert.True(manager.Resume(taskId));
        ArchiverTaskSnapshot completed = await manager.WaitForCompletionAsync(taskId).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ArchiverTaskState.Completed, completed.State);
        Assert.Equal(512L, requestedRangeStart);
        Assert.True(File.Exists(completed.FilePath));
        Assert.False(File.Exists(completed.FilePath + ".part"));
        Assert.Equal(payload, await File.ReadAllBytesAsync(completed.FilePath));
    }

    [Fact]
    public async Task ArchiverDownloadManager_CancelDeletesPartialFile()
    {
        var firstChunkRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] chunk = new byte[256];
        var handler = new DelegateHttpHandler((_, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new PauseAfterChunkContent(chunk, firstChunkRead)
            };
            resp.Content.Headers.ContentLength = 4096;
            return Task.FromResult(resp);
        });

        using var httpClient = new HttpClient(handler);
        using var manager = new ArchiverDownloadManager(() => httpClient, () => _root);

        string taskId = manager.Enqueue(5002, "Cancel Test", "https://example.org/cancel.zip", "res");
        await firstChunkRead.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(manager.Cancel(taskId, deletePartialFile: true));
        ArchiverTaskSnapshot cancelled = await manager.WaitForCompletionAsync(taskId).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ArchiverTaskState.Cancelled, cancelled.State);
        Assert.False(File.Exists(cancelled.FilePath + ".part"));
        Assert.False(File.Exists(cancelled.FilePath));
    }

    [Fact]
    public async Task ArchiverDownloadManager_AutoImportToLibrary_ExtractsZipAndWritesProgress()
    {
        byte[] zipBytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                ZipArchiveEntry e1 = zip.CreateEntry("001.png");
                using (Stream s1 = e1.Open())
                {
                    s1.Write([137, 80, 78, 71, 13, 10, 26, 10]);
                }

                ZipArchiveEntry e2 = zip.CreateEntry("002.jpg");
                using (Stream s2 = e2.Open())
                {
                    s2.Write([0xFF, 0xD8, 0xFF, 0xE0]);
                }
            }

            zipBytes = ms.ToArray();
        }

        var handler = new DelegateHttpHandler((_, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(zipBytes)
            };
            resp.Content.Headers.ContentLength = zipBytes.Length;
            return Task.FromResult(resp);
        });

        using var httpClient = new HttpClient(handler);
        var scanner = new LocalLibraryScanner(NullLogger<LocalLibraryScanner>.Instance);
        using var manager = new ArchiverDownloadManager(() => httpClient, () => _root, scanner);

        string taskId = manager.Enqueue(5003, "Auto Import Gallery", "https://example.org/auto.zip", "org", autoImportToLibrary: true);
        ArchiverTaskSnapshot completed = await manager.WaitForCompletionAsync(taskId).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ArchiverTaskState.Completed, completed.State);
        Assert.NotNull(completed.ImportedDirectory);
        Assert.True(Directory.Exists(completed.ImportedDirectory));
        Assert.True(File.Exists(Path.Combine(completed.ImportedDirectory!, "00000000.png")));
        Assert.True(File.Exists(Path.Combine(completed.ImportedDirectory!, "00000001.jpg")));
        Assert.True(File.Exists(Path.Combine(completed.ImportedDirectory!, ".ehviewer")));
    }

    [Fact]
    public void GalleryDetailViewModel_Apply_PopulatesCommentsLinksVotesVersionsAndBlacklistFilter()
    {
        AppPaths paths = AppPaths.CreateAt(Path.Combine(_root, "app"));
        paths.EnsureCreated();
        using var db = new EhDatabase(paths.EhDatabasePath);
        var settings = new EhClientSettings();
        using var http = new HttpClient(new DelegateHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var engine = new EhEngine(http, new TestCookieStore(), db, new EhFilter(db), settings);
        var browse = new GalleryBrowseService(engine, settings);
        var vm = new GalleryDetailViewModel(browse);

        var detail = new GalleryDetail
        {
            Gid = 9001,
            Token = "0123456789",
            Title = "Test Detail",
            TorrentCount = 3,
            TorrentUrl = "https://e-hentai.org/gallerytorrents.php?gid=9001&t=0123456789",
            Parent = "https://e-hentai.org/g/8999/abcdef0123/",
            FavoriteSlot = FavListUrlBuilder.FavCatLocal,
            IsFavorited = true,
            NewVersions =
            [
                new NewVersion { VersionName = "v2", VersionUrl = "https://e-hentai.org/g/9002/fedcba9876/" }
            ],
            Comments = new GalleryCommentList(
                [
                    new GalleryComment
                    {
                        Id = 11,
                        User = "alice",
                        Comment = "Hello <br><a href=\"https://e-hentai.org/g/123/abc/\">Ref</a>",
                        Score = 5,
                        Editable = true,
                        VoteUpAble = true,
                        VoteDownAble = true,
                        Time = 1700000000,
                        LastEdited = 1700001000
                    },
                    new GalleryComment
                    {
                        Id = 12,
                        User = "spammer",
                        Comment = "spam",
                        Score = -2,
                        Time = 1700000000
                    }
                ],
                hasMore: false)
        };

        vm.Apply(detail);

        Assert.True(vm.HasTorrents);
        Assert.Equal("种子 (3)", vm.TorrentButtonText);
        Assert.True(vm.HasParent);
        Assert.True(vm.HasVersions);
        Assert.Single(vm.Versions);
        Assert.Equal("已本地收藏", vm.FavoriteButtonText);

        Assert.Equal(2, vm.Comments.Count);
        CommentViewModel first = vm.Comments[0];
        Assert.Contains("Hello", first.Body);
        Assert.True(first.HasLinks);
        Assert.Equal("https://e-hentai.org/g/123/abc/", first.Links![0].Url);
        Assert.True(first.Editable);
        Assert.True(first.VoteUpAble);
        Assert.True(first.VoteDownAble);
        Assert.True(first.HasLastEdited);

        vm.ApplyCommentVote(new VoteCommentParser.Result(11, Score: 6, Vote: 1, ExpectVote: 1));
        Assert.Equal(6, vm.Comments[0].Score);
        Assert.True(vm.Comments[0].VoteUpEd);
        Assert.False(vm.Comments[0].VoteDownEd);

        vm.SetCommentBlacklistFilter(user => string.Equals(user, "spammer", StringComparison.Ordinal));
        Assert.Single(vm.Comments);
        Assert.Equal("alice", vm.Comments[0].User);
    }

    private sealed class DelegateHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> fn)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => fn(request, cancellationToken);
    }

    private sealed class PauseAfterChunkContent(byte[] firstChunk, TaskCompletionSource chunkServed) : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new PauseAfterChunkStream(firstChunk, chunkServed));

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            => Task.FromResult<Stream>(new PauseAfterChunkStream(firstChunk, chunkServed));

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await stream.WriteAsync(firstChunk, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            chunkServed.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = firstChunk.Length * 2;
            return false;
        }
    }

    private sealed class PauseAfterChunkStream(byte[] firstChunk, TaskCompletionSource chunkServed) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _offset; set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < firstChunk.Length)
            {
                int toCopy = Math.Min(buffer.Length, firstChunk.Length - _offset);
                firstChunk.AsMemory(_offset, toCopy).CopyTo(buffer);
                _offset += toCopy;
                return toCopy;
            }

            chunkServed.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private sealed class TestCookieStore : ICookieStore
    {
        public bool HasSignedIn => false;
        public void AddCookie(EhCookie cookie) { }
        public void SaveFromResponse(Uri requestUrl, IEnumerable<string> setCookieHeaders) { }
        public IReadOnlyList<EhCookie> LoadForRequest(Uri requestUrl) => [];
        public IReadOnlyList<EhCookie> GetCookies(Uri url) => [];
        public string GetCookieHeader(Uri url) => string.Empty;
        public bool Contains(Uri url, string name) => false;
        public void SignOut() { }
        public void Clear() { }
    }
}
