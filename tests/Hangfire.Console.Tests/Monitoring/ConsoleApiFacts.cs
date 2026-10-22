using Hangfire.Common;
using Hangfire.Console.Monitoring;
using Hangfire.Console.Serialization;
using Hangfire.Console.Storage;
using Hangfire.Storage;
using Moq;
using System;
using System.Linq;
using Xunit;

namespace Hangfire.Console.Tests.Monitoring
{
    public class ConsoleApiFacts
    {
        [Theory]
        [InlineData(null, "#ff0000")]
        [InlineData("#0000ff", "#0000ff")]
        public void GetLines_PreservesProgressColorUntilExplicitlyChanged(string nextColor, string expectedColor)
        {
            var timestamp = DateTime.UtcNow;
            var consoleId = new ConsoleId("1", timestamp);
            var lines = new[]
            {
                new ConsoleLine { Message = "1", ProgressValue = 0, ProgressName = "Import", TextColor = "#ff0000" },
                new ConsoleLine { Message = "1", ProgressValue = 50, TimeOffset = 1, TextColor = nextColor },
                new ConsoleLine { Message = "1", ProgressValue = 100, TimeOffset = 2 }
            };
            var connection = new Mock<JobStorageConnection>();
            connection.Setup(x => x.GetSetCount(consoleId.GetSetKey())).Returns(lines.Length);
            connection.Setup(x => x.GetRangeFromSet(consoleId.GetSetKey(), 0, lines.Length))
                .Returns(lines.Select(x => SerializationHelper.Serialize(x)).ToList());

            using (var api = new ConsoleApi(connection.Object))
            {
                var progress = Assert.IsType<ProgressBarDto>(Assert.Single(api.GetLines("1", timestamp)));

                Assert.Equal("Import", progress.Name);
                Assert.Equal(100, progress.Progress);
                Assert.Equal(expectedColor, progress.Color);
            }
        }
    }
}
