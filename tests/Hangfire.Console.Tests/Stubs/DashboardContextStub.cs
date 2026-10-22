using System.IO;
using Hangfire.Dashboard;

namespace Hangfire.Console.Tests.Stubs
{
    class DashboardContextStub : DashboardContext
    {
        public DashboardContextStub(
            DashboardOptions options, 
            DashboardRequest dashboardRequest,
            DashboardResponse dashboardResponse,
            JobStorage storage = null) : base(storage ?? new JobStorageStub(), options)
        {
            Request = dashboardRequest;
            Response = dashboardResponse;
        }
    }
}
