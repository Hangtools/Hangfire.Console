using Hangfire.Console.Server;
using Hangfire.Console.States;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hangfire.Console.Tests
{
    public class GlobalConfigurationExtensionsFacts
    {
        [Fact]
        public async Task UseConsole_ConcurrentCallsRegisterFiltersOnce()
        {
            using (var ready = new CountdownEvent(16))
            using (var start = new ManualResetEventSlim())
            {
                var calls = Enumerable.Range(0, 16).Select(_ => Task.Factory.StartNew(() =>
                {
                    ready.Signal();
                    start.Wait();
                    return GlobalConfiguration.Configuration.UseConsole();
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

                try
                {
                    Assert.True(ready.Wait(10_000));
                }
                finally
                {
                    start.Set();
                }

                var configurations = await Task.WhenAll(calls);

                Assert.All(configurations, configuration => Assert.Same(GlobalConfiguration.Configuration, configuration));
                Assert.Single(GlobalJobFilters.Filters, x => x.Instance is ConsoleServerFilter);
                Assert.Single(GlobalJobFilters.Filters, x => x.Instance is ConsoleApplyStateFilter);
            }
        }
    }
}
