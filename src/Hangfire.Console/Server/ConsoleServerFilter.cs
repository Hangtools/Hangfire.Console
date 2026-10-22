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
                    filterContext.Items["ConsoleContext"] = new ConsoleContext(
                        new ConsoleId(filterContext.BackgroundJob.Id, startedAt), new ConsoleStorage(connection), _options);
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
                // Stop late writes before fixing retention and releasing the console-owned connection.
                ConsoleContext.FromPerformContext(filterContext)?.Complete();
            }
            finally
            {
                filterContext.Items.Remove("ConsoleContext");
            }
        }
    }
}
