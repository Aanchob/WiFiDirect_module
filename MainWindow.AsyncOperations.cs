using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using direct_module.Network;

namespace direct_module
{
    public sealed partial class MainWindow
    {
        private readonly object _backgroundOperationsGate = new();
        private readonly HashSet<Task> _backgroundOperations = new();

        /// <summary>
        /// Starts an operation owned by the window and observes every exception it can
        /// produce. Event handlers use this instead of async-void continuations.
        /// </summary>
        private bool StartBackgroundOperation(
            Func<Task> operation,
            string context,
            ChatConnection? closeConnectionOnFailure = null)
        {
            ArgumentNullException.ThrowIfNull(operation);

            var registration = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_backgroundOperationsGate)
            {
                if (_shutdownStarted != 0)
                {
                    return false;
                }
                // Register a proxy before invoking user code. This closes the race
                // with shutdown without executing an arbitrary delegate while the
                // operations lock is held (the delegate may synchronously re-enter
                // StartBackgroundOperation before its first await).
                _backgroundOperations.Add(registration.Task);
            }

            Task task;
            try
            {
                task = operation() ?? throw new InvalidOperationException(
                    "The background operation returned no task.");
            }
            catch (OperationCanceledException) when (_windowLifetimeCancellation.IsCancellationRequested)
            {
                CompleteBackgroundOperationRegistration(registration);
                return false;
            }
            catch (Exception ex)
            {
                EnqueueLog($"{context}に失敗しました: {ex.Message}", LogLevel.Error);
                closeConnectionOnFailure?.Close();
                CompleteBackgroundOperationRegistration(registration);
                return false;
            }

            _ = ObserveBackgroundOperationAsync(
                task,
                registration,
                context,
                closeConnectionOnFailure);
            return true;
        }

        private async Task ObserveBackgroundOperationAsync(
            Task task,
            TaskCompletionSource<object?> registration,
            string context,
            ChatConnection? closeConnectionOnFailure)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException) when (_windowLifetimeCancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                EnqueueLog($"{context}に失敗しました: {ex.Message}", LogLevel.Error);
                closeConnectionOnFailure?.Close();
            }
            finally
            {
                CompleteBackgroundOperationRegistration(registration);
            }
        }

        private void CompleteBackgroundOperationRegistration(
            TaskCompletionSource<object?> registration)
        {
            registration.TrySetResult(null);
            lock (_backgroundOperationsGate)
            {
                _backgroundOperations.Remove(registration.Task);
            }
        }

        /// <summary>
        /// Moves a task-producing callback to the UI thread without creating an
        /// unobserved async lambda in DispatcherQueue.
        /// </summary>
        private bool TryEnqueueBackgroundOperation(
            Func<Task> operation,
            string context,
            ChatConnection? closeConnectionOnFailure = null,
            Action? onRejected = null)
        {
            if (Volatile.Read(ref _shutdownStarted) != 0)
            {
                onRejected?.Invoke();
                return false;
            }

            bool enqueued = DispatcherQueue.TryEnqueue(() =>
            {
                if (Volatile.Read(ref _shutdownStarted) != 0)
                {
                    onRejected?.Invoke();
                    return;
                }

                if (!StartBackgroundOperation(operation, context, closeConnectionOnFailure))
                {
                    // Shutdown can begin after the dispatcher callback passed the
                    // check above but before the operation is registered. Resource-
                    // owning callers (accepted sockets, queue slots, and so on) must
                    // still receive their rejection callback in that race.
                    onRejected?.Invoke();
                }
            });
            if (!enqueued)
            {
                onRejected?.Invoke();
            }
            return enqueued;
        }

        private async Task DrainBackgroundOperationsAsync()
        {
            while (true)
            {
                Task[] operations;
                lock (_backgroundOperationsGate)
                {
                    _backgroundOperations.RemoveWhere(operation => operation.IsCompleted);
                    operations = _backgroundOperations.ToArray();
                }

                if (operations.Length == 0)
                {
                    return;
                }

                try
                {
                    await Task.WhenAll(operations);
                }
                catch
                {
                    // Each operation is independently observed and logged above.
                }
            }
        }

        private bool TryBeginWindowShutdown()
        {
            lock (_backgroundOperationsGate)
            {
                if (_shutdownStarted != 0)
                {
                    return false;
                }

                Volatile.Write(ref _shutdownStarted, 1);
                return true;
            }
        }
    }
}
