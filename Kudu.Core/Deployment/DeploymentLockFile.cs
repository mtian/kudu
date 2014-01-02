using System.IO.Abstractions;
using Kudu.Core.Infrastructure;
using Kudu.Core.SourceControl;
using Kudu.Core.Tracing;

namespace Kudu.Core.Deployment
{
    public class DeploymentLockFile : LockFile
    {
        private readonly IRepository _repository;

        public DeploymentLockFile(string path, ITraceFactory traceFactory, IFileSystem fileSystem,
            IRepository repository) : base(path, traceFactory, fileSystem)
        {
            _repository = repository;
        }

        public new bool Lock()
        {
            bool success = base.Lock();
            
            if (success)
            {
                _repository.ClearLock();
            }

            return success;
        }
    }
}
