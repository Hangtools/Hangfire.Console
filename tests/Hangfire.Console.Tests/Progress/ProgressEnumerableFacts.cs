using Hangfire.Console.Progress;
using Hangfire.Console.Serialization;
using Hangfire.Console.Server;
using Hangfire.Console.Storage;
using Moq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Hangfire.Console.Tests.Progress
{
    public class ProgressEnumerableFacts
    {
        [Theory]
        [InlineData(true, 1_000_000, 101)]
        [InlineData(false, 1_000_000, 101)]
        [InlineData(true, 3, 4)]
        [InlineData(false, 3, 4)]
        [InlineData(true, 1, 2)]
        [InlineData(false, 1, 2)]
        [InlineData(true, 0, 2)]
        [InlineData(false, 0, 2)]
        public void WithProgress_BoundsStorageWrites(bool generic, int count, int expectedWrites)
        {
            var lines = new List<ConsoleLine>();
            var storage = new Mock<IConsoleStorage>();
            storage.Setup(x => x.AddLine(It.IsAny<ConsoleId>(), It.IsAny<ConsoleLine>()))
                .Callback<ConsoleId, ConsoleLine>((id, line) => lines.Add(line));
            var context = new ConsoleContext(new ConsoleId("1", DateTime.UtcNow), storage.Object,
                new ConsoleOptions { ProgressBarDecimalDigits = 3 });
            var bar = context.WriteProgressBar(null, 0, null);
            var visited = 0;

            foreach (var item in WithProgress(Enumerable.Range(0, count), bar, count, generic))
            {
                Assert.Equal(visited++, item);
            }

            Assert.Equal(count, visited);
            Assert.Equal(expectedWrites, lines.Count);
            Assert.Equal(0, lines[0].ProgressValue);
            Assert.Equal(100, lines[lines.Count - 1].ProgressValue);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void WithProgress_PreservesFractionalValuesForSmallCollections(bool generic)
        {
            var values = new List<double>();
            var bar = CreateProgressBar(values);

            foreach (var item in WithProgress(new[] { 1, 2, 3 }, bar, 3, generic))
            {
            }

            Assert.Equal(new[] { 0, 100.0 / 3, 200.0 / 3, 100 }, values);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void WithProgress_CompletesWhenLoopBreaks(bool generic)
        {
            var values = new List<double>();
            var bar = CreateProgressBar(values);

            foreach (var item in WithProgress(Enumerable.Range(0, 1_000), bar, 1_000, generic))
            {
                if (item == 5) break;
            }

            Assert.Equal(new[] { 0.0, 100.0 }, values);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void WithProgress_CompletesWhenLoopBodyThrows(bool generic)
        {
            var values = new List<double>();
            var bar = CreateProgressBar(values);

            Assert.Throws<InvalidOperationException>(() =>
            {
                foreach (var item in WithProgress(Enumerable.Range(0, 1_000), bar, 1_000, generic))
                {
                    if (item == 5) throw new InvalidOperationException();
                }
            });

            Assert.Equal(new[] { 0.0, 100.0 }, values);
        }

        [Theory]
        [InlineData(true, 0)]
        [InlineData(false, 0)]
        [InlineData(true, 2)]
        [InlineData(false, 2)]
        public void WithProgress_HandlesUnderestimatedCount(bool generic, int count)
        {
            var values = new List<double>();
            var bar = CreateProgressBar(values);
            var visited = 0;
            // Range implements ICollection<T> on newer runtimes, which overrides the supplied count.
            var items = Enumerable.Range(0, 1_000).Where(item => true);

            foreach (var item in WithProgress(items, bar, count, generic))
            {
                visited++;
            }

            Assert.Equal(1_000, visited);
            Assert.InRange(values.Count, 2, 101);
            Assert.Equal(50, values[1]);
            Assert.Equal(100, values[values.Count - 1]);
            Assert.All(values, value => Assert.InRange(value, 0, 100));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void WithProgress_ResetRestartsProgress(bool generic)
        {
            var values = new List<double>();
            var bar = CreateProgressBar(values);
            var items = new[] { 1, 2, 3 };
            var progress = generic
                ? (IEnumerable)items.WithProgress(bar)
                : ((IEnumerable)items).WithProgress(bar);
            var enumerator = progress.GetEnumerator();

            try
            {
                Assert.True(enumerator.MoveNext());
                Assert.True(enumerator.MoveNext());
                enumerator.Reset();
                Assert.True(enumerator.MoveNext());

                Assert.Equal(new[] { 0, 100.0 / 3, 0 }, values);
            }
            finally
            {
                ((IDisposable)enumerator).Dispose();
            }
        }

        private static IProgressBar CreateProgressBar(List<double> values)
        {
            var bar = new Mock<IProgressBar>();
            bar.Setup(x => x.SetValue(It.IsAny<double>())).Callback<double>(values.Add);
            bar.Setup(x => x.SetValue(It.IsAny<int>())).Callback<int>(value => values.Add(value));
            return bar.Object;
        }

        private static IEnumerable<int> WithProgress(IEnumerable<int> items, IProgressBar bar, int count, bool generic)
        {
            return generic
                ? items.WithProgress(bar, count)
                : ((IEnumerable)items).WithProgress(bar, count).Cast<int>();
        }
    }
}
