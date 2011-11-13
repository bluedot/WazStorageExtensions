﻿using Microsoft.WindowsAzure.StorageClient;
using System;
using System.Threading;
using System.Net;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text;
using Microsoft.AzureCAT.Samples.TransientFaultHandling;
using Microsoft.AzureCAT.Samples.TransientFaultHandling.AzureStorage;
using TransientFaultHandlingAlias = Microsoft.AzureCAT.Samples.TransientFaultHandling;

namespace smarx.WazStorageExtensions
{
    public class AutoRenewLease : IDisposable
    {
        public static void DoOnce(CloudBlob blob, Action action)
        {
            DoOnce(blob, action, TimeSpan.FromSeconds(5));
        }

        public static void DoOnce(CloudBlob blob, Action action, TimeSpan pollingFrequency)
        {
            while (!blob.Exists() || blob.Metadata["progress"] != "done")
            {
                using (var arl = new AutoRenewLease(blob))
                {
                    if (arl.HasLease)
                    {
                        var policy = new TransientFaultHandlingAlias.RetryPolicy<StorageTransientErrorDetectionStrategy>(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
                        policy.ExecuteAction(() => {
                            action();
                            blob.Metadata["progress"] = "done";
                            blob.SetMetadata(arl.leaseId);
                        });
                    }
                    else
                    {
                        Thread.Sleep(pollingFrequency);
                    }
                }
            }
        }

        public bool HasLease
        {
            get
            {
                return leaseId != null;
            }
        }

        public AutoRenewLease(CloudBlob blob)
        {
            this.blob = blob;

            retryPolicy.ExecuteAction(() => {
                blob.Container.CreateIfNotExist();
            });
            
            try
            {
                retryPolicy.ExecuteAction(() => {
                    blob.UploadByteArray(new byte[0], new BlobRequestOptions { AccessCondition = AccessCondition.IfNoneMatch("*") });
                });
            }
            catch (StorageClientException e)
            {
                if (e.ErrorCode != StorageErrorCode.BlobAlreadyExists
                && e.StatusCode != HttpStatusCode.PreconditionFailed)
                {
                    throw;
                }
            }
            leaseId = blob.TryAcquireLease();
            if (HasLease)
            {
                StartRenewalTask(blob);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void StartRenewalTask(CloudBlob blob)
        {
            cancellationSource = new CancellationTokenSource();
            var cancellationToken = cancellationSource.Token;

            var renewalTask = Task.Factory.StartNew(() => {
                cancellationToken.ThrowIfCancellationRequested();

                while (! cancellationToken.IsCancellationRequested)
                {
                    cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(40));

                    retryPolicy.ExecuteAction(() => {
                        blob.RenewLease(leaseId);
                    });
                    var message = String.Format("Lease renewed for leaseId: '{0}'", leaseId);
                    Trace.WriteLine(message);

                    cancellationToken.ThrowIfCancellationRequested();
                }
            }, cancellationToken);

            renewalTask.ContinueWith(task => {
                task.Exception.Handle(inner => {
                    if (inner is OperationCanceledException)
                    {
                        Trace.WriteLine("RenewalTask was canceled");
                    }
                    else
                    {
                        var message = String.Format("RenewalTask encountered an error while attempting to renew lease on blob '{0}' for leaseId '{1}'", blob.Uri.ToString(), leaseId);
                        Trace.WriteLine(message);
                    }

                    return true;
                });
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private string UnwindException(Exception ex)
        {
            var current = ex;
            var builder = new StringBuilder();

            while (current != null)
            {
                builder.AppendLine(current.ToString());
                current = current.InnerException;
            }

            return builder.ToString();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposed)
            {
                if (disposing)
                {
                    if (cancellationSource != null)
                    {
                        try
                        {
                            retryPolicy.ExecuteAction(() => {
                                blob.ReleaseLease(leaseId);
                            });
                        }
                        catch (Exception ex)
                        {
                            var message = String.Format("ReleaseLease failed: {0}", UnwindException(ex));
                            Trace.WriteLine(message);
                        }

                        try
                        {
                            if (! cancellationSource.IsCancellationRequested) {
                                cancellationSource.Cancel();
                            }
                        }
                        catch (Exception ex)
                        {
                            var message = String.Format("CancellationSource.Cancel failed: {0}", UnwindException(ex));
                            Trace.WriteLine(message);
                        }
                    }
                }
                disposed = true;
            }
        }

        ~AutoRenewLease()
        {
            Dispose(false);
        }

        private TransientFaultHandlingAlias.RetryPolicy retryPolicy = new RetryPolicy<StorageTransientErrorDetectionStrategy>(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
        private CloudBlob blob;
        private CancellationTokenSource cancellationSource;
        private string leaseId;
        private bool disposed;
    }
}
