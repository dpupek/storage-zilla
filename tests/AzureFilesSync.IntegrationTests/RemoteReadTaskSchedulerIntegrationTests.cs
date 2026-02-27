using AzureFilesSync.Infrastructure.Concurrency;

namespace AzureFilesSync.IntegrationTests;

public sealed class RemoteReadTaskSchedulerIntegrationTests
{
    [Fact]
    public async Task RunLatestAsync_WhenSecondOperationStarts_FirstOperationIsCanceled()
    {
        #region Arrange
        var scheduler = new RemoteReadTaskScheduler();
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCanceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstTask = scheduler.RunLatestAsync(
            async token =>
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
        var secondResult = await scheduler.RunLatestAsync(
            async token =>
            {
                await Task.Delay(10, token);
                return "second";
            },
            CancellationToken.None);
        #endregion

        #region Assert
        Assert.Equal("second", secondResult);
        Assert.True(await firstCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await firstTask);
        #endregion
    }

    [Fact]
    public async Task CancelCurrent_CancelsInFlightOperation()
    {
        #region Arrange
        var scheduler = new RemoteReadTaskScheduler();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var running = scheduler.RunLatestAsync(
            async token =>
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
        scheduler.CancelCurrent();
        #endregion

        #region Assert
        Assert.True(await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running);
        #endregion
    }
}
