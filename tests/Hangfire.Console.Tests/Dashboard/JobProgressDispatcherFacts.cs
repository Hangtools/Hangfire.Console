using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Hangfire.Common;
using Hangfire.Console.Dashboard;
using Hangfire.Console.Tests.Stubs;
using Hangfire.Dashboard;
using Hangfire.States;
using Hangfire.Storage;
using Moq;
using Xunit;

namespace Hangfire.Console.Tests.Dashboard
{
    public class JobProgressDispatcherFacts
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Dispatch_DisposesConnectionOnce_WhenReadingProgressSucceedsOrFails(bool failRead)
        {
            var connection = new Mock<JobStorageConnection>();
            var state = connection.Setup(x => x.GetStateData("1"));
            if (failRead)
            {
                state.Throws(new InvalidOperationException("Storage unavailable"));
            }
            else
            {
                state.Returns(new StateData
                {
                    Name = ProcessingState.StateName,
                    Data = new Dictionary<string, string>
                    {
                        ["StartedAt"] = JobHelper.SerializeDateTime(DateTime.UtcNow)
                    }
                });
            }
            connection.Setup(x => x.GetValueFromHash(It.IsAny<string>(), "progress")).Returns("42");
            var storage = new Mock<JobStorage>();
            storage.Setup(x => x.GetConnection()).Returns(connection.Object);
            var request = new Mock<DashboardRequest>();
            request.SetupGet(x => x.Method).Returns("POST");
            request.Setup(x => x.GetFormValuesAsync("jobs[]")).ReturnsAsync(new List<string> { "1" });

            using (var output = new MemoryStream())
            {
                var response = new DashboardResponseStub(output);
                var context = new DashboardContextStub(new DashboardOptions(), request.Object, response, storage.Object);
                var dispatcher = new JobProgressDispatcher(new ConsoleOptions());

                if (failRead)
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.Dispatch(context));
                }
                else
                {
                    await dispatcher.Dispatch(context);
                    Assert.Equal("{\"1\":42.0}", Encoding.UTF8.GetString(output.ToArray()));
                }

                connection.Verify(x => x.Dispose(), Times.Once);
            }
        }

        [Fact]
        public void Serialize_PreservesDictionaryKeyCase()
        {
            var result = new Dictionary<string, double>
            {
                ["AAA"] = 1.0,
                ["Bbb"] = 2.0,
                ["ccc"] = 3.0
            };

            var json = SerializationHelper.Serialize(result);

            Assert.Equal("{\"AAA\":1.0,\"Bbb\":2.0,\"ccc\":3.0}", json);
        }
    }
}
