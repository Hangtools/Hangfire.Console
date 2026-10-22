using Hangfire.Console.Serialization;
using Hangfire.Console.Server;
using Hangfire.Console.Storage;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Moq;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Xunit;

namespace Hangfire.Console.Tests.Server
{
    public class ConsoleContextFacts
    {
        private readonly Mock<IConsoleStorage> _storage;
        private readonly Mock<JobStorage> _jobStorage;
        private readonly Mock<IMonitoringApi> _monitoringApi;

        private readonly ConsoleOptions _options;

        public ConsoleContextFacts()
        {
            _storage = new Mock<IConsoleStorage>();
            _storage.Setup(x => x.GetState(It.IsAny<ConsoleId>()))
                .Returns(new StateData { Name = ProcessingState.StateName });
            _jobStorage = new Mock<JobStorage>();
            _monitoringApi = new Mock<IMonitoringApi>();
            _jobStorage.Setup(x => x.GetMonitoringApi()).Returns(_monitoringApi.Object);
            _options = new ConsoleOptions();
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public async Task ConsoleOperations_DoNotEnlistInTheJobTransaction(bool followJobRetention, bool asyncFlow)
        {
            _storage.Setup(x => x.InitConsole(It.IsAny<ConsoleId>()))
                .Callback(() => Assert.Null(Transaction.Current));
            _storage.Setup(x => x.AddLine(It.IsAny<ConsoleId>(), It.IsAny<ConsoleLine>()))
                .Callback(() => Assert.Null(Transaction.Current));
            _storage.Setup(x => x.GetConsoleTtl(It.IsAny<ConsoleId>()))
                .Callback(() => Assert.Null(Transaction.Current))
                .Returns(TimeSpan.FromMinutes(1));
            _storage.Setup(x => x.Expire(It.IsAny<ConsoleId>(), It.IsAny<TimeSpan>()))
                .Callback(() => Assert.Null(Transaction.Current));
            _options.FollowJobRetentionPolicy = followJobRetention;

            using (var transaction = new TransactionScope(TransactionScopeOption.Required,
                asyncFlow ? TransactionScopeAsyncFlowOption.Enabled : TransactionScopeAsyncFlowOption.Suppress))
            {
                var ambient = Transaction.Current;
                if (asyncFlow) await Task.Yield();
                var context = new ConsoleContext(new ConsoleId("1", DateTime.UtcNow), _storage.Object, _options);

                context.WriteLine("Diagnostic message", null);
                context.WriteProgressBar("Import", 50, null);
                context.Complete();

                Assert.Same(ambient, Transaction.Current);
            }

            _storage.Verify(x => x.Expire(It.IsAny<ConsoleId>(), It.IsAny<TimeSpan>()), Times.Once);
            _storage.Verify(x => x.Dispose(), Times.Once);
        }

        [Fact]
        public void Complete_IgnoresLateTextAndProgressWrites()
        {
            var context = new ConsoleContext(new ConsoleId("1", DateTime.UtcNow), _storage.Object, _options);
            var progress = context.WriteProgressBar(null, 0, null);
            _storage.Invocations.Clear();

            context.Complete();
            context.WriteLine("Late callback", null);
            progress.SetValue(100);
            context.Complete();

            _storage.Verify(x => x.AddLine(It.IsAny<ConsoleId>(), It.IsAny<ConsoleLine>()), Times.Never);
            _storage.Verify(x => x.Dispose(), Times.Once);
        }

        [Fact]
        public void Complete_DisposesStorageEvenIfExpirationFails()
        {
            _options.FollowJobRetentionPolicy = false;
            _storage.Setup(x => x.Expire(It.IsAny<ConsoleId>(), It.IsAny<TimeSpan>()))
                .Throws(new InvalidOperationException("Storage unavailable"));
            var context = new ConsoleContext(new ConsoleId("1", DateTime.UtcNow), _storage.Object, _options);

            Assert.Throws<InvalidOperationException>(() => context.Complete());
            context.Complete();

            _storage.Verify(x => x.Dispose(), Times.Once);
        }

        [Fact]
        public async Task Complete_WaitsForAnInFlightWriteBeforeDisposingStorage()
        {
            using (var writeEntered = new ManualResetEventSlim())
            using (var allowWrite = new ManualResetEventSlim())
            using (var completionStarted = new ManualResetEventSlim())
            {
                var writeFinished = 0;
                _storage.Setup(x => x.AddLine(It.IsAny<ConsoleId>(), It.IsAny<ConsoleLine>()))
                    .Callback(() =>
                    {
                        writeEntered.Set();
                        Assert.True(allowWrite.Wait(10_000));
                        Volatile.Write(ref writeFinished, 1);
                    });
                _storage.Setup(x => x.Dispose())
                    .Callback(() => Assert.Equal(1, Volatile.Read(ref writeFinished)));
                var context = new ConsoleContext(new ConsoleId("1", DateTime.UtcNow), _storage.Object, _options);
                var write = Task.Factory.StartNew(() => context.WriteLine("In flight", null),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Task complete = null;

                try
                {
                    Assert.True(writeEntered.Wait(10_000));
                    complete = Task.Factory.StartNew(() =>
                    {
                        completionStarted.Set();
                        context.Complete();
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    Assert.True(completionStarted.Wait(10_000));
                    Assert.NotSame(complete, await Task.WhenAny(complete, Task.Delay(100)));
                    allowWrite.Set();
                    await Task.WhenAll(write, complete);

                    context.WriteLine("After completion", null);
                    _storage.Verify(x => x.AddLine(It.IsAny<ConsoleId>(), It.IsAny<ConsoleLine>()), Times.Once);
                    _storage.Verify(x => x.Dispose(), Times.Once);
                }
                finally
                {
                    allowWrite.Set();
                    await Task.WhenAll(write, complete ?? Task.CompletedTask);
                }
            }
        }

        [Fact]
        public void Ctor_ThrowsException_IfConsoleIdIsNull()
        {
            Assert.Throws<ArgumentNullException>("consoleId", () => new ConsoleContext(null, _storage.Object, _options));
        }

        [Fact]
        public void Ctor_ThrowsException_IfStorageIsNull()
        {
            var consoleId = new ConsoleId("1", new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            Assert.Throws<ArgumentNullException>("storage", () => new ConsoleContext(consoleId, null, _options));
        }

        [Fact]
        public void Ctor_ThrowsException_IfOptionsIsNull()
        {
            var consoleId = new ConsoleId("1", new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            Assert.Throws<ArgumentNullException>("options", () => new ConsoleContext(consoleId, _storage.Object, null));
        }

        [Fact]
        public void Ctor_InitializesConsole()
        {
            var consoleId = new ConsoleId("1", new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var context = new ConsoleContext(consoleId, _storage.Object, _options);

            _storage.Verify(x => x.InitConsole(consoleId));
        }

        [Fact]
        public void AddLine_ThrowsException_IfLineIsNull()
        {
            var consoleId = new ConsoleId("1", new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var context = new ConsoleContext(consoleId, _storage.Object, _options);

            Assert.Throws<ArgumentNullException>("line", () => context.AddLine(null));
        }

        [Fact]
        public void AddLine_ReallyAddsLine()
        {
            var consoleId = new ConsoleId("1", new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var context = new ConsoleContext(consoleId, _storage.Object, _options);

            context.AddLine(new ConsoleLine() { TimeOffset = 0, Message = "line" });

            _storage.Verify(x => x.AddLine(It.IsAny<ConsoleId>(), It.IsAny<ConsoleLine>()));
        }

        [Fact]
        public void AddLine_CorrectsTimeOffset()
        {
            var consoleId = new ConsoleId("1", new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var context = new ConsoleContext(consoleId, _storage.Object, _options);

            var line1 = new ConsoleLine() { TimeOffset = 0, Message = "line" };
            var line2 = new ConsoleLine() { TimeOffset = 0, Message = "line" };

            context.AddLine(line1);
            context.AddLine(line2);

            _storage.Verify(x => x.AddLine(It.IsAny<ConsoleId>(), It.IsAny<ConsoleLine>()), Times.Exactly(2));
            Assert.NotEqual(line1.TimeOffset, line2.TimeOffset, 4);
        }

        [Fact]
        public void WriteLine_ReallyAddsLine()
        {
            var consoleId = new ConsoleId("1", new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var context = new ConsoleContext(consoleId, _storage.Object, _options);

            context.WriteLine("line", null);

            _storage.Verify(x => x.AddLine(It.IsAny<ConsoleId>(), It.Is<ConsoleLine>(_ => _.Message == "line" && _.TextColor == null)));
        }

        [Fact]
        public void WriteLine_ReallyAddsLineWithColor()
        {
            var consoleId = new ConsoleId("1", new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var context = new ConsoleContext(consoleId, _storage.Object, _options);

            context.WriteLine("line", ConsoleTextColor.Red);

            _storage.Verify(x => x.AddLine(It.IsAny<ConsoleId>(), It.Is<ConsoleLine>(_ => _.Message == "line" && _.TextColor == ConsoleTextColor.Red)));
        }

        [Fact]
        public void WriteProgressBar_WritesDefaults_AndReturnsNonNull()
        {
            var consoleId = new ConsoleId("1", new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var context = new ConsoleContext(consoleId, _storage.Object, _options);

            var progressBar = context.WriteProgressBar(null, 0, null);

            _storage.Verify(x => x.AddLine(It.IsAny<ConsoleId>(), It.IsAny<ConsoleLine>()));
            Assert.NotNull(progressBar);
        }
        
        [Fact]
        public void WriteProgressBar_WritesName_AndReturnsNonNull()
        {
            var consoleId = new ConsoleId("1", new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var context = new ConsoleContext(consoleId, _storage.Object, _options);

            var progressBar = context.WriteProgressBar("test", 0, null);

            _storage.Verify(x => x.AddLine(It.IsAny<ConsoleId>(), It.Is<ConsoleLine>(_ => _.ProgressName == "test")));
            Assert.NotNull(progressBar);
        }
        
        [Fact]
        public void WriteProgressBar_WritesInitialValue_AndReturnsNonNull()
        {
            var consoleId = new ConsoleId("1", new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var context = new ConsoleContext(consoleId, _storage.Object, _options);

            var progressBar = context.WriteProgressBar(null, 5, null);

            _storage.Verify(x => x.AddLine(It.IsAny<ConsoleId>(), It.Is<ConsoleLine>(_ => _.ProgressValue == 5)));
            Assert.NotNull(progressBar);
        }

        [Fact]
        public void WriteProgressBar_WritesProgressBarColor_AndReturnsNonNull()
        {
            var consoleId = new ConsoleId("1", new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var context = new ConsoleContext(consoleId, _storage.Object, _options);

            var progressBar = context.WriteProgressBar(null, 0, ConsoleTextColor.Red);

            _storage.Verify(x => x.AddLine(It.IsAny<ConsoleId>(), It.Is<ConsoleLine>(_ => _.TextColor == ConsoleTextColor.Red)));
            Assert.NotNull(progressBar);
        }

        [Fact]
        public void Expire_ReallyExpiresLines()
        {
            var consoleId = new ConsoleId("1", new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var context = new ConsoleContext(consoleId, _storage.Object, _options);

            context.Expire(TimeSpan.FromHours(1));

            _storage.Verify(x => x.Expire(It.IsAny<ConsoleId>(), It.IsAny<TimeSpan>()));
        }
        
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void FixExpiration_RemovesRecreatedKeysWhenParentHasExpired(int ttlSeconds)
        {
            _storage.Setup(x => x.GetConsoleTtl(It.IsAny<ConsoleId>()))
                .Returns(TimeSpan.FromSeconds(ttlSeconds));
            _storage.Setup(x => x.GetState(It.IsAny<ConsoleId>())).Returns((StateData)null);
            var context = new ConsoleContext(new ConsoleId("1", DateTime.UtcNow), _storage.Object, _options);

            context.Complete();

            _storage.Verify(x => x.Expire(It.IsAny<ConsoleId>(), TimeSpan.Zero), Times.Once);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void FixExpiration_DoesNotExpirePersistentConsoleWhileParentExists(int ttlSeconds)
        {
            _storage.Setup(x => x.GetConsoleTtl(It.IsAny<ConsoleId>()))
                .Returns(TimeSpan.FromSeconds(ttlSeconds));

            var consoleId = new ConsoleId("1", new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var context = new ConsoleContext(consoleId, _storage.Object, _options);

            context.FixExpiration();

            _storage.Verify(x => x.GetConsoleTtl(It.IsAny<ConsoleId>()));
            _storage.Verify(x => x.Expire(It.IsAny<ConsoleId>(), It.IsAny<TimeSpan>()), Times.Never);
        }

        [Fact]
        public void FixExpiration_RequestsConsoleTtl_ExpiresIfPositive()
        {
            _storage.Setup(x => x.GetConsoleTtl(It.IsAny<ConsoleId>()))
                .Returns(TimeSpan.FromHours(1));

            var consoleId = new ConsoleId("1", new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var context = new ConsoleContext(consoleId, _storage.Object, _options);

            context.FixExpiration();

            _storage.Verify(x => x.GetConsoleTtl(It.IsAny<ConsoleId>()));
            _storage.Verify(x => x.Expire(It.IsAny<ConsoleId>(), It.IsAny<TimeSpan>()));
        }

        [Theory]
        [InlineData(-0.3, -60, "Deleted")]
        [InlineData(-1, -60, "CustomFinal")]
        [InlineData(0, 3600, "Deleted")]
        [InlineData(-1, 3600, "CustomFinal")]
        [InlineData(-37, 3600, "CustomFinal")]
        public void Complete_UsesParentExpirationWhenConsoleTtlIsNonpositive(double ttlSeconds, int expiresInSeconds, string stateName)
        {
            var expiresAt = DateTime.UtcNow.AddSeconds(expiresInSeconds);
            _storage.Setup(x => x.GetConsoleTtl(It.IsAny<ConsoleId>())).Returns(TimeSpan.FromSeconds(ttlSeconds));
            _storage.Setup(x => x.GetState(It.IsAny<ConsoleId>())).Returns(new StateData { Name = stateName });
            _monitoringApi.Setup(x => x.JobDetails("1")).Returns(new JobDetailsDto { ExpireAt = expiresAt });
            var actualTtl = TimeSpan.MinValue;
            _storage.Setup(x => x.Expire(It.IsAny<ConsoleId>(), It.IsAny<TimeSpan>()))
                .Callback<ConsoleId, TimeSpan>((_, ttl) => actualTtl = ttl);
            var context = new ConsoleContext(new ConsoleId("1", DateTime.UtcNow), _storage.Object, _options, _jobStorage.Object);

            var startedAt = DateTime.UtcNow;
            context.Complete();
            var finishedAt = DateTime.UtcNow;

            if (expiresInSeconds < 0) Assert.Equal(TimeSpan.Zero, actualTtl);
            else Assert.InRange(actualTtl, expiresAt - finishedAt, expiresAt - startedAt);
            _monitoringApi.Verify(x => x.JobDetails("1"), Times.Once);
            _storage.Verify(x => x.Expire(It.IsAny<ConsoleId>(), It.IsAny<TimeSpan>()), Times.Once);
            _storage.Verify(x => x.Dispose(), Times.Once);
        }

        [Theory]
        [InlineData("Failed")]
        [InlineData("CustomState")]
        public void Complete_PreservesSessionWhenParentExpirationIsUnavailable(string stateName)
        {
            _storage.Setup(x => x.GetConsoleTtl(It.IsAny<ConsoleId>())).Returns(TimeSpan.FromSeconds(-37));
            _storage.Setup(x => x.GetState(It.IsAny<ConsoleId>())).Returns(new StateData { Name = stateName });
            _monitoringApi.Setup(x => x.JobDetails("1")).Returns(new JobDetailsDto());
            var context = new ConsoleContext(new ConsoleId("1", DateTime.UtcNow), _storage.Object, _options, _jobStorage.Object);

            context.Complete();

            _monitoringApi.Verify(x => x.JobDetails("1"), Times.Once);
            _storage.Verify(x => x.Expire(It.IsAny<ConsoleId>(), It.IsAny<TimeSpan>()), Times.Never);
            _storage.Verify(x => x.Dispose(), Times.Once);
        }

        [Fact]
        public void Complete_ExpiresSessionWhenParentDisappearsBetweenReads()
        {
            _storage.Setup(x => x.GetConsoleTtl(It.IsAny<ConsoleId>())).Returns(TimeSpan.FromSeconds(-37));
            _storage.Setup(x => x.GetState(It.IsAny<ConsoleId>())).Returns(new StateData { Name = "Deleted" });
            _monitoringApi.Setup(x => x.JobDetails("1")).Returns((JobDetailsDto)null);
            var context = new ConsoleContext(new ConsoleId("1", DateTime.UtcNow), _storage.Object, _options, _jobStorage.Object);

            context.Complete();

            _storage.Verify(x => x.Expire(It.IsAny<ConsoleId>(), TimeSpan.Zero), Times.Once);
            _storage.Verify(x => x.Dispose(), Times.Once);
        }

        [Theory]
        [InlineData(0, "Processing")]
        [InlineData(-1, "Processing")]
        [InlineData(-37, "processing")]
        [InlineData(60, "Deleted")]
        public void Complete_DoesNotReadMonitoringForProcessingOrPositiveTtl(double ttlSeconds, string stateName)
        {
            _storage.Setup(x => x.GetConsoleTtl(It.IsAny<ConsoleId>())).Returns(TimeSpan.FromSeconds(ttlSeconds));
            _storage.Setup(x => x.GetState(It.IsAny<ConsoleId>())).Returns(new StateData { Name = stateName });
            var context = new ConsoleContext(new ConsoleId("1", DateTime.UtcNow), _storage.Object, _options, _jobStorage.Object);

            context.Complete();

            _jobStorage.Verify(x => x.GetMonitoringApi(), Times.Never);
            _storage.Verify(x => x.Expire(It.IsAny<ConsoleId>(), It.IsAny<TimeSpan>()),
                ttlSeconds > 0 ? Times.Once() : Times.Never());
        }

        [Fact]
        public void Complete_WithoutJobStoragePreservesNonprocessingSession()
        {
            _storage.Setup(x => x.GetConsoleTtl(It.IsAny<ConsoleId>())).Returns(TimeSpan.FromSeconds(-37));
            _storage.Setup(x => x.GetState(It.IsAny<ConsoleId>())).Returns(new StateData { Name = "Deleted" });
            var context = new ConsoleContext(new ConsoleId("1", DateTime.UtcNow), _storage.Object, _options);

            context.Complete();

            _storage.Verify(x => x.Expire(It.IsAny<ConsoleId>(), It.IsAny<TimeSpan>()), Times.Never);
            _storage.Verify(x => x.Dispose(), Times.Once);
        }

        [Fact]
        public void Complete_ReadsParentExpirationOutsideTheAmbientTransaction()
        {
            _storage.Setup(x => x.GetConsoleTtl(It.IsAny<ConsoleId>())).Returns(TimeSpan.FromSeconds(-37));
            _storage.Setup(x => x.GetState(It.IsAny<ConsoleId>())).Returns(new StateData { Name = "CustomFinal" });
            _monitoringApi.Setup(x => x.JobDetails("1"))
                .Callback(() => Assert.Null(Transaction.Current))
                .Returns(new JobDetailsDto { ExpireAt = DateTime.UtcNow.AddMinutes(-1) });
            var context = new ConsoleContext(new ConsoleId("1", DateTime.UtcNow), _storage.Object, _options, _jobStorage.Object);

            using (var transaction = new TransactionScope())
            {
                var ambient = Transaction.Current;
                context.Complete();
                Assert.Same(ambient, Transaction.Current);
            }

            _storage.Verify(x => x.Expire(It.IsAny<ConsoleId>(), TimeSpan.Zero), Times.Once);
        }
    }
}
