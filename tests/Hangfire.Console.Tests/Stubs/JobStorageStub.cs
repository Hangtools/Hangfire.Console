using System;
using Hangfire.Storage;

namespace Hangfire.Console.Tests.Stubs
{
    class JobStorageStub : JobStorage
    {
        public override IMonitoringApi GetMonitoringApi()
        {
            throw new NotImplementedException();
        }

        public override IStorageConnection GetConnection()
        {
            throw new NotImplementedException();
        }
    }
}