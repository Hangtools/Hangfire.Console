using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Hangfire.Dashboard;

namespace Hangfire.Console.Tests.Stubs
{
    class DashboardResponseStub : DashboardResponse
    {
        public override string ContentType { get; set; }
        public override int StatusCode { get; set; }
        public override Stream Body { get; }

        public DashboardResponseStub(Stream responseStream)
        {
            Body = responseStream;
        }

        public override void SetExpire(DateTimeOffset? value)
        {
            throw new NotImplementedException();
        }

        public override Task WriteAsync(string text)
        {
            return Body.WriteAsync(Encoding.UTF8.GetBytes(text), 0, text.Length);
        }
    }
}