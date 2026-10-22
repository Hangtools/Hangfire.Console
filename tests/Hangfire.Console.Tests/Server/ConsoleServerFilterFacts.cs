using Hangfire.Common;
using Hangfire.Console.Server;
using Hangfire.Server;
using Hangfire.States;
using Hangfire.Storage;
using Moq;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
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
        }

        private IJobFilterProvider CreateJobFilterProvider(bool followJobRetention = false)
        {
            var filters = new JobFilterCollection();
            filters.Add(new ConsoleServerFilter(new ConsoleOptions() { FollowJobRetentionPolicy = followJobRetention }));
            filters.Add(_otherFilter.Object);
            return new JobFilterProviderCollection(filters);
        }

        private PerformContext CreatePerformContext()
        {
            var context = new PerformContext(_storage.Object,
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
