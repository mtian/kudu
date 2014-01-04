using System;
using System.Diagnostics;
using System.IO.Abstractions;
using System.Threading.Tasks;
using Kudu.Contracts.SourceControl;
using Kudu.Core.Infrastructure;
using Kudu.Core.SourceControl;
using Kudu.Core.Tracing;

namespace Kudu.Core.Deployment
{
    public class DeploymentLockFile : LockFile, IDisposable
    {
        private readonly IRepositoryFactory _repositoryFactory;

        public DeploymentLockFile(string path, ITraceFactory traceFactory, IFileSystem fileSystem,
            IRepositoryFactory repositoryFactory)
            : base(path, traceFactory, fileSystem)
        {
            _repositoryFactory = repositoryFactory;
        }

        protected override void OnLockAcquired()
        {
            while (!Debugger.IsAttached)
                ;
            var repository = _repositoryFactory.GetRepository();
            repository.ClearLock();
        }


        public void Dispose()
        {
            TerminateAsyncLocks();
        }
    }
}
