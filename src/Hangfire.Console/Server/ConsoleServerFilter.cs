using Hangfire.Common;
using Hangfire.Console.Serialization;
using Hangfire.Console.Storage;
using Hangfire.Server;
using Hangfire.States;
using System;

namespace Hangfire.Console.Server
{
    /// <summary>
    /// Server filter to initialize and cleanup console environment.
    /// </summary>
    internal class ConsoleServerFilter : IServerFilter
    {
        private const string OwnedConsoleContextKey = "Hangfire.Console.Server.ConsoleServerFilter.OwnedContext";
        private readonly ConsoleOptions _options;

        public ConsoleServerFilter(ConsoleOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public void OnPerforming(PerformingContext filterContext)
        {
            var state = filterContext.Connection.GetStateData(filterContext.BackgroundJob.Id);

            if (state == null)
            {
                // State for job not found?
                return;
            }

            if (!string.Equals(state.Name, ProcessingState.StateName, StringComparison.OrdinalIgnoreCase))
            {
                // Not in Processing state? Something is really off...
                return;
            }
            
            var startedAt = JobHelper.DeserializeDateTime(state.Data["StartedAt"]);

            using (ConsoleContext.SuppressAmbientTransaction())
            {
                var connection = filterContext.Storage.GetConnection();
                try
                {
                    var consoleContext = new ConsoleContext(
                        new ConsoleId(filterContext.BackgroundJob.Id, startedAt), new ConsoleStorage(connection), _options);
                    filterContext.Items[OwnedConsoleContextKey] = consoleContext;
                    filterContext.Items["ConsoleContext"] = consoleContext;
                }
                catch
                {
                    connection.Dispose();
                    throw;
                }
            }
        }

        public void OnPerformed(PerformedContext filterContext)
        {
            try
            {
                // Keep ownership independent of the entry exposed to other filters.
                if (filterContext.Items.TryGetValue(OwnedConsoleContextKey, out var ownedContext))
                {
                    var applyRetention = filterContext.Items.TryGetValue("ConsoleContext", out var activeContext) &&
                        ReferenceEquals(ownedContext, activeContext);
                    ((ConsoleContext)ownedContext).Complete(applyRetention);
                }
            }
            finally
            {
                filterContext.Items.Remove("ConsoleContext");
                filterContext.Items.Remove(OwnedConsoleContextKey);
            }
        }
    }
}
