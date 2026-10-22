using Hangfire.Console.Progress;
using Hangfire.Console.Serialization;
using Hangfire.Console.Storage;
using Hangfire.Server;
using Hangfire.States;
using System;
using System.Globalization;
using System.Threading;

namespace Hangfire.Console.Server
{
    internal class ConsoleContext
    {
        private readonly ConsoleId _consoleId;
        private readonly IConsoleStorage _storage;
        private readonly ConsoleOptions _options;
        private readonly JobStorage _jobStorage;
        private double _lastTimeOffset;
        private int _nextProgressBarId;
        private bool _completed;

        public static ConsoleContext FromPerformContext(PerformContext context)
        {
            if (context == null)
            {
                // PerformContext might be null because of refactoring, or during tests
                return null;
            }

            if (!context.Items.TryGetValue("ConsoleContext", out var consoleContext))
            {
                // Absence of ConsoleContext means ConsoleServerFilter was not properly added
                return null;
            }

            return (ConsoleContext)consoleContext;
        }

        public ConsoleContext(ConsoleId consoleId, IConsoleStorage storage, ConsoleOptions options, JobStorage jobStorage = null)
        {
            _consoleId = consoleId ?? throw new ArgumentNullException(nameof(consoleId));
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _jobStorage = jobStorage;

            _lastTimeOffset = 0;
            _nextProgressBarId = 0;

            using (SuppressAmbientTransaction())
            {
                _storage.InitConsole(_consoleId);
            }
        }

        public ConsoleTextColor TextColor { get; set; }
        
        public void AddLine(ConsoleLine line)
        {
            if (line == null)
                throw new ArgumentNullException(nameof(line));

            lock (this)
            {
                if (_completed) return;

                line.TimeOffset = Math.Round((DateTime.UtcNow - _consoleId.DateValue).TotalSeconds, 3);

                if (_lastTimeOffset >= line.TimeOffset)
                {
                    // prevent duplicate lines collapsing
                    line.TimeOffset = _lastTimeOffset + 0.0001;
                }

                _lastTimeOffset = line.TimeOffset;

                using (SuppressAmbientTransaction())
                {
                    _storage.AddLine(_consoleId, line);
                }
            }
        }
        
        public void WriteLine(string value, ConsoleTextColor color)
        {
            AddLine(new ConsoleLine() { Message = value ?? "", TextColor = color ?? TextColor });
        }

        public IProgressBar WriteProgressBar(string name, double value, ConsoleTextColor color)
        {
            var progressBarId = Interlocked.Increment(ref _nextProgressBarId).ToString(CultureInfo.InvariantCulture);

            var progressBar = new DefaultProgressBar(this, progressBarId, _options.ProgressBarDecimalDigits, name, color);

            // set initial value
            progressBar.SetValue(value);

            return progressBar;
        }
        
        public void Expire(TimeSpan expireIn)
        {
            using (SuppressAmbientTransaction())
            {
                _storage.Expire(_consoleId, expireIn);
            }
        }

        public void FixExpiration()
        {
            using (SuppressAmbientTransaction())
            {
                var ttl = _storage.GetConsoleTtl(_consoleId);
                if (ttl <= TimeSpan.Zero)
                {
                    var state = _storage.GetState(_consoleId);
                    if (state == null)
                    {
                        // A deleted job can keep running and recreate keys after its parent expires.
                        ttl = TimeSpan.Zero;
                    }
                    else
                    {
                        if (_jobStorage == null || string.Equals(state.Name, ProcessingState.StateName, StringComparison.OrdinalIgnoreCase))
                            return;

                        // SQL cleanup can leave an expired parent visible. Its expiration distinguishes
                        // that case from persistent console keys without relying on provider TTL sentinels.
                        var job = _jobStorage.GetMonitoringApi().JobDetails(_consoleId.JobId);
                        if (job == null)
                        {
                            ttl = TimeSpan.Zero;
                        }
                        else
                        {
                            if (!job.ExpireAt.HasValue) return;
                            ttl = job.ExpireAt.Value - DateTime.UtcNow;
                            if (ttl < TimeSpan.Zero) ttl = TimeSpan.Zero;
                        }
                    }
                }

                _storage.Expire(_consoleId, ttl);
            }
        }

        public void Complete(bool applyRetention = true)
        {
            lock (this)
            {
                if (_completed) return;
                _completed = true;

                try
                {
                    if (applyRetention)
                    {
                        if (_options.FollowJobRetentionPolicy) FixExpiration();
                        else Expire(_options.ExpireIn);
                    }
                }
                finally
                {
                    _storage.Dispose();
                }
            }
        }

        internal static IDisposable SuppressAmbientTransaction()
        {
#if NET451 || NETSTANDARD2_0
            return new System.Transactions.TransactionScope(System.Transactions.TransactionScopeOption.Suppress);
#else
            // .NET Standard 1.3 does not expose TransactionScope.
            return null;
#endif
        }
    }
}
