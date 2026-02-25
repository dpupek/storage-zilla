# CRCs

## AzureFilesSync.Desktop.ViewModels.MainViewModel
Responsibilities: UI orchestration, command handling, binding collections, mirror confirmation.
Collaborators: IAuthenticationService, IAzureDiscoveryService, ILocalBrowserService, IAzureFilesBrowserService, ITransferQueueService, IMirrorPlannerService, IMirrorExecutionService.

## AzureFilesSync.Infrastructure.Auth.InteractiveAuthenticationService
Responsibilities: interactive login, credential provisioning.
Collaborators: Azure.Identity InteractiveBrowserCredential.

## AzureFilesSync.Infrastructure.Azure.AzureDiscoveryService
Responsibilities: enumerate subscriptions, storage accounts, shares.
Collaborators: ArmClient, ShareServiceClient.

## AzureFilesSync.Infrastructure.Transfers.AzureFileTransferExecutor
Responsibilities: upload/download transfer execution, checkpoint updates.
Collaborators: ShareFileClient, ICheckpointStore.

## AzureFilesSync.Core.Services.TransferQueueService
Responsibilities: queue state machine, worker dispatch, status publication.
Collaborators: ITransferExecutor, ICheckpointStore.

## AzureFilesSync.Core.Services.MirrorPlannerService
Responsibilities: compare local and remote trees, produce mirror actions.
Collaborators: ILocalBrowserService, IAzureFilesBrowserService.
