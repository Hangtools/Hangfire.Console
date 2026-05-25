using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hangfire.Console.Dashboard;
using Hangfire.Console.Tests.Stubs;
using Hangfire.Dashboard;
using Moq;
using Xunit;

namespace Hangfire.Console.Tests.Dashboard
{
    public class DynamicJsDispatcherFacts
    {
        private readonly Mock<DashboardRequest> _request;
        private readonly DashboardResponse _response;
        private readonly MemoryStream _responseStream;

        public DynamicJsDispatcherFacts()
        {
            _request = new Mock<DashboardRequest>();
            _responseStream = new MemoryStream();
            _response = new DashboardResponseStub(_responseStream);
        }

        [Fact]
        public void Ctor_ThrowsException_IfOptionsIsNull()
        {
            Assert.Throws<ArgumentNullException>("options", () => new DynamicJsDispatcher(null));
        }

        [Fact]
        public void Ctor_DoesNotThrow_IfOptionsIsProvided()
        {
            var options = new ConsoleOptions();
            var dispatcher = new DynamicJsDispatcher(options);

            Assert.NotNull(dispatcher);
        }

        [Fact]
        public async Task Dispatch_WritesScript_WithDefaultPollInterval()
        {
            var options = new ConsoleOptions();
            var dispatcher = new DynamicJsDispatcher(options);
            var context = CreateContext("/hangfire");

            await dispatcher.Dispatch(context);

            var result = GetWrittenContent();

            Assert.Contains("(function (hangfire) {", result);
            Assert.Contains("hangfire.config = hangfire.config || {};", result);
            Assert.Contains($"hangfire.config.consolePollInterval = {options.PollInterval};", result);
            Assert.Contains("hangfire.config.consolePollUrl = '/hangfire/console/';", result);
            Assert.Contains("})(window.Hangfire = window.Hangfire || {});", result);
        }

        [Fact]
        public async Task Dispatch_WritesScript_WithCustomPollInterval()
        {
            var options = new ConsoleOptions { PollInterval = 5000 };
            var dispatcher = new DynamicJsDispatcher(options);
            var context = CreateContext("/hangfire");

            await dispatcher.Dispatch(context);

            var result = GetWrittenContent();

            Assert.Contains("hangfire.config.consolePollInterval = 5000;", result);
        }

        [Fact]
        public async Task Dispatch_UsesInvariantCulture_ForPollInterval()
        {
            // Switch current culture to one that uses comma as decimal separator
            var originalCulture = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("pt-BR");

                var options = new ConsoleOptions { PollInterval = 1234 };
                var dispatcher = new DynamicJsDispatcher(options);
                var context = CreateContext("/hangfire");

                await dispatcher.Dispatch(context);

                var result = GetWrittenContent();

                // Number should be formatted using invariant culture (no thousands separator)
                Assert.Contains("hangfire.config.consolePollInterval = 1234;", result);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = originalCulture;
            }
        }

        [Fact]
        public async Task Dispatch_UsesEmptyPathBase_WhenRequestPathBaseIsEmpty()
        {
            var options = new ConsoleOptions();
            var dispatcher = new DynamicJsDispatcher(options);
            var context = CreateContext("");

            await dispatcher.Dispatch(context);

            var result = GetWrittenContent();

            Assert.Contains("hangfire.config.consolePollUrl = '/console/';", result);
        }

        [Fact]
        public async Task Dispatch_UsesProvidedPathBase()
        {
            var options = new ConsoleOptions();
            var dispatcher = new DynamicJsDispatcher(options);
            var context = CreateContext("/custom/path");

            await dispatcher.Dispatch(context);

            var result = GetWrittenContent();

            Assert.Contains("hangfire.config.consolePollUrl = '/custom/path/console/';", result);
        }

        [Fact]
        public async Task Dispatch_UsesProvidedPrefixPath()
        {
            var options = new ConsoleOptions();
            var dispatcher = new DynamicJsDispatcher(options);
            var context = CreateContext("/custom/path", "/api");

            await dispatcher.Dispatch(context);

            var result = GetWrittenContent();

            Assert.Contains("hangfire.config.consolePollUrl = '/api/custom/path/console/';", result);
        }

        [Fact]
        public async Task Dispatch_AppendsTrailingNewLine()
        {
            var options = new ConsoleOptions();
            var dispatcher = new DynamicJsDispatcher(options);
            var context = CreateContext("/hangfire");

            await dispatcher.Dispatch(context);

            var result = GetWrittenContent();

            Assert.EndsWith(Environment.NewLine, result);
        }

        [Fact]
        public async Task Dispatch_WritesOnce_ToResponse()
        {
            var options = new ConsoleOptions();
            var dispatcher = new DynamicJsDispatcher(options);
            var context = CreateContext("/hangfire");

            await dispatcher.Dispatch(context);

            Assert.True(_response.Body.Length > 0);
        }

        [Fact]
        public async Task Dispatch_ProducesScript_InExpectedOrder()
        {
            var options = new ConsoleOptions { PollInterval = 2000 };
            var dispatcher = new DynamicJsDispatcher(options);
            var context = CreateContext("/hangfire");

            await dispatcher.Dispatch(context);

            var result = GetWrittenContent();

            var openIdx = result.IndexOf("(function (hangfire) {", StringComparison.Ordinal);
            var configInitIdx = result.IndexOf("hangfire.config = hangfire.config || {};", StringComparison.Ordinal);
            var pollIntervalIdx = result.IndexOf("hangfire.config.consolePollInterval", StringComparison.Ordinal);
            var pollUrlIdx = result.IndexOf("hangfire.config.consolePollUrl", StringComparison.Ordinal);
            var closeIdx = result.IndexOf("})(window.Hangfire = window.Hangfire || {});", StringComparison.Ordinal);

            Assert.True(openIdx >= 0);
            Assert.True(openIdx < configInitIdx);
            Assert.True(configInitIdx < pollIntervalIdx);
            Assert.True(pollIntervalIdx < pollUrlIdx);
            Assert.True(pollUrlIdx < closeIdx);
        }

        private DashboardContext CreateContext(string pathBase, string prefixPath = null)
        {
            _request.Setup(x => x.PathBase).Returns(pathBase);

            var options = new DashboardOptions
            {
                IsReadOnlyFunc = _ => true,
                PrefixPath = prefixPath ?? string.Empty,
            };
            var context = new DashboardContextStub(options, _request.Object, _response);

            return context;
        }

        private string GetWrittenContent()
        {
            return Encoding.UTF8.GetString(_responseStream.ToArray());
        }
    }
}