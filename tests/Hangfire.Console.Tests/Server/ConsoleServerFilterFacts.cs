using Hangfire.Common;
using Hangfire.Console.Server;
using Hangfire.Server;
using Hangfire.States;
using Hangfire.Storage;
using Moq;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Threading;
using Xunit;

namespace Hangfire.Console.Tests.Server
{
    public class ConsoleServerFilterFacts
    {
        private readonly Mock<IServerFilter> _otherFilter;
        private readonly Mock<IJobCancellationToken> _cancellationToken;
        private readonly Mock<JobStorage> _storage;
        private readonly Mock<JobStorageConnection> _connection;
        private readonly Mock<JobStorageConnection> _consoleConnection;
        private readonly Mock<JobStorageTransaction> _transaction;

        public ConsoleServerFilterFacts()
        {
            _otherFilter = new Mock<IServerFilter>();
            _cancellationToken = new Mock<IJobCancellationToken>();
            _storage = new Mock<JobStorage>();
            _connection = new Mock<JobStorageConnection>();
            _consoleConnection = new Mock<JobStorageConnection>();
            _transaction = new Mock<JobStorageTransaction>();

            _storage.Setup(x => x.GetConnection())
                .Returns(_consoleConnection.Object);
            _consoleConnection.Setup(x => x.CreateWriteTransaction())
                .Returns(_transaction.Object);
            _consoleConnection.Setup(x => x.GetStateData("1"))
                .Returns(CreateState(ProcessingState.StateName));
            _connection.Setup(x => x.CreateWriteTransaction())
                .Returns(_transaction.Object);
        }

        [Fact]
        public void DoesNotCreateConsoleContext_IfStateNotFound()
        {
            _connection.Setup(x => x.GetStateData("1"))
                .Returns((StateData)null);

            var performer = new BackgroundJobPerformer(CreateJobFilterProvider());
            var context = CreatePerformContext();

            performer.Perform(context);

            var consoleContext = ConsoleContext.FromPerformContext(context);
            Assert.Null(consoleContext);
        }

        [Fact]
        public void DoesNotCreateConsoleContext_IfStateIsNotProcessing()
        {
            _connection.Setup(x => x.GetStateData("1"))
                .Returns(CreateState(SucceededState.StateName));

            var performer = new BackgroundJobPerformer(CreateJobFilterProvider());
            var context = CreatePerformContext();

            performer.Perform(context);

            var consoleContext = ConsoleContext.FromPerformContext(context);
            Assert.Null(consoleContext);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void RemovedConsoleContext_StillDisposesItsConnection_WithoutExpiringData(bool followJobRetention)
        {
            ConsoleContext removedConsole = null;
            _connection.Setup(x => x.GetStateData("1"))
                .Returns(CreateState(ProcessingState.StateName));
            _otherFilter.Setup(x => x.OnPerforming(It.IsAny<PerformingContext>()))
                .Callback<PerformingContext>(x =>
                {
                    removedConsole = ConsoleContext.FromPerformContext(x);
                    x.Items.Remove("ConsoleContext");
                });

            var performer = new BackgroundJobPerformer(CreateJobFilterProvider(followJobRetention));
            var context = CreatePerformContext();

            performer.Perform(context);

            var consoleContext = ConsoleContext.FromPerformContext(context);
            Assert.Null(consoleContext);
            Assert.NotNull(removedConsole);
            removedConsole.WriteLine("Late callback", null);

            _transaction.Verify(x => x.Commit(), Times.Never);
            _consoleConnection.Verify(x => x.GetHashTtl(It.IsAny<string>()), Times.Never);
            _consoleConnection.Verify(x => x.Dispose(), Times.Once);
            _connection.Verify(x => x.Dispose(), Times.Never);
            Assert.Single(context.Items);
        }

        [Fact]
        public void CreatesConsoleContext_IfStateIsProcessing_FixesExpiration_IfFollowsJobRetention()
        {
            _connection.Setup(x => x.GetStateData("1"))
                .Returns(CreateState(ProcessingState.StateName));
            _consoleConnection.Setup(x => x.GetHashTtl(It.IsAny<string>()))
                .Returns(TimeSpan.FromSeconds(1));

            var performer = new BackgroundJobPerformer(CreateJobFilterProvider(true));
            var context = CreatePerformContext();

            performer.Perform(context);

            var consoleContext = ConsoleContext.FromPerformContext(context);
            Assert.Null(consoleContext);

            _consoleConnection.Verify(x => x.GetHashTtl(It.IsAny<string>()));

            _transaction.Verify(x => x.ExpireSet(It.IsAny<string>(), It.IsAny<TimeSpan>()));
            _transaction.Verify(x => x.ExpireHash(It.IsAny<string>(), It.IsAny<TimeSpan>()));

            _transaction.Verify(x => x.Commit());
        }

        [Fact]
        public void CreatesConsoleContext_IfStateIsProcessing_DoesNotFixExpiration_IfNegativeTtl_AndFollowsJobRetention()
        {
            _connection.Setup(x => x.GetStateData("1"))
                .Returns(CreateState(ProcessingState.StateName));
            _consoleConnection.Setup(x => x.GetHashTtl(It.IsAny<string>()))
                .Returns(TimeSpan.FromSeconds(-1));

            var performer = new BackgroundJobPerformer(CreateJobFilterProvider(true));
            var context = CreatePerformContext();

            performer.Perform(context);

            var consoleContext = ConsoleContext.FromPerformContext(context);
            Assert.Null(consoleContext);

            _consoleConnection.Verify(x => x.GetHashTtl(It.IsAny<string>()));

            _transaction.Verify(x => x.Commit(), Times.Never);
        }

        [Fact]
        public void CreatesConsoleContext_IfStateIsProcessing_DoesNotFixExpiration_IfZeroTtl_AndFollowsJobRetention()
        {
            _connection.Setup(x => x.GetStateData("1"))
                .Returns(CreateState(ProcessingState.StateName));
            _consoleConnection.Setup(x => x.GetHashTtl(It.IsAny<string>()))
                .Returns(TimeSpan.Zero);

            var performer = new BackgroundJobPerformer(CreateJobFilterProvider(true));
            var context = CreatePerformContext();

            performer.Perform(context);

            var consoleContext = ConsoleContext.FromPerformContext(context);
            Assert.Null(consoleContext);

            _consoleConnection.Verify(x => x.GetHashTtl(It.IsAny<string>()));

            _transaction.Verify(x => x.Commit(), Times.Never);
        }

        [Fact]
        public void CreatesConsoleContext_IfStateIsProcessing_ExpiresData_IfNotFollowsJobRetention()
        {
            _connection.Setup(x => x.GetStateData("1"))
                .Returns(CreateState(ProcessingState.StateName));

            var performer = new BackgroundJobPerformer(CreateJobFilterProvider());
            var context = CreatePerformContext();

            performer.Perform(context);

            var consoleContext = ConsoleContext.FromPerformContext(context);
            Assert.Null(consoleContext);

            _consoleConnection.Verify(x => x.GetHashTtl(It.IsAny<string>()), Times.Never);

            _transaction.Verify(x => x.ExpireSet(It.IsAny<string>(), It.IsAny<TimeSpan>()));
            _transaction.Verify(x => x.ExpireHash(It.IsAny<string>(), It.IsAny<TimeSpan>()));

            _transaction.Verify(x => x.Commit());
        }

        [Fact]
        public void ConsoleOwnsAndDisposesItsConnection_WithoutDisposingJobConnection()
        {
            _connection.Setup(x => x.GetStateData("1"))
                .Returns(CreateState(ProcessingState.StateName));
            var performer = new BackgroundJobPerformer(CreateJobFilterProvider());

            performer.Perform(CreatePerformContext());

            _storage.Verify(x => x.GetConnection(), Times.Once);
            _consoleConnection.Verify(x => x.Dispose(), Times.Once);
            _connection.Verify(x => x.CreateWriteTransaction(), Times.Never);
            _connection.Verify(x => x.Dispose(), Times.Never);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void MissingStorage_UsesBorrowedJobConnectionWithoutDisposingIt(bool followJobRetention)
        {
            _connection.Setup(x => x.GetStateData("1"))
                .Returns(CreateState(ProcessingState.StateName));
            ConsoleContext console = null;
            _otherFilter.Setup(x => x.OnPerforming(It.IsAny<PerformingContext>()))
                .Callback<PerformingContext>(x =>
                {
                    console = ConsoleContext.FromPerformContext(x);
                    console.WriteLine("Message from a custom performer", null);
                });
            var context = CreatePerformContext(withStorage: false);

            new BackgroundJobPerformer(CreateJobFilterProvider(followJobRetention)).Perform(context);

            Assert.NotNull(console);
            Assert.Null(ConsoleContext.FromPerformContext(context));
            console.WriteLine("Late callback", null);
            _storage.Verify(x => x.GetConnection(), Times.Never);
            _connection.Verify(x => x.CreateWriteTransaction(), Times.AtLeastOnce);
            _connection.Verify(x => x.Dispose(), Times.Never);
            _consoleConnection.Verify(x => x.Dispose(), Times.Never);
            _transaction.Verify(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()), Times.Never);
        }

        [Fact]
        public void MissingStorage_InitializationFailureDoesNotDisposeBorrowedConnection()
        {
            _connection.Setup(x => x.GetStateData("1"))
                .Returns(CreateState(ProcessingState.StateName));
            _transaction.Setup(x => x.Commit()).Throws(new InvalidOperationException("Storage unavailable"));
            var filter = new ConsoleServerFilter(new ConsoleOptions());

            Assert.Throws<InvalidOperationException>(() => filter.OnPerforming(
                new PerformingContext(CreatePerformContext(withStorage: false))));

            _storage.Verify(x => x.GetConnection(), Times.Never);
            _connection.Verify(x => x.Dispose(), Times.Never);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void OtherFilterAbort_DoesNotLeakConsoleConnection(bool cancel)
        {
            _connection.Setup(x => x.GetStateData("1"))
                .Returns(CreateState(ProcessingState.StateName));
            if (cancel)
                _otherFilter.Setup(x => x.OnPerforming(It.IsAny<PerformingContext>()))
                    .Callback<PerformingContext>(x => x.Canceled = true);
            else
                _otherFilter.Setup(x => x.OnPerforming(It.IsAny<PerformingContext>()))
                    .Throws(new InvalidOperationException("Another filter failed"));
            var performer = new BackgroundJobPerformer(CreateJobFilterProvider());
            var context = CreatePerformContext();

            if (cancel) performer.Perform(context);
            else Assert.IsType<InvalidOperationException>(Assert.Throws<JobPerformanceException>(
                () => performer.Perform(context)).InnerException);

            Assert.DoesNotContain(context.Items.Values, item => item is ConsoleContext);
            _consoleConnection.Verify(x => x.Dispose(), Times.Once);
            _connection.Verify(x => x.Dispose(), Times.Never);
        }

        [Fact]
        public void JobFailure_ClosesConsoleAndPreservesTheJobException()
        {
            _connection.Setup(x => x.GetStateData("1"))
                .Returns(CreateState(ProcessingState.StateName));
            var context = CreatePerformContext();
            var failure = new InvalidOperationException("Job failed");
            context.Items["failure"] = failure;

            var exception = Assert.Throws<JobPerformanceException>(() =>
                new BackgroundJobPerformer(CreateJobFilterProvider()).Perform(context));

            Assert.Same(failure, exception.InnerException);
            Assert.DoesNotContain(context.Items.Values, item => item is ConsoleContext);
            _consoleConnection.Verify(x => x.Dispose(), Times.Once);
            _connection.Verify(x => x.Dispose(), Times.Never);
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public void JobCancellation_ClosesConsoleAndPreservesCancellation(bool withStorage, bool shutdown)
        {
            _connection.Setup(x => x.GetStateData("1"))
                .Returns(CreateState(ProcessingState.StateName));
            _cancellationToken.SetupGet(x => x.ShutdownToken)
                .Returns(new CancellationToken(shutdown));
            var context = CreatePerformContext(withStorage);
            Exception cancellation = shutdown ? (Exception)new OperationCanceledException() : new JobAbortedException();
            context.Items["failure"] = cancellation;

            var exception = Record.Exception(() =>
                new BackgroundJobPerformer(CreateJobFilterProvider()).Perform(context));

            Assert.Same(cancellation, exception);
            Assert.DoesNotContain(context.Items.Values, item => item is ConsoleContext);
            _consoleConnection.Verify(x => x.Dispose(), withStorage ? Times.Once() : Times.Never());
            _connection.Verify(x => x.Dispose(), Times.Never);
        }

        [Fact]
        public void RetentionFailure_ClosesConsoleAndRemovesItsContext()
        {
            _connection.Setup(x => x.GetStateData("1"))
                .Returns(CreateState(ProcessingState.StateName));
            _transaction.Setup(x => x.ExpireSet(It.IsAny<string>(), It.IsAny<TimeSpan>()))
                .Throws(new InvalidOperationException("Expiration failed"));
            var context = CreatePerformContext();

            Assert.Throws<JobPerformanceException>(() =>
                new BackgroundJobPerformer(CreateJobFilterProvider()).Perform(context));

            Assert.DoesNotContain(context.Items.Values, item => item is ConsoleContext);
            _consoleConnection.Verify(x => x.Dispose(), Times.Once);
            _connection.Verify(x => x.Dispose(), Times.Never);
        }

        [Fact]
        public void InitializationFailureDisposesConsoleConnection()
        {
            _connection.Setup(x => x.GetStateData("1"))
                .Returns(CreateState(ProcessingState.StateName));
            _transaction.Setup(x => x.Commit()).Throws(new InvalidOperationException("Storage unavailable"));
            var filter = new ConsoleServerFilter(new ConsoleOptions());

            Assert.Throws<InvalidOperationException>(() => filter.OnPerforming(new PerformingContext(CreatePerformContext())));

            _consoleConnection.Verify(x => x.Dispose(), Times.Once);
            _connection.Verify(x => x.Dispose(), Times.Never);
        }

        [Fact]
        public void UnsupportedConsoleConnectionIsDisposed()
        {
            _connection.Setup(x => x.GetStateData("1"))
                .Returns(CreateState(ProcessingState.StateName));
            var connection = new Mock<IStorageConnection>();
            _storage.Setup(x => x.GetConnection()).Returns(connection.Object);
            var filter = new ConsoleServerFilter(new ConsoleOptions());

            Assert.Throws<NotSupportedException>(() => filter.OnPerforming(new PerformingContext(CreatePerformContext())));

            connection.Verify(x => x.Dispose(), Times.Once);
        }

        [SuppressMessage("Usage", "xUnit1013", Justification = "Hangfire job methods must be public for Job.FromExpression")]
        public static void JobMethod(PerformContext context)
        {
            // reset transaction method calls after OnPerforming is completed
            var @this = (ConsoleServerFilterFacts)context.Items["this"];
            @this._transaction.Invocations.Clear();
            if (context.Items.TryGetValue("failure", out var failure)) throw (Exception)failure;
        }

        private IJobFilterProvider CreateJobFilterProvider(bool followJobRetention = false)
        {
            var filters = new JobFilterCollection();
            filters.Add(new ConsoleServerFilter(new ConsoleOptions() { FollowJobRetentionPolicy = followJobRetention }));
            filters.Add(_otherFilter.Object);
            return new JobFilterProviderCollection(filters);
        }

        private PerformContext CreatePerformContext(bool withStorage = true)
        {
            var context = new PerformContext(withStorage ? _storage.Object : null,
                _connection.Object,
                new BackgroundJob("1", Job.FromExpression(() => JobMethod(null)), DateTime.UtcNow),
                _cancellationToken.Object);
            context.Items["this"] = this;
            return context;
        }

        private StateData CreateState(string stateName)
        {
            return new StateData()
            {
                Name = stateName,
                Data = new Dictionary<string, string>()
                {
                    ["StartedAt"] = JobHelper.SerializeDateTime(DateTime.UtcNow)
                }
            };
        }

    }
}
