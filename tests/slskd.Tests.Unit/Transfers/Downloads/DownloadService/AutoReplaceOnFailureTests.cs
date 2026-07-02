namespace slskd.Tests.Unit.Transfers.Downloads;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using slskd;
using slskd.Events;
using slskd.Files;
using slskd.Integrations.FTP;
using slskd.Relay;
using slskd.Search;
using slskd.Transfers;
using slskd.Transfers.Downloads;
using Soulseek;
using Xunit;

using Transfer = slskd.Transfers.Transfer;

public partial class DownloadServiceTests
{
    private sealed class TestableDownloadService : DownloadService
    {
        public TestableDownloadService(
            IBatchService batchService,
            ISearchService searchService,
            Microsoft.Extensions.Options.IOptionsMonitor<Options> optionsMonitor,
            ISoulseekClient soulseekClient,
            IDbContextFactory<TransfersDbContext> contextFactory,
            FileService fileService,
            IRelayService relayService,
            IFTPService ftpClient,
            EventBus eventBus,
            IServiceProvider serviceProvider)
            : base(batchService, searchService, optionsMonitor, soulseekClient, contextFactory, fileService, relayService, ftpClient, eventBus, serviceProvider)
        {
        }

        public Task CallTryAutoReplaceOnFailureAsync(Transfer transfer, Exception exception)
            => TryAutoReplaceOnFailureAsync(transfer, exception);
    }

    public class AutoReplaceOnFailure
    {
        [Fact]
        public async Task Returns_Without_Replacement_When_Cancelled()
        {
            var mocks = new Mocks(autoReplaceEnabled: true);
            mocks.ServiceProvider
                .Setup(p => p.GetService(typeof(IAutoReplaceService)))
                .Returns(new Mock<IAutoReplaceService>().Object);
            var service = new TestableDownloadService(
                mocks.BatchService.Object,
                mocks.SearchService.Object,
                mocks.OptionsMonitor,
                null,
                new Mock<IDbContextFactory<TransfersDbContext>>().Object,
                null,
                null,
                null,
                null,
                mocks.ServiceProvider.Object);

            await service.CallTryAutoReplaceOnFailureAsync(
                new Transfer { Filename = "test.mp3" },
                new OperationCanceledException());
        }

        [Fact]
        public async Task Returns_Without_Replacement_When_Disabled()
        {
            var mocks = new Mocks(autoReplaceEnabled: false);
            var autoReplace = new Mock<IAutoReplaceService>();
            mocks.ServiceProvider
                .Setup(p => p.GetService(typeof(IAutoReplaceService)))
                .Returns(autoReplace.Object);
            var service = new TestableDownloadService(
                mocks.BatchService.Object,
                mocks.SearchService.Object,
                mocks.OptionsMonitor,
                null,
                new Mock<IDbContextFactory<TransfersDbContext>>().Object,
                null,
                null,
                null,
                null,
                mocks.ServiceProvider.Object);

            await service.CallTryAutoReplaceOnFailureAsync(
                new Transfer { Filename = "test.mp3" },
                new TimeoutException());

            autoReplace.Verify(
                a => a.TryReplaceAsync(It.IsAny<Transfer>(), It.IsAny<AutoReplaceReason>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task Calls_TryReplace_When_Enabled()
        {
            var mocks = new Mocks(autoReplaceEnabled: true);
            var autoReplace = new Mock<IAutoReplaceService>();
            mocks.ServiceProvider
                .Setup(p => p.GetService(typeof(IAutoReplaceService)))
                .Returns(autoReplace.Object);
            var service = new TestableDownloadService(
                mocks.BatchService.Object,
                mocks.SearchService.Object,
                mocks.OptionsMonitor,
                null,
                new Mock<IDbContextFactory<TransfersDbContext>>().Object,
                null,
                null,
                null,
                null,
                mocks.ServiceProvider.Object);
            var transfer = new Transfer { Filename = "test.mp3" };

            await service.CallTryAutoReplaceOnFailureAsync(transfer, new TimeoutException());

            autoReplace.Verify(
                a => a.TryReplaceAsync(transfer, AutoReplaceReason.Failure, It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task Does_Not_Throw_When_TryReplace_Throws()
        {
            var mocks = new Mocks(autoReplaceEnabled: true);
            var autoReplace = new Mock<IAutoReplaceService>();
            autoReplace
                .Setup(a => a.TryReplaceAsync(It.IsAny<Transfer>(), It.IsAny<AutoReplaceReason>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("search failed"));
            mocks.ServiceProvider
                .Setup(p => p.GetService(typeof(IAutoReplaceService)))
                .Returns(autoReplace.Object);
            var service = new TestableDownloadService(
                mocks.BatchService.Object,
                mocks.SearchService.Object,
                mocks.OptionsMonitor,
                null,
                new Mock<IDbContextFactory<TransfersDbContext>>().Object,
                null,
                null,
                null,
                null,
                mocks.ServiceProvider.Object);

            var ex = await Record.ExceptionAsync(() =>
                service.CallTryAutoReplaceOnFailureAsync(new Transfer { Filename = "test.mp3" }, new TimeoutException()));

            Assert.Null(ex);
        }

        [Fact]
        public async Task Does_Not_Throw_When_Service_Not_Registered()
        {
            var mocks = new Mocks(autoReplaceEnabled: true);
            mocks.ServiceProvider
                .Setup(p => p.GetService(typeof(IAutoReplaceService)))
                .Returns(null);
            var service = new TestableDownloadService(
                mocks.BatchService.Object,
                mocks.SearchService.Object,
                mocks.OptionsMonitor,
                null,
                new Mock<IDbContextFactory<TransfersDbContext>>().Object,
                null,
                null,
                null,
                null,
                mocks.ServiceProvider.Object);

            var ex = await Record.ExceptionAsync(() =>
                service.CallTryAutoReplaceOnFailureAsync(new Transfer { Filename = "test.mp3" }, new TimeoutException()));

            Assert.Null(ex);
        }
    }
}
