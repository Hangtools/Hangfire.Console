using Hangfire.Dashboard;
using System;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;

namespace Hangfire.Console.Dashboard
{
    /// <summary>
    /// Dispatcher for configured script
    /// </summary>
    internal class DynamicJsDispatcher : IDashboardDispatcher
    {
        private readonly ConsoleOptions _options;

        public DynamicJsDispatcher(ConsoleOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public Task Dispatch(DashboardContext context)
        {
            var builder = new StringBuilder();
            var urlHelper = new UrlHelper(context);

            builder.Append(@"(function (hangfire) {")
                .Append("hangfire.config = hangfire.config || {};")
                .AppendFormat(CultureInfo.InvariantCulture, "hangfire.config.consolePollInterval = {0};", _options.PollInterval)
                .AppendFormat("hangfire.config.consolePollUrl = '{0}';", urlHelper.To("/console/"))
                .Append("})(window.Hangfire = window.Hangfire || {});")
                .AppendLine();

            return context.Response.WriteAsync(builder.ToString());
        }
    }
}