// Copyright (C) Microsoft Corporation. All Rights Reserved.

using BuildXL.Utilities.Core.Tasks;

namespace FastDownload.Utilities
{
    /// <summary>
    /// An async representation of an auto reset event or barrier. <see cref="Reset"/> will release all current waiters and any new calls will block until the next call to reset.
    /// </summary>
    public class AsyncBarrier : IDisposable
    {
        private TaskCompletionSource _barrier = CreateTaskCompletionSource();

        public Task WaitAsync(CancellationToken cancellationToken)
        {
            // Wait until the allowance is refreshed.
            if (cancellationToken == default)
            {
                return _barrier.Task;
            }

            return TaskUtilities.AwaitWithCancellationAsync(_barrier.Task, cancellationToken);
        }

        public void Reset()
        {
            var previousTask = Interlocked.Exchange(ref _barrier, CreateTaskCompletionSource());
            // See CreateTaskCompletionSource for rationale.
            previousTask.TrySetResult();
        }

        public void Dispose()
        {
            var nextTask = CreateTaskCompletionSource();
            // See CreateTaskCompletionSource for rationale.
            nextTask.SetCanceled();

            var previousTask = Interlocked.Exchange(ref _barrier, nextTask);
            // See CreateTaskCompletionSource for rationale.
            previousTask.TrySetCanceled();
        }

        private static TaskCompletionSource CreateTaskCompletionSource()
        {
            // REMARK: We use RunContinuationsAsynchronously to ensure that continuations are run on a thread pool
            // thread. This is important to prevent continuations (read the Acquire operations) from running on the
            // background thread that refills the allowance. This would lead to a deadlock.
            //
            // The main point is that we don't ever want to block BackgroundRefillCoreAsync.
            return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
