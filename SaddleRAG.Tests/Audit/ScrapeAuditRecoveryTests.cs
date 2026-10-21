using Microsoft.Extensions.Logging.Abstractions;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Ingestion.Diagnostics;

namespace SaddleRAG.Tests.Audit;

public sealed class ScrapeAuditRecoveryTests
{
    [Fact]
    public async Task FailedBatchIsRetainedAndLaterEventsStillFlush()
    {
        var repository = Substitute.For<IScrapeAuditRepository>();
        bool available = false;
        var saved = new HashSet<string>();
        repository.InsertManyAsync(Arg.Any<IEnumerable<ScrapeAuditLogEntry>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (!available)
                    throw new IOException("offline");
                foreach (var entry in call.Arg<IEnumerable<ScrapeAuditLogEntry>>() ?? [])
                    saved.Add(entry.Id);
                return Task.CompletedTask;
            });
        await using var writer = new ScrapeAuditWriter(repository, null, batchSize: 2,
            flushInterval: TimeSpan.FromMilliseconds(20), logger: NullLogger<ScrapeAuditWriter>.Instance);
        writer.RecordFetched(new AuditContext { JobId = "job", LibraryId = "library", Version = "1" },
            "https://example.com/one", null, "example.com", 0);
        await Assert.ThrowsAsync<IOException>(() => writer.FlushAsync(TestContext.Current.CancellationToken));
        available = true;
        writer.RecordFetched(new AuditContext { JobId = "job", LibraryId = "library", Version = "1" },
            "https://example.com/two", null, "example.com", 0);
        await writer.FlushAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, saved.Count);
    }

    [Fact]
    public async Task OutageCannotAccumulateAnUnboundedQueue()
    {
        var repository = Substitute.For<IScrapeAuditRepository>();
        repository.InsertManyAsync(Arg.Any<IEnumerable<ScrapeAuditLogEntry>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("offline")));
        await using var writer = new ScrapeAuditWriter(repository, null, batchSize: 1,
            flushInterval: TimeSpan.FromHours(1), capacity: 2);
        int accepted = 0;
        Assert.Throws<InvalidOperationException>(() =>
        {
            for (int index = 0; index < 10; index++)
            {
                writer.RecordFetched(new AuditContext { JobId = "job", LibraryId = "library", Version = "1" },
                    "https://example.com/" + index, null, "example.com", 0);
                accepted++;
            }
        });
        Assert.InRange(accepted, 2, 3);
    }
}
