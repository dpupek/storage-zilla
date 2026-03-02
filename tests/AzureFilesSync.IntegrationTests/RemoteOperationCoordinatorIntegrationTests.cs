using AzureFilesSync.Core.Models;
using AzureFilesSync.Infrastructure.Concurrency;

namespace AzureFilesSync.IntegrationTests;

public sealed class RemoteOperationCoordinatorIntegrationTests
{
    [Fact]
    public async Task RunLatestAsync_WhenSecondOperationStarts_FirstOperationIsCanceled()
    {
        #region Arrange
        var scheduler = new RemoteReadTaskScheduler();
        var coordinator = new RemoteOperationCoordinator(scheduler);
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCanceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstTask = coordinator.RunLatestAsync(
            RemoteOperationType.Browse,
            async (_, token) =>
            {
                firstStarted.TrySetResult(true);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                    return "first";
                }
                catch (OperationCanceledException)
                {
                    firstCanceled.TrySetResult(true);
                    throw;
                }
            },
            CancellationToken.None);
        #endregion

        #region Initial Assert
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        #endregion

        #region Act
        var secondResult = await coordinator.RunLatestAsync(
            RemoteOperationType.Search,
            async (_, token) =>
            {
                await Task.Delay(10, token);
                return "second";
            },
            CancellationToken.None);
        #endregion

        #region Assert
        Assert.Equal("second", secondResult);
        Assert.True(await firstCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(RemoteOperationCancelReason.ReplacedByLatest, coordinator.LastCancelReason);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await firstTask);
        #endregion
    }

    [Fact]
    public async Task CancelCurrent_TracksCancelReason_AndCancelsInFlightOperation()
    {
        #region Arrange
        var scheduler = new RemoteReadTaskScheduler();
        var coordinator = new RemoteOperationCoordinator(scheduler);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var running = coordinator.RunLatestAsync(
            RemoteOperationType.Search,
            async (_, token) =>
            {
                started.TrySetResult(true);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                    return 1;
                }
                catch (OperationCanceledException)
                {
                    canceled.TrySetResult(true);
                    throw;
                }
            },
            CancellationToken.None);
        #endregion

        #region Initial Assert
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        #endregion

        #region Act
        coordinator.CancelCurrent(RemoteOperationCancelReason.UserRequested);
        #endregion

        #region Assert
        Assert.True(await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(RemoteOperationCancelReason.UserRequested, coordinator.LastCancelReason);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running);
        #endregion
    }
}
