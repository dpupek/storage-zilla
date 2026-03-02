# CRCs

## AzureFilesSync.Desktop.ViewModels.MainViewModel
Responsibilities: UI orchestration, command handling, binding collections, mirror confirmation.
Collaborators: IAuthenticationService, IAzureDiscoveryService, ILocalBrowserService, IAzureFilesBrowserService, ITransferQueueService, IMirrorPlannerService, IMirrorExecutionService.

## AzureFilesSync.Infrastructure.Auth.InteractiveAuthenticationService
Responsibilities: interactive login, credential provisioning.
Collaborators: Azure.Identity InteractiveBrowserCredential.

## AzureFilesSync.Infrastructure.Azure.AzureDiscoveryService
Responsibilities: enumerate subscriptions, storage accounts, and unified remote roots (file shares + blob containers).
Collaborators: ArmClient, ShareServiceClient, BlobServiceClient.

## AzureFilesSync.Infrastructure.Transfers.AzureFileTransferExecutor
Responsibilities: upload/download transfer execution across provider kinds, checkpoint updates.
Collaborators: ShareFileClient, BlobClient, ICheckpointStore.

## AzureFilesSync.Infrastructure.Azure.AzureFilesBrowserService
Responsibilities: provider-aware remote browsing/paging and metadata lookups for Azure Files and Azure Blob roots.
Collaborators: ShareServiceClient, BlobServiceClient.

## AzureFilesSync.Core.Services.TransferQueueService
Responsibilities: queue state machine, worker dispatch, status publication, active transfer dedupe with provider-aware identity.
Collaborators: ITransferExecutor, ICheckpointStore.

## AzureFilesSync.Core.Services.MirrorPlannerService
Responsibilities: compare local and remote trees, produce mirror actions.
Collaborators: ILocalBrowserService, IAzureFilesBrowserService.
